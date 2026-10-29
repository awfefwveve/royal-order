using System.Collections;
using System.Collections.Generic;
using System.Text;
using KnifeWar.Core;
using KnifeWar.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KnifeWar.Net
{
    /// <summary>
    /// 데디 서버 ↔ 백엔드.
    /// 등록 → 하트비트 → 경기 배정 대기 → 배정되면 경기 시작 (배정된 플레이어만 입장 허용)
    /// → 경기 끝나면 결과 보고 → 씬을 새로 불러 다음 배정 대기.
    /// 안전장치: 사람이 아무도 없는 경기·너무 긴 경기는 무효 처리하고 서버를 비운다 (서버가 영원히 묶이지 않게).
    /// </summary>
    public sealed class GameServerAgent : MonoBehaviour
    {
        MatchController _m;
        NetServer _server;
        BackendClient _api;
        string _region, _address;
        int _port;
        string _serverId;
        AssignmentDto _assignment;
        bool _reported;

        const float NoShowTimeoutSec = 90f;      // 배정 후 아무도 안 들어오면 무효
        const float EmptyTimeoutSec = 120f;      // 모두 나간 뒤 이 시간 동안 아무도 안 돌아오면 무효
        const float MaxMatchSeconds = 60f * 60f; // 어떤 경우에도 1시간 넘는 경기는 정리

        /// <summary>이 서버에서 끝난 경기들 (씬을 다시 불러도 유지). 끝난 경기로 재접속하려는 클라에게 "끝났음"을 알려준다.</summary>
        static readonly HashSet<string> EndedMatches = new HashSet<string>();

        public string ServerSecret => _api.ServerSecret;
        public string CurrentMatchId => _assignment?.matchId;

        public void Run(MatchController m, NetServer server, string backendUrl, string secret, string region, string address, int port)
        {
            _m = m;
            _server = server;
            _api = new BackendClient(backendUrl) { ServerSecret = secret };
            _region = region;
            _address = address;
            _port = port;
            server.Agent = this;
            NetInfo.Status = $"Registering with backend ({backendUrl}, region {region})";
            StartCoroutine(Loop());
        }

        IEnumerator Loop()
        {
            // 등록 (실패하면 재시도)
            while (_serverId == null)
            {
                string body = $"{{\"region\":\"{BackendClient.Escape(_region)}\",\"address\":\"{BackendClient.Escape(_address)}\",\"port\":{_port},\"version\":\"{NetProtocol.Version}\",\"tickRate\":{MatchController.TickRate}}}";
                yield return _api.Post("/server/register", body, (code, text) =>
                {
                    if (code == 200) _serverId = JsonUtility.FromJson<RegisterResponse>(text).serverId;
                    else Debug.LogWarning($"[KnifeWar] 백엔드 등록 실패 code={code} {text}");
                });
                if (_serverId == null) yield return new WaitForSecondsRealtime(3f);
            }
            Debug.Log($"[KnifeWar] 백엔드 등록 완료 serverId={_serverId} region={_region}");
            NetInfo.Status = $"Waiting for match (region {_region})";
            StartCoroutine(Heartbeat());

            // 배정 대기
            while (_assignment == null)
            {
                yield return _api.Post("/server/assignment", $"{{\"serverId\":\"{_serverId}\"}}", (code, text) =>
                {
                    if (code == 200)
                    {
                        var r = JsonUtility.FromJson<AssignmentResponse>(text);
                        if (r.assigned) _assignment = r.assignment;
                    }
                    else if (code == 404) _serverId = null;   // 백엔드가 재시작됨 → 다시 등록
                });
                if (_serverId == null) { StartCoroutine(Restart(0f)); yield break; }
                if (_assignment == null) yield return new WaitForSecondsRealtime(1f);
            }

            Debug.Log($"[KnifeWar] 경기 배정 match={_assignment.matchId} 인원={_assignment.players.Length}");
            _m.Begin(NetRole.Server, -1);
            foreach (var p in _assignment.players) _m.ReserveSlot(p.slot, p.name);
            NetInfo.Status = $"Match {_assignment.matchId}";

            // 경기 종료 대기 (+ 빈 경기·너무 긴 경기 감시)
            float started = Time.realtimeSinceStartup;
            bool anyoneJoined = false;
            string abandonReason = null;
            while (_m.Round.Phase != RoundPhase.MatchEnd)
            {
                float now = Time.realtimeSinceStartup;
                if (_server.HumanCount > 0) anyoneJoined = true;
                if (!anyoneJoined && now - started > NoShowTimeoutSec) abandonReason = "no players joined";
                else if (anyoneJoined && _server.HumanCount == 0 && now - _server.LastHumanSeenAt > EmptyTimeoutSec) abandonReason = "everyone left";
                else if (now - started > MaxMatchSeconds) abandonReason = "time limit";
                if (abandonReason != null) break;
                yield return null;
            }
            EndedMatches.Add(_assignment.matchId);

            if (abandonReason != null)
            {
                Debug.LogWarning($"[KnifeWar] 경기 무효 처리 match={_assignment.matchId} ({abandonReason})");
                yield return Release();
                yield return Restart(1f);
                yield break;
            }
            yield return Report();
            yield return Restart(8f);
        }

        /// <summary>경기를 결과 없이 정리 (MMR 변화 없음).</summary>
        IEnumerator Release()
        {
            string body = $"{{\"serverId\":\"{_serverId}\",\"matchId\":\"{_assignment?.matchId}\"}}";
            for (int attempt = 0; attempt < 3; attempt++)
            {
                bool ok = false;
                yield return _api.Post("/server/released", body, (code, text) => ok = code == 200);
                if (ok) yield break;
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        /// <summary>끝난 경기의 입장 토큰인지 (서명이 맞고 이 서버에서 끝난 경기).</summary>
        public bool IsEndedMatch(string token)
        {
            var c = JoinToken.Verify(token, _api.ServerSecret);
            return c != null && EndedMatches.Contains(c.Match);
        }

        IEnumerator Heartbeat()
        {
            while (true)
            {
                yield return new WaitForSecondsRealtime(5f);
                if (_serverId != null)
                    yield return _api.Post("/server/heartbeat",
                        $"{{\"serverId\":\"{_serverId}\",\"matchId\":\"{_assignment?.matchId}\",\"players\":{_server.HumanCount}}}", null);
            }
        }

        IEnumerator Report()
        {
            if (_reported || _assignment == null) yield break;
            _reported = true;
            var sb = new StringBuilder();
            sb.Append($"{{\"matchId\":\"{_assignment.matchId}\",\"winnerTeam\":{_m.Round.MatchWinner},\"players\":[");
            for (int i = 0; i < _assignment.players.Length; i++)
            {
                var p = _assignment.players[i];
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"accountId\":\"{BackendClient.Escape(p.accountId)}\",\"kills\":{_m.Kills[p.slot]},\"deaths\":{_m.Deaths[p.slot]}}}");
            }
            sb.Append("]}");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                bool ok = false;
                yield return _api.Post("/server/result", sb.ToString(), (code, text) =>
                {
                    ok = code == 200;
                    Debug.Log($"[KnifeWar] 결과 보고 code={code} {text}");
                });
                if (ok) yield break;
                yield return new WaitForSecondsRealtime(2f);
            }
        }

        IEnumerator Restart(float delay)
        {
            NetInfo.Status = "Preparing next match";
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        /// <summary>입장 토큰 확인: 이 서버에 배정된 경기·플레이어인지.</summary>
        public JoinToken.Claims ValidateJoin(string token)
        {
            if (_assignment == null) return null;
            var c = JoinToken.Verify(token, _api.ServerSecret);
            if (c == null || c.Match != _assignment.matchId) return null;
            foreach (var p in _assignment.players)
                if (p.accountId == c.Sub && p.slot == c.Slot) return c;
            return null;
        }
    }
}
