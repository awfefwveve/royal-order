using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace KnifeWar.Net
{
    /// <summary>백엔드 HTTP API (클라·게임 서버 공용). 코루틴 기반.</summary>
    public sealed class BackendClient
    {
        public string BaseUrl;
        public string Token;            // 클라 세션 토큰
        public string ServerSecret;     // 게임 서버용

        public BackendClient(string baseUrl) { BaseUrl = baseUrl.TrimEnd('/'); }

        public IEnumerator Get(string path, Action<long, string> done) => Send("GET", path, null, done);
        public IEnumerator Post(string path, string json, Action<long, string> done) => Send("POST", path, json ?? "{}", done);

        IEnumerator Send(string method, string path, string json, Action<long, string> done)
        {
            using var req = new UnityWebRequest(BaseUrl + path, method);
            if (json != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            req.timeout = 10;
            if (!string.IsNullOrEmpty(Token)) req.SetRequestHeader("Authorization", "Bearer " + Token);
            if (!string.IsNullOrEmpty(ServerSecret)) req.SetRequestHeader("X-Server-Secret", ServerSecret);
            yield return req.SendWebRequest();
            long code = req.result == UnityWebRequest.Result.ConnectionError ? 0 : req.responseCode;
            done?.Invoke(code, req.downloadHandler?.text ?? "");
        }

        /// <summary>HTTP 왕복 시간으로 리전 핑 측정 (3번 중 최소값).</summary>
        public static IEnumerator MeasurePing(string url, Action<int> done)
        {
            int best = -1;
            for (int i = 0; i < 3; i++)
            {
                var sw = Stopwatch.StartNew();
                using var req = UnityWebRequest.Get(url);
                req.timeout = 3;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                {
                    int ms = (int)sw.ElapsedMilliseconds;
                    if (best < 0 || ms < best) best = ms;
                }
            }
            done(best);
        }

        public static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        public static string PingsJson(Dictionary<string, int> pings)
        {
            var sb = new StringBuilder("{\"pings\":{");
            bool first = true;
            foreach (var kv in pings)
            {
                if (!first) sb.Append(',');
                sb.Append('"').Append(Escape(kv.Key)).Append("\":").Append(kv.Value);
                first = false;
            }
            return sb.Append("}}").ToString();
        }
    }

    // ---- JSON 형식 (JsonUtility용: 필드 이름 = JSON 키) ----

    [Serializable] public sealed class LoginResponse { public string token; public ProfileDto account; }
    [Serializable] public sealed class AccountDto { public string id; public string name; public string homeRegion; }
    [Serializable] public sealed class RegionDto { public string id; public string name; public string continent; public string pingUrl; public int freeServers; public int searching; }
    [Serializable] public sealed class RegionList { public RegionDto[] items; }
    [Serializable] public sealed class MatchDto { public string matchId; public string region; public string address; public int port; public string joinToken; public int slot; public int team; }
    [Serializable] public sealed class StatusResponse { public string state; public string status; public int waited; public bool rejoin; public bool party; public MatchDto match; }
    [Serializable] public sealed class PartyMemberDto { public string id; public string name; public bool leader; public bool me; }
    [Serializable] public sealed class PartyDto { public bool inParty; public string id; public string code; public string leaderId; public bool isLeader; public PartyMemberDto[] members; public string searchState; }
    [Serializable] public sealed class RegisterResponse { public string serverId; }
    [Serializable] public sealed class AssignedPlayerDto { public string accountId; public string name; public int slot; public int team; }
    [Serializable] public sealed class AssignmentDto { public string matchId; public string region; public AssignedPlayerDto[] players; }
    [Serializable] public sealed class AssignmentResponse { public bool assigned; public AssignmentDto assignment; }

    /// <summary>입장 토큰 검증 (게임 서버). 백엔드와 같은 ServerSecret으로 HMAC-SHA256 서명을 확인.</summary>
    public static class JoinToken
    {
        [Serializable]
        public sealed class Claims { public string Match; public string Sub; public string Name; public int Slot; public long Exp; }

        public static Claims Verify(string token, string serverSecret)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(serverSecret)) return null;
            int dot = token.IndexOf('.');
            if (dot <= 0) return null;
            string body = token.Substring(0, dot), sig = token.Substring(dot + 1);
            byte[] expected;
            using (var h = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(serverSecret)))
                expected = h.ComputeHash(Encoding.ASCII.GetBytes(body));
            byte[] given;
            try { given = UnB64(sig); } catch { return null; }
            if (given.Length != expected.Length) return null;
            int diff = 0;
            for (int i = 0; i < given.Length; i++) diff |= given[i] ^ expected[i];
            if (diff != 0) return null;
            Claims c;
            try { c = JsonUtility.FromJson<Claims>(Encoding.UTF8.GetString(UnB64(body))); } catch { return null; }
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return c != null && c.Exp > now ? c : null;
        }

        static byte[] UnB64(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }
    }
}
