using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KnifeWar.Backend;

/// <summary>
/// ASTREL 계정(회사 홈페이지 로그인 = Firebase Auth)의 ID 토큰 검증. 외부 라이브러리 없이 표준 암호화만 사용.
/// 구글 공개 인증서로 RS256 서명 확인 + aud/iss/exp/iat/sub 확인 (Firebase 공식 문서의 검증 절차 그대로).
/// </summary>
public sealed class FirebaseAuth
{
    const string CertUrl = "https://www.googleapis.com/robot/v1/metadata/x509/securetoken@system.gserviceaccount.com";
    readonly string _project;
    readonly HttpClient _http;
    Dictionary<string, RSA> _keys = new();
    DateTimeOffset _keysUntil;
    readonly SemaphoreSlim _lock = new(1, 1);

    public FirebaseAuth(string projectId, HttpClient http) { _project = projectId ?? ""; _http = http; }
    public bool Enabled => _project.Length > 0;

    public sealed class User
    {
        public string Uid = "", Email = "", Name = "";
        public bool EmailVerified;
    }

    public async Task<User> VerifyAsync(string idToken, CancellationToken ct)
    {
        if (!Enabled || string.IsNullOrEmpty(idToken) || idToken.Length > 4096) return null;
        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var head = JsonDocument.Parse(B64(parts[0]));
            if (head.RootElement.GetProperty("alg").GetString() != "RS256") return null;
            string kid = head.RootElement.GetProperty("kid").GetString() ?? "";
            var key = await KeyAsync(kid, ct);
            if (key == null) return null;
            if (!key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return null;

            using var body = JsonDocument.Parse(B64(parts[1]));
            var c = body.RootElement;
            long now = TokenService.Now();
            if (c.GetProperty("aud").GetString() != _project) return null;
            if (c.GetProperty("iss").GetString() != "https://securetoken.google.com/" + _project) return null;
            if (c.GetProperty("exp").GetInt64() < now - 30) return null;
            if (c.GetProperty("iat").GetInt64() > now + 300) return null;
            if (c.TryGetProperty("auth_time", out var at) && at.GetInt64() > now + 300) return null;
            string sub = c.GetProperty("sub").GetString() ?? "";
            if (sub.Length == 0 || sub.Length > 128) return null;
            return new User
            {
                Uid = sub,
                Email = c.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "",
                EmailVerified = c.TryGetProperty("email_verified", out var ev) && ev.ValueKind == JsonValueKind.True,
                Name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            };
        }
        catch { return null; }
    }

    async Task<RSA> KeyAsync(string kid, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _keysUntil && _keys.TryGetValue(kid, out var k)) return k;
        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < _keysUntil && _keys.TryGetValue(kid, out k)) return k;
            using var res = await _http.GetAsync(CertUrl, ct);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(ct);
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            var keys = new Dictionary<string, RSA>();
            foreach (var (id, pem) in map)
            {
                var cert = X509Certificate2.CreateFromPem(pem);
                var rsa = cert.GetRSAPublicKey();
                if (rsa != null) keys[id] = rsa;
            }
            var age = res.Headers.CacheControl?.MaxAge ?? TimeSpan.FromHours(1);
            _keys = keys;
            _keysUntil = DateTimeOffset.UtcNow + (age > TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : age);
            return _keys.TryGetValue(kid, out k) ? k : null;
        }
        finally { _lock.Release(); }
    }

    static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// 한 번 쓰고 버리는 짧은 코드 (2분). 홈페이지 → 런처 로그인, 런처 → 게임 실행에 쓴다.
/// 코드 자체는 저장하지 않고 해시만 들고 있다. 쓰면 바로 지워져서 다시 못 쓴다.
/// </summary>
public sealed class OneTimeCodes
{
    readonly Dictionary<string, (string account, int ver, long exp)> _codes = new();
    public int LifeSeconds = 120;

    public string Issue(string accountId, int tokenVersion)
    {
        string code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        long now = TokenService.Now();
        lock (_codes)
        {
            foreach (var k in _codes.Where(kv => kv.Value.exp < now).Select(kv => kv.Key).ToList()) _codes.Remove(k);
            if (_codes.Count > 50_000) return null;   // 비정상적으로 많으면 거부
            _codes[Hash(code)] = (accountId, tokenVersion, now + LifeSeconds);
        }
        return code;
    }

    /// <summary>맞으면 (계정 id, 토큰 버전), 아니면 null. 한 번 쓰면 끝.</summary>
    public (string account, int ver)? Redeem(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 100) return null;
        string h = Hash(code);
        lock (_codes)
        {
            if (!_codes.Remove(h, out var v) || v.exp < TokenService.Now()) return null;
            return (v.account, v.ver);
        }
    }

    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
