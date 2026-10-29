using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnifeWar.Backend;

/// <summary>
/// HMAC-SHA256 서명 토큰: base64url(json).base64url(sig)
/// - 세션 토큰: 클라 → 백엔드 API
/// - 입장 토큰: 클라 → 게임 서버 (게임 서버는 ServerSecret으로 검증, 백엔드에 묻지 않아도 됨)
/// </summary>
public sealed class TokenService
{
    readonly byte[] _sessionKey;
    readonly byte[] _joinKey;

    public TokenService(string tokenSecret, string serverSecret)
    {
        if (tokenSecret.Length < 32 || serverSecret.Length < 32)
            throw new InvalidOperationException("TokenSecret / ServerSecret은 32자 이상이어야 합니다 (appsettings.json).");
        _sessionKey = Encoding.UTF8.GetBytes(tokenSecret);
        _joinKey = Encoding.UTF8.GetBytes(serverSecret);
    }

    public sealed class SessionClaims
    {
        public string Sub { get; set; } = "";   // 계정 id
        public long Exp { get; set; }          // 유닉스 초
        public int Ver { get; set; }           // 계정 TokenVersion과 다르면 무효 (비밀번호 변경·전체 로그아웃)
    }

    public sealed class JoinClaims
    {
        public string Match { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Name { get; set; } = "";
        public int Slot { get; set; }
        public long Exp { get; set; }
    }

    public string IssueSession(string accountId, TimeSpan life, int version = 0) =>
        Sign(new SessionClaims { Sub = accountId, Exp = Now() + (long)life.TotalSeconds, Ver = version }, _sessionKey);

    public SessionClaims ValidateSession(string token)
    {
        var c = Verify<SessionClaims>(token, _sessionKey);
        return c != null && c.Exp > Now() ? c : null;
    }

    public string IssueJoin(string matchId, string accountId, string name, int slot, TimeSpan life) =>
        Sign(new JoinClaims { Match = matchId, Sub = accountId, Name = name, Slot = slot, Exp = Now() + (long)life.TotalSeconds }, _joinKey);

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    static string Sign<T>(T claims, byte[] key)
    {
        string body = B64(JsonSerializer.SerializeToUtf8Bytes(claims));
        string sig = B64(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
        return body + "." + sig;
    }

    static T Verify<T>(string token, byte[] key) where T : class
    {
        if (string.IsNullOrEmpty(token)) return null;
        int dot = token.IndexOf('.');
        if (dot <= 0) return null;
        string body = token[..dot], sig = token[(dot + 1)..];
        var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body));
        byte[] given;
        try { given = UnB64(sig); } catch { return null; }
        if (!CryptographicOperations.FixedTimeEquals(expected, given)) return null;
        try { return JsonSerializer.Deserialize<T>(UnB64(body)); } catch { return null; }
    }

    static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
