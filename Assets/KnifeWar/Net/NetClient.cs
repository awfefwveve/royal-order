using System;
using System.Collections.Generic;
using KnifeWar.Core;
using KnifeWar.Game;
using Unity.Networking.Transport;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>클라이언트 접속이 끝난 이유. NetBootstrap이 재접속할지 메뉴로 갈지 정한다.</summary>
    public enum ClientEnd
    {
        ConnectFailed,  // 처음부터 연결 안 됨 (서버 꺼짐·주소 오류·방화벽)
        Lost,           // 플레이 중 끊김 → 재접속 시도
        NotReady,       // 서버가 아직 경기 배정 전 → 잠시 후 재시도
        Rejected,       // 버전 다름·끝난 경기·자리 없음 → 재시도 안 함
        MatchOver,      // 경기 정상 종료
        Left,           // 스스로 나감
    }

    /// <summary>
    /// 클라이언트의 네트워크 부분.
    /// 접속 → 칸 배정받음(+현재 상태 전체) → 매 틱 입력 전송(최근 4개 중복) → 스냅샷·이벤트 받아 MatchController에 반영.
    /// 끊기면 Ended 이벤트로 이유를 알린다 (재접속은 NetBootstrap 담당).
    /// </summary>
    public sealed class NetClient : MonoBehaviour
    {
        const float ConnectTimeoutSec = 10f;
        const float UnstableAfterSec = 1.5f;     // 이 시간 동안 스냅샷이 없으면 "연결 불안정" 표시
        const float MatchOverLeaveSec = 7f;      // 경기 종료 화면을 보여준 뒤 메뉴로

        MatchController _m;
        NetTransport _net;
        NetworkConnection _conn;
        float _simLatencyMs;
        bool _bot;
        string _joinToken = "";
        bool _welcomed;
        bool _ended;
        float _connectStarted;
        float _lastRecv;
        float _matchEndSeenAt = -1f;
        RejectReason _rejectReason;
        bool _rejected;
        readonly ByteWriter _w = new ByteWriter();
        readonly List<NetCommand> _recent = new List<NetCommand>();
        readonly SnapshotFrame _frame = new SnapshotFrame();
        float _pingSmoothed = -1f;
        float _nextLog;

        public bool Connected => _welcomed && !_ended;
        public bool Unstable { get; private set; }
        public RejectReason LastReject => _rejectReason;
        public string Address { get; private set; }
        public ushort Port { get; private set; }

        /// <summary>접속이 끝남 (한 번만 호출).</summary>
        public event Action<NetClient, ClientEnd> Ended;

        public void Connect(MatchController m, string address, ushort port, float simLatencyMs, bool bot, string joinToken = "")
        {
            _joinToken = joinToken ?? "";
            _m = m;
            _simLatencyMs = simLatencyMs;
            _bot = bot;
            Address = address;
            Port = port;
            _net = new NetTransport();
            _connectStarted = Time.realtimeSinceStartup;
            if (!NetworkEndpoint.TryParse(address, port, out var ep))
            {
                NetInfo.Status = L.T("st_bad_address", address);
                Debug.LogError($"[KnifeWar] 잘못된 주소 {address}");
                Finish(ClientEnd.Rejected);
                return;
            }
            _conn = _net.Driver.Connect(ep);
            NetInfo.Status = L.T("st_connecting", address, port);
            Debug.Log($"[KnifeWar] 접속 시도 {address}:{port} 테스트지연={simLatencyMs}ms bot={bot}");
        }

        /// <summary>스스로 나가기: 서버에 알리고 끊는다 (서버는 바로 봇으로 전환).</summary>
        public void Leave()
        {
            if (_ended) return;
            if (_conn.IsCreated && _net != null)
            {
                _w.Clear();
                _w.Byte((byte)Msg.Bye);
                _net.Send(_conn, true, _w);
                _net.Flush();
                _net.Driver.ScheduleUpdate().Complete();
            }
            Finish(ClientEnd.Left);
        }

        void OnDestroy()
        {
            if (_m != null) _m.ClientCommandReady -= OnCommand;
            if (_conn.IsCreated && _net != null && _net.Driver.IsCreated) _net.Driver.Disconnect(_conn);
            _net?.Dispose();
        }

        void Finish(ClientEnd why)
        {
            if (_ended) return;
            _ended = true;
            if (_m != null) _m.ClientCommandReady -= OnCommand;
            if (_conn.IsCreated && _net != null && _net.Driver.IsCreated) _net.Driver.Disconnect(_conn);
            _conn = default;
            Debug.Log($"[KnifeWar] 접속 종료: {why}" + (_rejected ? $" ({_rejectReason})" : ""));
            Ended?.Invoke(this, why);
        }

        ClientEnd EndReason()
        {
            if (_matchEndSeenAt >= 0f) return ClientEnd.MatchOver;
            if (_rejected) return _rejectReason == RejectReason.NotReady ? ClientEnd.NotReady : ClientEnd.Rejected;
            return _welcomed ? ClientEnd.Lost : ClientEnd.ConnectFailed;
        }

        void Update()
        {
            if (_net == null || _ended) return;
            _net.Update();
            if (!_conn.IsCreated) return;

            NetworkEvent.Type type;
            while ((type = _net.Driver.PopEventForConnection(_conn, out var reader)) != NetworkEvent.Type.Empty)
            {
                switch (type)
                {
                    case NetworkEvent.Type.Connect:
                        Debug.Log("[KnifeWar] 서버 연결됨");
                        _w.Clear();
                        _w.Byte((byte)Msg.Hello);
                        _w.UShort(NetProtocol.Version);
                        _w.Float(_simLatencyMs);
                        _w.Str(_joinToken);
                        _net.Send(_conn, true, _w, _simLatencyMs);
                        break;
                    case NetworkEvent.Type.Data:
                        _lastRecv = Time.realtimeSinceStartup;
                        try { Handle(_net.Read(ref reader)); }
                        catch (Exception e) { Debug.LogWarning($"[KnifeWar] 잘못된 패킷 무시: {e.Message}"); }
                        if (_ended) return;
                        break;
                    case NetworkEvent.Type.Disconnect:
                        NetInfo.Status = L.T("st_disconnected");
                        Debug.LogWarning("[KnifeWar] 서버와 연결 끊김");
                        _conn = default;
                        Finish(EndReason());
                        return;
                }
            }

            float now = Time.realtimeSinceStartup;
            if (!_welcomed && now - _connectStarted > ConnectTimeoutSec)
            {
                NetInfo.Status = L.T("st_connect_failed");
                Finish(EndReason());
                return;
            }

            if (_welcomed)
            {
                Unstable = now - _lastRecv > UnstableAfterSec;
                NetInfo.Unstable = Unstable;

                // 경기 종료 → 결과를 잠깐 보여주고 스스로 나간다 (서버는 곧 다음 경기를 위해 재시작)
                if (_m.Round.Phase == RoundPhase.MatchEnd)
                {
                    if (_matchEndSeenAt < 0f) _matchEndSeenAt = now;
                    else if (now - _matchEndSeenAt > MatchOverLeaveSec)
                    {
                        _w.Clear();
                        _w.Byte((byte)Msg.Bye);
                        _net.Send(_conn, true, _w);
                        _net.Flush();
                        Finish(ClientEnd.MatchOver);
                        return;
                    }
                }

                if (now > _nextLog)
                {
                    _nextLog = now + 10f;
                    Debug.Log($"[KnifeWar] (client) ping={NetInfo.PingMs:0}ms corrections={_m.CorrectionCount} lastErr={_m.LastCorrectionError:0.000}m tick={_m.ServerTick}");
                }
            }
        }

        void Handle(ByteReader r)
        {
            var msg = (Msg)r.Byte();
            switch (msg)
            {
                case Msg.Welcome:
                {
                    if (_welcomed) return;
                    int slot = r.Byte();
                    r.Int();
                    int tickRate = r.UShort();
                    if (!_m.SetTickRate(tickRate))
                    {
                        Debug.LogError($"[KnifeWar] 지원하지 않는 서버 틱 {tickRate}");
                        _rejected = true;
                        _rejectReason = RejectReason.Version;
                        Finish(ClientEnd.Rejected);
                        return;
                    }
                    _welcomed = true;
                    _m.HumanIsBot = _bot;
                    _m.Begin(NetRole.Client, slot);
                    _m.ClientCommandReady += OnCommand;
                    NetInfo.Status = L.T("st_connected", slot + 1);
                    Debug.Log($"[KnifeWar] 접속 완료 slot={slot}");
                    break;
                }
                case Msg.Reject:
                    _rejected = true;
                    _rejectReason = (RejectReason)r.Byte();
                    Debug.LogWarning($"[KnifeWar] 입장 거부: {_rejectReason}");
                    Finish(EndReason());
                    break;
                case Msg.Full:   // 구버전 호환
                    _rejected = true;
                    _rejectReason = RejectReason.Full;
                    NetInfo.Status = L.T("st_full");
                    Finish(ClientEnd.Rejected);
                    break;
                case Msg.FullState:
                    if (_welcomed) ReadFullState(r);
                    break;
                case Msg.Roster:
                {
                    if (!_welcomed) return;
                    int n = r.Byte();
                    for (int p = 0; p < n; p++)
                    {
                        r.Byte();   // 칸 종류 (봇/사람) — 나중에 점수판 표시용
                        _m.ApplyRoster(p, r.Str());
                    }
                    break;
                }
                case Msg.Snapshot:
                    if (_welcomed) ReadSnapshot(r);
                    break;
                case Msg.Events:
                {
                    if (!_welcomed) return;
                    r.Int();
                    int n = r.UShort();
                    for (int i = 0; i < n; i++) _m.HandleEventLocal(NetProtocol.ReadEvent(r));
                    break;
                }
            }
        }

        /// <summary>입장·재접속 직후 서버의 현재 상태 (이후 변화는 이벤트로 순서대로 이어진다).</summary>
        void ReadFullState(ByteReader r)
        {
            r.Int();
            int n = r.Byte();
            for (int p = 0; p < n; p++)
            {
                int k = r.UShort(), d = r.UShort(), dmg = r.Int();
                if (p >= MatchController.MaxPlayers) continue;
                _m.Kills[p] = k;
                _m.Deaths[p] = d;
                _m.DamageDealt[p] = dmg;
            }
            _m.Monsters.Clear();
            int count = r.UShort();
            for (int i = 0; i < count; i++)
            {
                int slot = r.UShort();
                byte team = r.Byte(), type = r.Byte();
                int pos = r.Int();
                short lat = r.Short();
                byte state = r.Byte();
                _m.Monsters.PuppetSpawn(slot, team, type, pos, lat, state);
            }
            Debug.Log($"[KnifeWar] 현재 상태 수신: 몬스터 {count}마리");
        }

        void ReadSnapshot(ByteReader r)
        {
            int tick = r.Int();
            uint ack = r.UInt();
            float echoed = r.Float();
            var phase = (RoundPhase)r.Byte();
            int round = r.Byte();
            int phaseEnd = r.Int();
            int s0 = r.Byte(), s1 = r.Byte();
            int lastWinner = r.SByte(), matchWinner = r.SByte();
            int buff0 = r.Int(), buff1 = r.Int();
            var vel = new Vector3(r.Float(), r.Float(), r.Float());
            float mana = r.Float();
            _frame.Tick = tick;
            for (int p = 0; p < MatchController.MaxPlayers; p++) _frame.Players[p] = NetProtocol.ReadPlayer(r);

            float rtt = (Time.realtimeSinceStartup - echoed) * 1000f;
            if (rtt >= 0f && rtt < 5000f)
            {
                _pingSmoothed = _pingSmoothed < 0f ? rtt : Mathf.Lerp(_pingSmoothed, rtt, 0.1f);
                NetInfo.PingMs = _pingSmoothed;
            }

            _m.ClientReceiveSnapshot(_frame, ack, vel, mana, phase, round, phaseEnd, s0, s1, lastWinner, matchWinner, buff0, buff1);

            int corr = r.Byte();
            for (int k = 0; k < corr; k++)
            {
                int slot = r.UShort();
                int pos = r.Int();
                byte state = r.Byte();
                _m.Monsters.PuppetCorrect(slot, pos, state);
            }
        }

        /// <summary>매 틱 예측이 끝난 입력을 서버로 보낸다 (손실 대비 최근 4개를 같이).</summary>
        void OnCommand(NetCommand cmd)
        {
            if (_ended || !_conn.IsCreated) return;
            _recent.Add(cmd);
            if (_recent.Count > NetProtocol.MaxCommandsPerPacket) _recent.RemoveAt(0);
            _w.Clear();
            _w.Byte((byte)Msg.Commands);
            _w.Float(Time.realtimeSinceStartup);
            _w.Byte((byte)_recent.Count);
            for (int i = 0; i < _recent.Count; i++) NetProtocol.WriteCommand(_w, _recent[i]);
            _net.Send(_conn, false, _w, _simLatencyMs);
            _net.Flush();
        }
    }
}
