using System.Collections.Generic;
using KnifeWar.Core;
using KnifeWar.Game;
using Unity.Networking.Transport;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 데디 서버 / 호스트의 네트워크 부분.
    /// 접속 → 빈 칸 배정 → 입력 받아 MatchController에 넘김 → 매 틱 스냅샷·이벤트 전송.
    /// </summary>
    public sealed class NetServer : MonoBehaviour
    {
        sealed class Peer
        {
            public NetworkConnection Conn;
            public int Slot = -1;
            public float SimLatencyMs;
            public float LastClientTime;   // 핑 측정용: 클라가 보낸 시각을 그대로 돌려준다
            public float LastClientTimeRecvAt;
            public string AccountId;
            public bool Closed;
            public float ConnectedAt;
        }

        const int MaxPeers = 32;              // 입장 전 연결 포함 (무차별 접속 방지)
        const float HelloTimeoutSec = 10f;    // 연결만 하고 인사 안 하는 연결은 끊는다

        MatchController _m;
        NetTransport _net;
        readonly List<Peer> _peers = new List<Peer>();
        readonly ByteWriter _w = new ByteWriter();
        readonly ByteWriter _events = new ByteWriter();
        readonly PlayerNetState[] _states = new PlayerNetState[MatchController.MaxPlayers];
        int _correctionCursor;
        readonly List<int> _corrSlots = new List<int>(NetProtocol.CorrectionsPerSnapshot);   // 매 틱 재사용 (할당 없음)
        bool _rosterDirty;

        public int PeerCount => _peers.Count;
        /// <summary>칸을 배정받아 플레이 중인 원격 플레이어 수.</summary>
        public int HumanCount
        {
            get { int n = 0; foreach (var p in _peers) if (p.Slot >= 0 && !p.Closed) n++; return n; }
        }
        /// <summary>마지막으로 원격 플레이어가 한 명이라도 있었던 시각 (빈 경기 정리용).</summary>
        public float LastHumanSeenAt { get; private set; }
        public GameServerAgent Agent;   // 백엔드 연동 중이면 입장 토큰 필수

        public bool StartServer(MatchController m, ushort port)
        {
            _m = m;
            _net = new NetTransport();
            var ep = NetworkEndpoint.AnyIpv4.WithPort(port);
            if (_net.Driver.Bind(ep) != 0)
            {
                Debug.LogError($"[KnifeWar] 포트 {port} 열기 실패 (이미 사용 중일 수 있음)");
                return false;
            }
            _net.Driver.Listen();
            _m.ServerTickCompleted += OnServerTick;
            _m.RosterChanged += () => _rosterDirty = true;
            LastHumanSeenAt = Time.realtimeSinceStartup;
            Debug.Log($"[KnifeWar] 서버 시작 port={port}");
            NetInfo.Status = $"Server :{port}";
            return true;
        }

        void OnDestroy()
        {
            if (_m != null) _m.ServerTickCompleted -= OnServerTick;
            _net?.Dispose();
        }

        void Update()
        {
            if (_net == null) return;
            _net.Update();

            NetworkConnection c;
            while ((c = _net.Driver.Accept()) != default)
            {
                if (_peers.Count >= MaxPeers) { _net.Driver.Disconnect(c); continue; }
                _peers.Add(new Peer { Conn = c, ConnectedAt = Time.realtimeSinceStartup });
                Debug.Log("[KnifeWar] 연결 수락");
            }

            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                var peer = _peers[i];
                NetworkEvent.Type type;
                while ((type = _net.Driver.PopEventForConnection(peer.Conn, out var reader)) != NetworkEvent.Type.Empty)
                {
                    if (type == NetworkEvent.Type.Data)
                    {
                        try { Handle(peer, _net.Read(ref reader)); }
                        catch (System.Exception e) { Debug.LogWarning($"[KnifeWar] 잘못된 패킷 무시: {e.Message}"); }
                    }
                    else if (type == NetworkEvent.Type.Disconnect)
                    {
                        _m.ReleaseRemoteSlot(peer.Slot);
                        _peers.RemoveAt(i);
                        Debug.Log("[KnifeWar] 연결 끊김");
                        break;
                    }
                }
            }
            float now = Time.realtimeSinceStartup;
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                var peer = _peers[i];
                if (!peer.Closed && peer.Slot < 0 && now - peer.ConnectedAt > HelloTimeoutSec)
                {
                    _net.Driver.Disconnect(peer.Conn);
                    peer.Closed = true;
                }
            }
            if (HumanCount > 0) LastHumanSeenAt = now;
            for (int i = _peers.Count - 1; i >= 0; i--)
                if (_peers[i].Closed)
                {
                    _m.ReleaseRemoteSlot(_peers[i].Slot);
                    _peers.RemoveAt(i);
                }
            NetInfo.Status = $"Server · {_peers.Count} peers";
        }

        void Handle(Peer peer, ByteReader r)
        {
            var msg = (Msg)r.Byte();
            switch (msg)
            {
                case Msg.Hello:
                {
                    ushort version = r.UShort();
                    peer.SimLatencyMs = r.Float();
                    string token = r.Str();
                    string deckText = r.HasMore ? r.Str() : "";
                    if (version != NetProtocol.Version)
                    {
                        Debug.LogWarning($"[KnifeWar] 버전 불일치 client={version} server={NetProtocol.Version}");
                        Reject(peer, RejectReason.Version);
                        return;
                    }
                    if (peer.Slot >= 0) return;
                    if (_m.Role == NetRole.None)
                    {
                        // 아직 경기 배정 전 (또는 경기가 끝나 다음 경기를 기다리는 중)
                        Reject(peer, Agent != null && Agent.IsEndedMatch(token) ? RejectReason.BadToken : RejectReason.NotReady);
                        return;
                    }
                    int preferred = -1;
                    if (Agent != null)
                    {
                        var claims = Agent.ValidateJoin(token);
                        if (claims == null)
                        {
                            Debug.LogWarning("[KnifeWar] 입장 토큰 거부");
                            Reject(peer, RejectReason.BadToken);
                            return;
                        }
                        foreach (var other in _peers)
                            if (other != peer && other.AccountId == claims.Sub && other.Slot >= 0)
                            {
                                _m.ReleaseRemoteSlot(other.Slot);   // 같은 계정 재접속 → 이전 연결 정리
                                _net.Driver.Disconnect(other.Conn);
                                other.Slot = -1;
                                other.Closed = true;
                            }
                        peer.AccountId = claims.Sub;
                        preferred = claims.Slot;
                    }
                    peer.Slot = _m.AssignRemoteSlot(preferred);
                    if (peer.Slot < 0)
                    {
                        Reject(peer, RejectReason.Full);
                        return;
                    }
                    _m.Wizard.SetDeck(peer.Slot, CardDefs.Parse(deckText));   // 규칙에 안 맞으면 기본 덱
                    _w.Clear();
                    _w.Byte((byte)Msg.Welcome);
                    _w.Byte((byte)peer.Slot);
                    _w.Int(_m.ServerTick);
                    _w.UShort((ushort)MatchController.TickRate);
                    _net.Send(peer.Conn, true, _w, peer.SimLatencyMs);
                    SendFullState(peer);
                    _rosterDirty = true;
                    break;
                }
                case Msg.Bye:
                {
                    Debug.Log($"[KnifeWar] 플레이어가 나감 slot={peer.Slot}");
                    _net.Driver.Disconnect(peer.Conn);
                    peer.Closed = true;
                    break;
                }
                case Msg.Commands:
                {
                    if (peer.Slot < 0) return;
                    peer.LastClientTime = r.Float();
                    peer.LastClientTimeRecvAt = Time.realtimeSinceStartup;
                    int n = r.Byte();
                    for (int k = 0; k < n && k < NetProtocol.MaxCommandsPerPacket; k++)
                        _m.EnqueueCommand(peer.Slot, NetProtocol.ReadCommand(r));
                    break;
                }
            }
        }

        /// <summary>서버 틱이 끝날 때마다: 이벤트(신뢰) + 스냅샷(비신뢰) 전송.</summary>
        void OnServerTick()
        {
            if (_peers.Count == 0) return;
            if (_rosterDirty) SendRoster();

            // 이벤트 (모두 같은 내용)
            bool hasEvents = _m.TickEvents.Count > 0;
            if (hasEvents)
            {
                _events.Clear();
                _events.Byte((byte)Msg.Events);
                _events.Int(_m.ServerTick);
                _events.UShort((ushort)_m.TickEvents.Count);
                for (int i = 0; i < _m.TickEvents.Count; i++) NetProtocol.WriteEvent(_events, _m.TickEvents[i]);
            }

            // 플레이어 상태 (공통)
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                var a = _m.Avatars[p];
                _states[p] = new PlayerNetState
                {
                    Pos = a.transform.position,
                    Yaw = a.Yaw,
                    Pitch = a.Pitch,
                    Crouch = a.Crouch,
                    Alive = a.Alive,
                    Class = _m.Round.Class[p],
                    Pick = _m.Round.IsCandidate(p) ? PlayerClass.King : PlayerClass.Knight,   // 준비 단계 후보 표시
                    Vote = (sbyte)_m.Round.VoteOf(p),
                    Hp = _m.Combat.Hp[p],
                    TeleportSeq = a.TeleportSeq,
                };
            }

            // 몬스터 위치 보정 (돌아가며 몇 마리씩)
            var sim = _m.Monsters;
            var corrSlots = _corrSlots;
            corrSlots.Clear();
            for (int scanned = 0; scanned < sim.Capacity && corrSlots.Count < NetProtocol.CorrectionsPerSnapshot; scanned++)
            {
                _correctionCursor = (_correctionCursor + 1) % sim.Capacity;
                if (sim.Alive[_correctionCursor] != 0) corrSlots.Add(_correctionCursor);
            }

            var round = _m.Round;
            int tick = _m.ServerTick;
            for (int i = 0; i < _peers.Count; i++)
            {
                var peer = _peers[i];
                if (peer.Slot < 0) continue;

                if (hasEvents) _net.Send(peer.Conn, true, _events, peer.SimLatencyMs);

                _w.Clear();
                _w.Byte((byte)Msg.Snapshot);
                _w.Int(tick);
                _w.UInt(_m.LastProcessedSeq[peer.Slot]);
                // 핑: 클라 시각 + 서버가 붙잡고 있던 시간
                _w.Float(peer.LastClientTime + (Time.realtimeSinceStartup - peer.LastClientTimeRecvAt));
                _w.Byte((byte)round.Phase);
                _w.Byte((byte)round.RoundNumber);
                _w.Int(round.PhaseEndTick);
                _w.Byte((byte)round.Score[0]);
                _w.Byte((byte)round.Score[1]);
                _w.SByte((sbyte)round.LastRoundWinner);
                _w.SByte((sbyte)round.MatchWinner);
                WriteTeamKing(0);
                WriteTeamKing(1);
                var me = _m.Avatars[peer.Slot];
                _w.Float(me.Velocity.x);
                _w.Float(me.Velocity.y);
                _w.Float(me.Velocity.z);
                _w.Float(_m.Wizard.Mana(peer.Slot));
                var hand = _m.HandOf(peer.Slot);
                _w.Byte(hand.C0); _w.Byte(hand.C1); _w.Byte(hand.C2); _w.Byte(hand.C3); _w.Byte(hand.Next);
                for (int p = 0; p < MatchController.MaxPlayers; p++) NetProtocol.WritePlayer(_w, _states[p]);
                _w.Byte((byte)corrSlots.Count);
                for (int k = 0; k < corrSlots.Count; k++)
                {
                    int s = corrSlots[k];
                    _w.UShort((ushort)s);
                    _w.Int(sim.Pos[s]);
                    _w.Byte(sim.State[s]);
                }
                _net.Send(peer.Conn, false, _w, peer.SimLatencyMs);
            }
            _net.Flush();
        }

        /// <summary>거부 이유를 알려주고 끊는다 (신뢰 채널이 비워질 시간을 조금 준다).</summary>
        void Reject(Peer peer, RejectReason reason)
        {
            _w.Clear();
            _w.Byte((byte)Msg.Reject);
            _w.Byte((byte)reason);
            _net.Send(peer.Conn, true, _w);
            _net.Flush();
            StartCoroutine(DisconnectLater(peer, 0.5f));
        }

        System.Collections.IEnumerator DisconnectLater(Peer peer, float delay)
        {
            yield return new WaitForSecondsRealtime(delay);
            if (peer.Closed) yield break;
            _net.Driver.Disconnect(peer.Conn);
            peer.Closed = true;
        }

        /// <summary>입장·재접속 직후: 지금 살아 있는 몬스터 전체와 킬/데스 (이후 변화는 이벤트로 이어짐).</summary>
        void SendFullState(Peer peer)
        {
            var sim = _m.Monsters;
            _w.Clear();
            _w.Byte((byte)Msg.FullState);
            _w.Int(_m.ServerTick);
            _w.Byte(MatchController.MaxPlayers);
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                _w.UShort((ushort)Mathf.Clamp(_m.Kills[p], 0, 65535));
                _w.UShort((ushort)Mathf.Clamp(_m.Deaths[p], 0, 65535));
                _w.Int(_m.DamageDealt[p]);
            }
            int count = 0;
            for (int s = 0; s < sim.Capacity; s++) if (sim.Alive[s] != 0) count++;
            _w.UShort((ushort)count);
            for (int s = 0; s < sim.Capacity; s++)
            {
                if (sim.Alive[s] == 0) continue;
                _w.UShort((ushort)s);
                _w.Byte(sim.Team[s]);
                _w.Byte(sim.Type[s]);
                _w.Int(sim.Pos[s]);
                _w.Short(sim.Lat[s]);
                _w.Byte(sim.State[s]);
            }
            _net.Send(peer.Conn, true, _w, peer.SimLatencyMs);
        }

        void SendRoster()
        {
            _rosterDirty = false;
            _w.Clear();
            _w.Byte((byte)Msg.Roster);
            _w.Byte(MatchController.MaxPlayers);
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                _w.Byte((byte)_m.Slots[p]);
                _w.Str(_m.Avatars[p].DisplayName);
            }
            foreach (var peer in _peers)
                if (peer.Slot >= 0) _net.Send(peer.Conn, true, _w, peer.SimLatencyMs);
        }

        void WriteTeamKing(int team)
        {
            var t = _m.TeamKing(team);
            _w.Int(t.BuffEnd);
            _w.SByte((sbyte)t.FieldId);
            _w.Int(t.FieldEnd);
            _w.Byte(t.Barracks);
            _w.Byte(t.Wells);
        }
    }
}
