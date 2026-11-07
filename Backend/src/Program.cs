using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnifeWar.Backend;
using static KnifeWar.Backend.HttpServer;

// =====================================================================
// 황실 칼전 백엔드 (기본 .NET 런타임만 사용)
//   계정·로그인(스팀/개발용) · 리전별 매칭 · 게임 서버 등록/배정 · 경기 결과·MMR · 리더보드
//   클라 API: Authorization: Bearer <세션 토큰>
//   게임 서버 API: X-Server-Secret: <ServerSecret>
// =====================================================================

static void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");

string baseDir = AppContext.BaseDirectory;
string cfgPath = File.Exists("appsettings.json") ? "appsettings.json" : Path.Combine(baseDir, "appsettings.json");
var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
          ?? new AppConfig();
var opt = cfg.KnifeWar ?? new KnifeWarOptions();
// 배포 환경에서는 비밀값·주소를 환경 변수로 덮어쓴다 (설정 파일에 비밀값을 두지 않기)
string Env(string k) => Environment.GetEnvironmentVariable(k);
if (!string.IsNullOrEmpty(Env("KW_URLS"))) cfg.Urls = Env("KW_URLS");
if (!string.IsNullOrEmpty(Env("KW_TOKEN_SECRET"))) opt.TokenSecret = Env("KW_TOKEN_SECRET");
if (!string.IsNullOrEmpty(Env("KW_SERVER_SECRET"))) opt.ServerSecret = Env("KW_SERVER_SECRET");
if (!string.IsNullOrEmpty(Env("KW_DB"))) { opt.Storage = "postgres"; opt.ConnectionString = Env("KW_DB"); }
if (Env("KW_DEV_AUTH") == "0") opt.DevAuth = false;
if (!string.IsNullOrEmpty(Env("KW_ADMIN_KEY"))) opt.AdminKey = Env("KW_ADMIN_KEY");
if (!string.IsNullOrEmpty(Env("KW_CLIENT_VERSION"))) opt.Client.Version = Env("KW_CLIENT_VERSION");
if (!string.IsNullOrEmpty(Env("KW_PATCH_URL"))) opt.Client.PatchBaseUrl = Env("KW_PATCH_URL");
if (!string.IsNullOrEmpty(Env("KW_SMTP_HOST"))) opt.Smtp.Host = Env("KW_SMTP_HOST");
if (!string.IsNullOrEmpty(Env("KW_SMTP_USER"))) opt.Smtp.User = Env("KW_SMTP_USER");
if (!string.IsNullOrEmpty(Env("KW_SMTP_PASSWORD"))) opt.Smtp.Password = Env("KW_SMTP_PASSWORD");
if (!string.IsNullOrEmpty(Env("KW_SMTP_FROM"))) opt.Smtp.From = Env("KW_SMTP_FROM");
string dataDir = Path.IsPathRooted(opt.DataDir) ? opt.DataDir : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? baseDir, opt.DataDir);

IStore store;
if (opt.Storage == "postgres")
{
#if POSTGRES
    store = new PostgresStore(opt.ConnectionString);
#else
    throw new InvalidOperationException("PostgreSQL 저장소는 -p:UsePostgres=true 로 빌드해야 합니다.");
#endif
}
else store = new FileStore(dataDir);
var tokens = new TokenService(opt.TokenSecret, opt.ServerSecret);
var servers = new ServerRegistry();
var steam = new SteamAuth(opt.Steam, new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
var mm = new Matchmaker(opt, store, servers, tokens, Log);
var parties = new PartyService(mm);
var rooms = new RoomService(mm);
var serverSecret = Encoding.UTF8.GetBytes(opt.ServerSecret);
var mailer = new Mailer(opt.Smtp, Log);
var astrel = new FirebaseAuth(opt.Astrel.FirebaseProjectId, new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
var codes = new OneTimeCodes();
var presence = new Presence();
var accountLock = new object();   // 가입·이름 변경 (이메일·이름#태그 중복 방지)

var http = new HttpServer(cfg.Prefixes(), Log);
foreach (var o in opt.Astrel.WebOrigins) http.CorsOrigins.Add(o.TrimEnd('/'));
if (!string.IsNullOrEmpty(opt.Client.PatchDir))
    http.StaticDir = Path.IsPathRooted(opt.Client.PatchDir) ? opt.Client.PatchDir : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? baseDir, opt.Client.PatchDir);

Account Me(Req r)
{
    string h = r.Header("Authorization");
    if (!h.StartsWith("Bearer ")) return null;
    var c = tokens.ValidateSession(h[7..]);
    var a = c != null ? store.GetAccount(c.Sub) : null;
    if (a == null || c.Ver != a.Extra.TokenVersion || a.IsBanned(TokenService.Now())) return null;
    presence.Touch(a.Id);
    return a;
}

bool IsAdmin(Req r)
{
    if (string.IsNullOrEmpty(opt.AdminKey)) return false;
    var given = Encoding.UTF8.GetBytes(r.Header("X-Admin-Key"));
    var key = Encoding.UTF8.GetBytes(opt.AdminKey);
    return given.Length == key.Length && CryptographicOperations.FixedTimeEquals(given, key);
}

// 랭크 티어: 배치 5판 전에는 -1. 0 견습 ~ 6 왕의 검 (이름은 클라 다국어 표)
static int Tier(RegionStats st)
{
    if (st == null || st.Matches < 5) return -1;
    int[] min = { 0, 900, 1050, 1200, 1350, 1500, 1700 };
    int t = 0;
    for (int i = 0; i < min.Length; i++) if (st.Mmr >= min[i]) t = i;
    return t;
}

object Profile(Account a)
{
    a.Stats.TryGetValue(a.HomeRegion ?? "", out var st);
    return new
    {
        a.Id, a.Name, tag = a.Extra.Tag, displayId = a.DisplayId,
        email = a.Extra.Email, emailVerified = a.Extra.EmailVerified || a.Provider != "email",
        a.HomeRegion,
        mmr = st?.Mmr ?? 1000, tier = Tier(st), wins = st?.Wins ?? 0, losses = st?.Losses ?? 0, matches = st?.Matches ?? 0,
        kills = st?.Kills ?? 0, deaths = st?.Deaths ?? 0,
        a.Stats,
        termsOk = TermsOk(a),
        deck = a.Extra.Deck ?? "",
    };
}

bool TermsOk(Account a) =>
    a.Provider is not ("email" or "astrel") || string.IsNullOrEmpty(opt.Client.TermsVersion) || a.Extra.TermsVersion == opt.Client.TermsVersion;

void AgreeTerms(Account a)
{
    a.Extra.TermsVersion = opt.Client.TermsVersion;
    a.Extra.TermsAgreedAt = TokenService.Now();
}

static bool ValidEmail(string e) =>
    !string.IsNullOrWhiteSpace(e) && e.Length <= 254 && System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

string NewTag(string name)
{
    for (int i = 0; i < 50; i++)
    {
        var t = Passwords.Tag();
        if (!store.NameTagTaken(name, t)) return t;
    }
    return Guid.NewGuid().ToString("N")[..5];
}

bool IsGameServer(Req r)
{
    var given = Encoding.UTF8.GetBytes(r.Header("X-Server-Secret"));
    return given.Length == serverSecret.Length && CryptographicOperations.FixedTimeEquals(given, serverSecret);
}

static string CleanName(string n)
{
    n = (n ?? "").Trim();
    if (n.Length > 16) n = n[..16];
    return n.Length >= 2 ? n : "Player";
}

object Login(Account a) => new
{
    token = tokens.IssueSession(a.Id, TimeSpan.FromDays(30), a.Extra.TokenVersion),
    account = Profile(a),
};

Account Upsert(string provider, string externalId, string name)
{
    var a = store.FindAccount(provider, externalId);
    long now = TokenService.Now();
    if (a == null)
    {
        a = new Account { Id = Guid.NewGuid().ToString("N")[..16], Provider = provider, ExternalId = externalId, Name = CleanName(name), CreatedAt = now };
        a.Extra.Tag = NewTag(a.Name);
    }
    else if (!string.IsNullOrWhiteSpace(name) && CleanName(name) != a.Name) { a.Name = CleanName(name); a.Extra.Tag = NewTag(a.Name); }
    a.LastLoginAt = now;
    store.SaveAccount(a);
    return a;
}

// ---------------------------------------------------------------- 공통

http.Map("GET", "/", _ => Ok(new { service = "knifewar-backend", time = TokenService.Now() }));
http.Map("GET", "/ping", _ => Text("pong"));

// 게임 시작 시 확인: 최신 버전·패치 주소·공지·점검
http.Map("GET", "/client/version", _ => Ok(new
{
    version = opt.Client.Version,
    patchBaseUrl = opt.Client.PatchBaseUrl.TrimEnd('/'),
    notice = opt.Client.Notice,
    maintenance = opt.Client.Maintenance,
    termsVersion = opt.Client.TermsVersion,
    termsUrl = opt.Client.TermsUrl,
    privacyUrl = opt.Client.PrivacyUrl,
}));

// 클라 오류 보고: 날짜별 파일(dataDir/client-errors/yyyy-MM-dd.jsonl)에 한 줄씩 + 최근 200건은 메모리 (운영 API로 조회)
var clientErrors = new LinkedList<string>();
http.Map("POST", "/client/report", r =>
{
    if (string.IsNullOrEmpty(r.Body) || r.Body.Length > 64_000) return Status(400);
    try { System.Text.Json.JsonDocument.Parse(r.Body).Dispose(); } catch { return Status(400); }
    string line = $"{{\"at\":{TokenService.Now()},\"report\":{r.Body.Replace('\n', ' ')}}}";
    lock (clientErrors)
    {
        clientErrors.AddFirst(line);
        while (clientErrors.Count > 200) clientErrors.RemoveLast();
        try
        {
            var dir = Path.Combine(dataDir, "client-errors");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, DateTime.UtcNow.ToString("yyyy-MM-dd") + ".jsonl"), line + "\n");
        }
        catch (Exception e) { Log("client report save failed: " + e.Message); }
    }
    return Ok(new { ok = true });
});

http.Map("GET", "/admin/errors", r =>
{
    if (!IsAdmin(r)) return Status(401);
    string[] items;
    lock (clientErrors) items = clientErrors.ToArray();
    return Text("[" + string.Join(",", items) + "]");
});

// 운영: 점검 켜기/끄기, 공지, 최신 버전 바꾸기 (재시작 없이)
http.Map("POST", "/admin/client", r =>
{
    if (!IsAdmin(r)) return Status(401);
    var req = r.Json<ClientAdminReq>();
    if (req == null) return Status(400);
    if (req.Version != null) opt.Client.Version = req.Version;
    if (req.Notice != null) opt.Client.Notice = req.Notice;
    if (req.Maintenance.HasValue) opt.Client.Maintenance = req.Maintenance.Value;
    Log($"client settings version={opt.Client.Version} maintenance={opt.Client.Maintenance}");
    return Ok(opt.Client);
});

http.Map("GET", "/regions", _ => Ok(opt.Regions.Select(r => new
{
    r.Id, r.Name, r.Continent, r.PingUrl,
    freeServers = servers.FreeServers(r.Id),
    searching = mm.Searching(r.Id),
})));

// ---------------------------------------------------------------- 로그인

http.Map("POST", "/auth/dev", r =>
{
    if (!opt.DevAuth) return Status(403);
    // 개발용 로그인은 같은 컴퓨터에서만 (인터넷 → Caddy 를 거친 요청은 X-Forwarded-For 가 붙음)
    var ep = r.Raw.RemoteEndPoint;
    if (ep == null || !System.Net.IPAddress.IsLoopback(ep.Address) || !string.IsNullOrEmpty(r.Header("X-Forwarded-For"))) return Status(403);
    var req = r.Json<DevLogin>();
    if (req == null || string.IsNullOrWhiteSpace(req.DeviceId) || req.DeviceId.Length < 8) return Status(400, new { error = "deviceId" });
    return Ok(Login(Upsert("dev", req.DeviceId, req.Name)));
});

http.MapAsync("POST", "/auth/steam", async r =>
{
    if (!steam.Enabled) return Status(503);
    var req = r.Json<SteamLogin>();
    string steamId = req == null ? null : await steam.VerifyAsync(req.Ticket, CancellationToken.None);
    return steamId == null ? Status(401) : Ok(Login(Upsert("steam", steamId, req.Name)));
});

// ---------------------------------------------------------------- ASTREL 계정 (홈페이지·런처·게임 공통)
// 홈페이지(Firebase) 로그인 토큰 → 게임 계정. 같은 이메일로 만든 자체 계정이 있고 둘 다 인증됐으면 그 계정으로 이어짐.
http.MapAsync("POST", "/auth/astrel", async r =>
{
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    var req = r.Json<AstrelLogin>();
    var u = req == null ? null : await astrel.VerifyAsync(req.IdToken, CancellationToken.None);
    if (u == null) return Status(401);
    Account a;
    lock (accountLock)
    {
        a = store.FindAccount("astrel", u.Uid);
        if (a == null && u.EmailVerified && ValidEmail(u.Email))
        {
            var byEmail = store.FindAccount("email", u.Email.Trim().ToLowerInvariant());
            if (byEmail != null && byEmail.Extra.EmailVerified) a = byEmail;
        }
        long now = TokenService.Now();
        if (a == null)
        {
            string name = CleanName(string.IsNullOrWhiteSpace(u.Name) ? u.Email.Split('@')[0] : u.Name);
            a = new Account { Id = Guid.NewGuid().ToString("N")[..16], Provider = "astrel", ExternalId = u.Uid, Name = name, CreatedAt = now };
            a.Extra.Tag = NewTag(name);
            a.Extra.Email = u.Email;
            a.Extra.EmailVerified = u.EmailVerified;
            Log($"account created (astrel) {a.Id} {a.DisplayId}");
        }
        a.LastLoginAt = now;
        store.SaveAccount(a);
    }
    if (a.IsBanned(TokenService.Now())) return Status(403, new { error = "banned", reason = a.Extra.BanReason, until = a.Banned ? 0 : a.Extra.BanUntil });
    return Ok(Login(a));
});

// 로그인된 쪽이 한 번 쓰는 코드를 만듦 (2분): 홈페이지 → 런처, 런처 → 게임
http.Map("POST", "/auth/code", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    string code = codes.Issue(a.Id, a.Extra.TokenVersion);
    return code == null ? Status(503) : Ok(new { code });
});

// 코드 → 세션. 한 번 쓰면 끝, 비밀번호 변경·전체 로그아웃 뒤에는 무효.
http.Map("POST", "/auth/code/redeem", r =>
{
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    var req = r.Json<CodeReq>();
    var v = codes.Redeem(req?.Code);
    var a = v == null ? null : store.GetAccount(v.Value.account);
    if (a == null || a.Extra.TokenVersion != v.Value.ver) return Status(401);
    if (a.IsBanned(TokenService.Now())) return Status(403, new { error = "banned", reason = a.Extra.BanReason, until = a.Banned ? 0 : a.Extra.BanUntil });
    return Ok(Login(a));
});

// 세션 연장 (런처가 켤 때): 아직 유효하면 새 30일 토큰
http.Map("POST", "/auth/refresh", r =>
{
    var a = Me(r);
    return a == null ? Status(401) : Ok(Login(a));
});

http.Map("GET", "/me", r =>
{
    var a = Me(r);
    return a == null ? Status(401) : Ok(Profile(a));
});

// ---------------------------------------------------------------- 자체 계정 (이메일 + 비밀번호, 이름#태그)

http.Map("POST", "/auth/register", r =>
{
    var req = r.Json<RegisterReq>();
    if (req == null || !ValidEmail(req.Email)) return Status(400, new { error = "email" });
    if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 8 || req.Password.Length > 128) return Status(400, new { error = "password" });
    if (!string.IsNullOrEmpty(opt.Client.TermsVersion) && req.AgreedTerms != opt.Client.TermsVersion) return Status(400, new { error = "terms" });
    if (!req.Age14) return Status(400, new { error = "age" });
    string name = CleanName(req.Name);
    string norm = req.Email.Trim().ToLowerInvariant();
    Account a;
    lock (accountLock)
    {
        if (store.FindAccount("email", norm) != null) return Status(409, new { error = "email_taken" });
        long now = TokenService.Now();
        a = new Account { Id = Guid.NewGuid().ToString("N")[..16], Provider = "email", ExternalId = norm, Name = name, CreatedAt = now, LastLoginAt = now };
        a.Extra.Email = req.Email.Trim();
        a.Extra.PasswordHash = Passwords.Hash(req.Password);
        a.Extra.Tag = NewTag(name);
        a.Extra.VerifyCode = Passwords.Code();
        AgreeTerms(a);
        store.SaveAccount(a);
    }
    mailer.Send(a.Extra.Email, "ROYAL ORDER 이메일 인증 / Email verification", $"인증 코드 / Code: {a.Extra.VerifyCode}");
    Log($"account registered {a.Id} {a.DisplayId}");
    return Ok(Login(a));
});

http.Map("POST", "/auth/login", r =>
{
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    var req = r.Json<LoginReq>();
    if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrEmpty(req.Password)) return Status(400);
    var a = store.FindAccount("email", req.Email.Trim().ToLowerInvariant());
    long now = TokenService.Now();
    if (a == null) { Passwords.Verify(req.Password, "pbkdf2-sha256$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="); return Status(401, new { error = "wrong" }); }   // 없는 계정도 같은 시간 (계정 존재 여부 노출 방지)
    lock (accountLock)
    {
        if (a.Extra.LockedUntil > now) return Status(423, new { error = "locked", retryAfter = a.Extra.LockedUntil - now });
        if (!Passwords.Verify(req.Password, a.Extra.PasswordHash))
        {
            a.Extra.FailedLogins++;
            if (a.Extra.FailedLogins >= 10) { a.Extra.LockedUntil = now + 15 * 60; a.Extra.FailedLogins = 0; }
            store.SaveAccount(a);
            return Status(401, new { error = "wrong" });
        }
        a.Extra.FailedLogins = 0;
        a.LastLoginAt = now;
        store.SaveAccount(a);
    }
    if (a.IsBanned(now)) return Status(403, new { error = "banned", reason = a.Extra.BanReason, until = a.Banned ? 0 : a.Extra.BanUntil });
    return Ok(Login(a));
});

http.Map("POST", "/auth/verify", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<CodeReq>();
    if (req == null || string.IsNullOrEmpty(a.Extra.VerifyCode) || req.Code?.Trim() != a.Extra.VerifyCode) return Status(400, new { error = "code" });
    a.Extra.EmailVerified = true;
    a.Extra.VerifyCode = "";
    store.SaveAccount(a);
    return Ok(Profile(a));
});

http.Map("POST", "/auth/verify/resend", r =>
{
    var a = Me(r);
    if (a == null || a.Provider != "email" || a.Extra.EmailVerified) return Status(400);
    a.Extra.VerifyCode = Passwords.Code();
    store.SaveAccount(a);
    mailer.Send(a.Extra.Email, "ROYAL ORDER 이메일 인증 / Email verification", $"인증 코드 / Code: {a.Extra.VerifyCode}");
    return Ok();
});

// 비밀번호 재설정: 요청은 계정이 있든 없든 같은 응답 (계정 존재 여부 노출 방지)
http.Map("POST", "/auth/reset/request", r =>
{
    var req = r.Json<ResetRequestReq>();
    var a = req == null ? null : store.FindAccount("email", (req.Email ?? "").Trim().ToLowerInvariant());
    if (a != null)
    {
        a.Extra.ResetCode = Passwords.Code();
        a.Extra.ResetExpires = TokenService.Now() + 30 * 60;
        store.SaveAccount(a);
        mailer.Send(a.Extra.Email, "ROYAL ORDER 비밀번호 재설정 / Password reset", $"재설정 코드 (30분) / Reset code (30 min): {a.Extra.ResetCode}");
    }
    return Ok();
});

http.Map("POST", "/auth/reset", r =>
{
    var req = r.Json<ResetReq>();
    if (req == null || string.IsNullOrEmpty(req.Password) || req.Password.Length < 8) return Status(400, new { error = "password" });
    var a = store.FindAccount("email", (req.Email ?? "").Trim().ToLowerInvariant());
    long now = TokenService.Now();
    if (a == null || string.IsNullOrEmpty(a.Extra.ResetCode) || a.Extra.ResetExpires < now || req.Code?.Trim() != a.Extra.ResetCode)
        return Status(400, new { error = "code" });
    a.Extra.PasswordHash = Passwords.Hash(req.Password);
    a.Extra.ResetCode = "";
    a.Extra.TokenVersion++;          // 다른 기기 로그인 전부 끊기
    a.Extra.EmailVerified = true;    // 메일을 받았으니 인증된 것
    a.Extra.LockedUntil = 0;
    store.SaveAccount(a);
    return Ok(Login(a));
});

http.Map("POST", "/auth/logout-all", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    a.Extra.TokenVersion++;
    store.SaveAccount(a);
    return Ok();
});

// 약관이 바뀐 뒤 다시 동의
http.Map("POST", "/auth/terms", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<TermsReq>();
    if (req?.Version != opt.Client.TermsVersion) return Status(409, new { error = "terms_version", current = opt.Client.TermsVersion });
    lock (accountLock) { AgreeTerms(a); store.SaveAccount(a); }
    return Ok(Profile(a));
});

http.Map("POST", "/account/name", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<NameReq>();
    string name = CleanName(req?.Name);
    lock (accountLock)
    {
        if (name != a.Name)
        {
            a.Name = name;
            if (store.NameTagTaken(name, a.Extra.Tag)) a.Extra.Tag = NewTag(name);
            store.SaveAccount(a);
        }
    }
    return Ok(Profile(a));
});

// 왕 카드 덱 저장 (로비 덱 편집). 규칙에 안 맞으면 400.
http.Map("POST", "/account/deck", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<DeckReq>();
    string deck = req?.Deck ?? "";
    if (!DeckRules.IsValid(deck)) return Status(400, new { error = "bad_deck" });
    lock (accountLock) { a.Extra.Deck = DeckRules.Normalize(deck); store.SaveAccount(a); }
    return Ok(Profile(a));
});

// 최근 경기 (결과 화면·신고용)
http.Map("GET", "/me/matches", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var list = new List<object>();
    foreach (var id in a.Extra.RecentMatches.Take(10))
    {
        var m = store.GetMatch(id);
        if (m == null) continue;
        var mine = m.Players.FirstOrDefault(p => p.AccountId == a.Id);
        list.Add(new
        {
            m.Id, m.Region, m.CreatedAt, m.EndedAt, m.WinnerTeam,
            myTeam = mine?.Team ?? -1,
            mmrChange = mine == null ? 0 : mine.MmrAfter - mine.MmrBefore,
            players = m.Players.Select(p => new { id = p.AccountId, p.Name, p.Team, p.Kills, p.Deaths, me = p.AccountId == a.Id }),
        });
    }
    return Ok(list);
});

// 신고: 같은 경기에 있던 사람만, 경기당 대상당 1번
http.Map("POST", "/report", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<ReportReq>();
    string[] reasons = { "cheat", "abuse", "troll", "name", "afk" };
    if (req == null || !reasons.Contains(req.Reason) || req.AccountId == a.Id) return Status(400);
    var m = store.GetMatch(req.MatchId ?? "");
    if (m == null || m.Players.All(p => p.AccountId != a.Id) || m.Players.All(p => p.AccountId != req.AccountId)) return Status(400, new { error = "not_in_match" });
    if (store.HasReport(a.Id, req.AccountId, m.Id)) return Ok(new { duplicate = true });
    store.SaveReport(new Report
    {
        Id = Guid.NewGuid().ToString("N")[..16], ReporterId = a.Id, TargetId = req.AccountId, MatchId = m.Id,
        Reason = req.Reason, Note = (req.Note ?? "").Length > 300 ? req.Note[..300] : (req.Note ?? ""), CreatedAt = TokenService.Now(),
    });
    Log($"report {a.Id} -> {req.AccountId} ({req.Reason}) match={m.Id}");
    return Ok();
});

// ---------------------------------------------------------------- 운영 (X-Admin-Key)

http.Map("GET", "/admin/reports", r =>
{
    if (!IsAdmin(r)) return Status(401);
    return Ok(store.Reports(r.Raw.QueryString["all"] != "1", 200).Select(rp => new
    {
        rp.Id, rp.Reason, rp.Note, rp.MatchId, rp.CreatedAt, rp.Handled,
        reporter = store.GetAccount(rp.ReporterId)?.DisplayId, rp.ReporterId,
        target = store.GetAccount(rp.TargetId)?.DisplayId, rp.TargetId,
    }));
});

http.Map("POST", "/admin/reports/handled", r =>
{
    if (!IsAdmin(r)) return Status(401);
    var req = r.Json<IdReq>();
    var rp = req == null ? null : store.Reports(false, 20000).FirstOrDefault(x => x.Id == req.Id);
    if (rp == null) return Status(404);
    rp.Handled = true;
    store.SaveReport(rp);
    return Ok();
});

http.Map("POST", "/admin/ban", r =>
{
    if (!IsAdmin(r)) return Status(401);
    var req = r.Json<BanReq>();
    var a = req == null ? null : store.GetAccount(req.AccountId);
    if (a == null) return Status(404);
    if (req.Hours <= 0) a.Banned = true; else a.Extra.BanUntil = TokenService.Now() + req.Hours * 3600L;
    a.Extra.BanReason = req.Reason ?? "";
    a.Extra.TokenVersion++;   // 바로 로그아웃
    store.SaveAccount(a);
    mm.Leave(a.Id);
    Log($"ban {a.Id} {a.DisplayId} hours={req.Hours} reason={req.Reason}");
    return Ok();
});

http.Map("POST", "/admin/unban", r =>
{
    if (!IsAdmin(r)) return Status(401);
    var req = r.Json<IdReq>();
    var a = req == null ? null : store.GetAccount(req.Id);
    if (a == null) return Status(404);
    a.Banned = false;
    a.Extra.BanUntil = 0;
    a.Extra.BanReason = "";
    store.SaveAccount(a);
    return Ok();
});

http.Map("GET", "/admin/account/{id}", r =>
{
    if (!IsAdmin(r)) return Status(401);
    var a = store.GetAccount(r.Params["id"]);
    return a == null ? Status(404) : Ok(new { profile = Profile(a), a.Provider, a.Banned, a.Extra.BanUntil, a.Extra.BanReason, a.CreatedAt, a.LastLoginAt });
});

// ---------------------------------------------------------------- 매칭

http.Map("POST", "/mm/join", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    if (opt.RequireEmailVerification && a.Provider == "email" && !a.Extra.EmailVerified) return Status(403, new { error = "verify_email" });
    if (!TermsOk(a)) return Status(403, new { error = "terms" });
    var req = r.Json<JoinQueue>();
    var best = req?.Pings?.OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
    if (best != null && a.HomeRegion != best) { a.HomeRegion = best; store.SaveAccount(a); }

    Matchmaker.Ticket t;
    var party = parties.Get(a.Id);
    if (party != null && party.Members.Count > 1)
    {
        // 파티: 파티장만 시작할 수 있고, 전원이 한 묶음으로 들어간다
        var existing = mm.Status(a.Id);
        if (party.LeaderId != a.Id)
            return existing != null && existing.State == Matchmaker.TicketState.Matched ? Ok(new { ticket = existing.Id }) : Status(403, new { error = "leader_only" });
        parties.SetPings(a.Id, req?.Pings);
        var members = new List<(Account, Dictionary<string, int>)>();
        foreach (var m in party.Members.OrderBy(m => m.AccountId == party.LeaderId ? 0 : 1))
        {
            var acc = store.GetAccount(m.AccountId);
            if (acc != null) members.Add((acc, m.AccountId == a.Id ? req?.Pings : m.Pings));
        }
        t = mm.JoinGroup(members, party.Id);
        if (t == null) return Status(400, new { error = "party_too_big" });
    }
    else t = mm.Join(a, req?.Pings);
    return Ok(new { ticket = t.Id });
});

http.Map("POST", "/mm/leave", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    mm.Leave(a.Id);
    return Ok();
});

http.Map("GET", "/mm/status", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var t = mm.Status(a.Id);
    if (t == null) return Ok(new { state = "none" });
    return Ok(new
    {
        state = t.State.ToString().ToLowerInvariant(),
        status = t.Status,
        waited = TokenService.Now() - t.CreatedAt,
        rejoin = t.State == Matchmaker.TicketState.Matched && TokenService.Now() - t.MatchedAt > 15,   // 진행 중 경기로 복귀
        party = t.Size > 1,
        match = t.State == Matchmaker.TicketState.Matched ? mm.ResultFor(t, a.Id) : null,
    });
});

// ---------------------------------------------------------------- 파티

object PartyView(PartyService.Party p, Account me)
{
    var t = mm.Status(me.Id);
    bool partyTicket = t != null && t.PartyId == p.Id;
    return new
    {
        inParty = true,
        p.Id, p.Code, p.LeaderId,
        isLeader = p.LeaderId == me.Id,
        members = p.Members.Select(m => new { id = m.AccountId, m.Name, leader = m.AccountId == p.LeaderId, me = m.AccountId == me.Id }),
        searchState = partyTicket ? t.State.ToString().ToLowerInvariant() : "none",
    };
}

http.Map("GET", "/party", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var p = parties.Get(a.Id);
    return p == null ? Ok(new { inParty = false }) : Ok(PartyView(p, a));
});

http.Map("POST", "/party/create", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    rooms.Leave(a.Id);
    return Ok(PartyView(parties.Create(a), a));
});

http.Map("POST", "/party/join", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<PartyJoin>();
    var (p, error) = parties.Join(a, req?.Code);
    if (p != null) rooms.Leave(a.Id);
    return p == null ? Status(error == "not_found" ? 404 : 409, new { error }) : Ok(PartyView(p, a));
});

http.Map("POST", "/party/leave", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    parties.Leave(a.Id);
    return Ok(new { inParty = false });
});

http.Map("POST", "/party/kick", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<PartyKick>();
    return req != null && parties.Kick(a.Id, req.AccountId) ? Ok() : Status(403);
});

http.Map("POST", "/party/pings", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    parties.SetPings(a.Id, r.Json<JoinQueue>()?.Pings);
    return Ok();
});

// ---------------------------------------------------------------- 친구

string FriendState(string id)
{
    if (!presence.Online(id)) return "offline";
    var t = mm.Status(id);
    if (t != null && t.State == Matchmaker.TicketState.Matched) return "in_match";
    if (t != null && t.State == Matchmaker.TicketState.Searching) return "searching";
    return "online";
}

object Brief(string id)
{
    var f = store.GetAccount(id);
    return f == null ? null : new { id = f.Id, displayId = f.DisplayId, state = FriendState(f.Id) };
}

http.Map("GET", "/friends", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    return Ok(new
    {
        friends = a.Extra.Friends.Select(Brief).Where(x => x != null),
        incoming = a.Extra.FriendRequestsIn.Select(Brief).Where(x => x != null),
        outgoing = a.Extra.FriendRequestsOut.Select(Brief).Where(x => x != null),
        invites = presence.Invites(a.Id).Select(i => new { i.Id, from = i.FromName, i.Kind, i.Code }),
    });
});

// 이름#태그로 친구 요청. 상대가 이미 나에게 요청했으면 바로 친구.
http.Map("POST", "/friends/request", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<FriendAdd>();
    string did = (req?.DisplayId ?? "").Trim();
    int hash = did.LastIndexOf('#');
    if (hash <= 0 || hash == did.Length - 1 || did.Length > 40) return Status(400, new { error = "format" });
    var b = store.FindByNameTag(did[..hash].Trim(), did[(hash + 1)..].Trim());
    if (b == null || b.Id == a.Id) return Status(404, new { error = "not_found" });
    lock (accountLock)
    {
        a = store.GetAccount(a.Id); b = store.GetAccount(b.Id);
        if (a.Extra.Friends.Contains(b.Id)) return Ok(new { state = "friends" });
        if (a.Extra.Friends.Count >= 200) return Status(409, new { error = "limit" });
        if (a.Extra.FriendRequestsIn.Contains(b.Id))
        {
            a.Extra.FriendRequestsIn.Remove(b.Id); b.Extra.FriendRequestsOut.Remove(a.Id);
            a.Extra.Friends.Add(b.Id); b.Extra.Friends.Add(a.Id);
            store.SaveAccount(a); store.SaveAccount(b);
            return Ok(new { state = "friends" });
        }
        if (b.Extra.FriendRequestsIn.Count >= 100) return Status(409, new { error = "limit" });
        if (!b.Extra.FriendRequestsIn.Contains(a.Id)) b.Extra.FriendRequestsIn.Add(a.Id);
        if (!a.Extra.FriendRequestsOut.Contains(b.Id)) a.Extra.FriendRequestsOut.Add(b.Id);
        store.SaveAccount(a); store.SaveAccount(b);
    }
    return Ok(new { state = "requested" });
});

// 수락 / 거절·취소 / 친구 삭제 — 모두 상대 id 하나로
http.Map("POST", "/friends/accept", r =>
{
    var a = Me(r);
    var id = r.Json<FriendRef>()?.Id;
    if (a == null) return Status(401);
    lock (accountLock)
    {
        a = store.GetAccount(a.Id);
        var b = id == null ? null : store.GetAccount(id);
        if (b == null || !a.Extra.FriendRequestsIn.Contains(b.Id)) return Status(404);
        a.Extra.FriendRequestsIn.Remove(b.Id); b.Extra.FriendRequestsOut.Remove(a.Id);
        if (!a.Extra.Friends.Contains(b.Id)) a.Extra.Friends.Add(b.Id);
        if (!b.Extra.Friends.Contains(a.Id)) b.Extra.Friends.Add(a.Id);
        store.SaveAccount(a); store.SaveAccount(b);
    }
    return Ok();
});

http.Map("POST", "/friends/remove", r =>
{
    var a = Me(r);
    var id = r.Json<FriendRef>()?.Id;
    if (a == null) return Status(401);
    lock (accountLock)
    {
        a = store.GetAccount(a.Id);
        var b = id == null ? null : store.GetAccount(id);
        if (b == null) return Status(404);
        foreach (var (x, y) in new[] { (a, b), (b, a) })
        {
            x.Extra.Friends.Remove(y.Id); x.Extra.FriendRequestsIn.Remove(y.Id); x.Extra.FriendRequestsOut.Remove(y.Id);
        }
        store.SaveAccount(a); store.SaveAccount(b);
    }
    return Ok();
});

// 친구를 내 파티나 방으로 초대 (2분). 받는 쪽은 /friends 의 invites 로 받고, 코드로 들어온다.
http.Map("POST", "/friends/invite", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<FriendInvite>();
    if (req == null || !a.Extra.Friends.Contains(req.Id ?? "")) return Status(403, new { error = "not_friend" });
    string code;
    if (req.Kind == "room") code = rooms.Get(a.Id)?.Code;
    else if (req.Kind == "party") code = (parties.Get(a.Id) ?? parties.Create(a)).Code;
    else return Status(400);
    if (code == null) return Status(409, new { error = "no_room" });
    presence.AddInvite(req.Id, new Presence.Invite
    {
        Id = Guid.NewGuid().ToString("N")[..10], FromId = a.Id, FromName = a.DisplayId, Kind = req.Kind, Code = code,
        Expires = TokenService.Now() + 120,
    });
    return Ok(new { code });
});

http.Map("POST", "/friends/invite/dismiss", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    presence.RemoveInvite(a.Id, r.Json<FriendRef>()?.Id ?? "");
    return Ok();
});

// ---------------------------------------------------------------- 사용자 설정 대전 (사설방)

object RoomView(RoomService.Room room, Account me)
{
    var t = mm.Status(me.Id);
    var match = room.MatchId != null && t != null && t.State == Matchmaker.TicketState.Matched && t.MatchId == room.MatchId;
    return new
    {
        inRoom = true,
        room.Code,
        isHost = room.HostId == me.Id,
        started = match,
        seats = room.Seats.Select((s, i) => s == null
            ? new { slot = i, id = "", name = "", host = false, me = false }
            : new { slot = i, id = s.AccountId, name = s.Name, host = s.AccountId == room.HostId, me = s.AccountId == me.Id }),
    };
}

http.Map("GET", "/room", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var room = rooms.Get(a.Id);
    return room == null ? Ok(new { inRoom = false }) : Ok(RoomView(room, a));
});

http.Map("POST", "/room/create", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    if (!TermsOk(a)) return Status(403, new { error = "terms" });
    parties.Leave(a.Id);
    mm.Leave(a.Id);
    return Ok(RoomView(rooms.Create(a, r.Json<JoinQueue>()?.Pings), a));
});

http.Map("POST", "/room/join", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    if (!TermsOk(a)) return Status(403, new { error = "terms" });
    var req = r.Json<RoomJoin>();
    var (room, error) = rooms.Join(a, req?.Code, req?.Pings);
    if (room == null) return Status(error == "not_found" ? 404 : 409, new { error });
    parties.Leave(a.Id);
    mm.Leave(a.Id);
    return Ok(RoomView(room, a));
});

http.Map("POST", "/room/move", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    var req = r.Json<RoomMove>();
    return req != null && rooms.Move(a.Id, req.Slot) ? Ok(RoomView(rooms.Get(a.Id), a)) : Status(409);
});

http.Map("POST", "/room/kick", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    return rooms.Kick(a.Id, r.Json<FriendRef>()?.Id ?? "") ? Ok() : Status(403);
});

http.Map("POST", "/room/leave", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    rooms.Leave(a.Id);
    return Ok(new { inRoom = false });
});

http.Map("POST", "/room/start", r =>
{
    var a = Me(r);
    if (a == null) return Status(401);
    if (opt.Client.Maintenance) return Status(503, new { error = "maintenance", notice = opt.Client.Notice });
    string error = rooms.Start(a.Id, store.GetAccount);
    return error == null ? Ok(RoomView(rooms.Get(a.Id), a)) : Status(error == "host_only" ? 403 : 409, new { error });
});

// ---------------------------------------------------------------- 리더보드

http.Map("GET", "/leaderboard/{region}", r =>
{
    string region = r.Params["region"];
    return Ok(store.TopAccounts(region, 100).Select((a, i) => new
    {
        rank = i + 1, a.Name, displayId = a.DisplayId, mmr = a.Stats[region].Mmr, tier = Tier(a.Stats[region]), wins = a.Stats[region].Wins, matches = a.Stats[region].Matches,
    }));
});

// ---------------------------------------------------------------- 게임 서버

http.Map("POST", "/server/register", r =>
{
    if (!IsGameServer(r)) return Status(401);
    var req = r.Json<RegisterServer>();
    if (req == null || opt.Regions.All(x => x.Id != req.Region)) return Status(400, new { error = "unknown region" });
    var s = servers.Register(req.Region, req.Address, req.Port, req.Version ?? "");
    Log($"server registered {s.Id} {s.Region} {s.Address}:{s.Port}");
    return Ok(new { serverId = s.Id });
});

http.Map("POST", "/server/heartbeat", r =>
{
    if (!IsGameServer(r)) return Status(401);
    var req = r.Json<ServerHeartbeat>();
    return req != null && servers.Heartbeat(req.ServerId, req.Players) ? Ok() : Status(404);
});

http.Map("POST", "/server/assignment", r =>
{
    if (!IsGameServer(r)) return Status(401);
    var req = r.Json<ServerRef>();
    if (req == null || servers.Get(req.ServerId) == null) return Status(404);
    var a = servers.TakeAssignment(req.ServerId);
    return a == null ? Ok(new { assigned = false }) : Ok(new { assigned = true, assignment = a });
});

var resultLock = new object();   // 같은 결과가 재전송돼도 한 번만 반영

http.Map("POST", "/server/result", r =>
{
    if (!IsGameServer(r)) return Status(401);
    var req = r.Json<MatchResultReq>();
    lock (resultLock)
    {
    var m = req == null ? null : store.GetMatch(req.MatchId);
    if (m == null) return Status(404);
    if (m.EndedAt != 0) return Ok(new { duplicate = true });

    m.EndedAt = TokenService.Now();
    m.WinnerTeam = req.WinnerTeam;
    foreach (var p in m.Players)
    {
        var pr = req.Players?.FirstOrDefault(x => x.AccountId == p.AccountId);
        if (pr != null) { p.Kills = pr.Kills; p.Deaths = pr.Deaths; }
    }

    if (m.Custom)
    {
        // 사용자 설정 대전: MMR·전적 반영 없음 (최근 경기 목록에만)
        foreach (var p in m.Players)
        {
            p.MmrAfter = p.MmrBefore;
            var acc = store.GetAccount(p.AccountId);
            if (acc == null) continue;
            acc.Extra.RecentMatches.Remove(m.Id);
            acc.Extra.RecentMatches.Insert(0, m.Id);
            if (acc.Extra.RecentMatches.Count > 20) acc.Extra.RecentMatches.RemoveRange(20, acc.Extra.RecentMatches.Count - 20);
            store.SaveAccount(acc);
        }
        store.SaveMatch(m);
        servers.MatchEnded(m.ServerId);
        mm.EndMatch(m.Id);
        Log($"custom match {m.Id} ended winner={m.WinnerTeam}");
        return Ok(new { players = m.Players.Select(p => new { p.AccountId, p.MmrBefore, p.MmrAfter }) });
    }

    // MMR: 팀 평균 Elo (첫 10판은 변동 크게)
    double Avg(int team) => m.Players.Where(p => p.Team == team).Select(p => (double)p.MmrBefore).DefaultIfEmpty(1000).Average();
    double t0 = Avg(0), t1 = Avg(1);
    foreach (var p in m.Players)
    {
        var acc = store.GetAccount(p.AccountId);
        if (acc == null) continue;
        if (!acc.Stats.TryGetValue(m.Region, out var st)) acc.Stats[m.Region] = st = new RegionStats();
        double own = p.Team == 0 ? t0 : t1, opp = p.Team == 0 ? t1 : t0;
        double expected = 1.0 / (1.0 + Math.Pow(10, (opp - own) / 400.0));
        double score = m.WinnerTeam < 0 ? 0.5 : (m.WinnerTeam == p.Team ? 1.0 : 0.0);
        int k = st.Matches < 10 ? 48 : 32;
        st.Mmr = Math.Max(0, st.Mmr + (int)Math.Round(k * (score - expected)));
        st.Matches++;
        if (m.WinnerTeam == p.Team) st.Wins++; else if (m.WinnerTeam >= 0) st.Losses++;
        st.Kills += p.Kills;
        st.Deaths += p.Deaths;
        p.MmrAfter = st.Mmr;
        acc.Extra.RecentMatches.Remove(m.Id);
        acc.Extra.RecentMatches.Insert(0, m.Id);
        if (acc.Extra.RecentMatches.Count > 20) acc.Extra.RecentMatches.RemoveRange(20, acc.Extra.RecentMatches.Count - 20);
        store.SaveAccount(acc);
    }
    store.SaveMatch(m);
    servers.MatchEnded(m.ServerId);
    mm.EndMatch(m.Id);
    Log($"match {m.Id} ended winner={m.WinnerTeam}");
    return Ok(new { players = m.Players.Select(p => new { p.AccountId, p.MmrBefore, p.MmrAfter }) });
    }
});

// 경기를 결과 없이 정리 (사람이 안 들어옴·전원 이탈·시간 초과). MMR 변화 없음.
http.Map("POST", "/server/released", r =>
{
    if (!IsGameServer(r)) return Status(401);
    var req = r.Json<ServerReleased>();
    if (req == null) return Status(400);
    servers.MatchEnded(req.ServerId);
    if (!string.IsNullOrEmpty(req.MatchId))
    {
        lock (resultLock)
        {
            var m = store.GetMatch(req.MatchId);
            if (m != null && m.EndedAt == 0)
            {
                m.EndedAt = TokenService.Now();
                m.WinnerTeam = MatchRecord.Abandoned;
                store.SaveMatch(m);
            }
        }
        mm.EndMatch(req.MatchId);
        Log($"match {req.MatchId} released without result");
    }
    return Ok();
});

http.Map("GET", "/admin/servers", r => IsGameServer(r) ? Ok(servers.All()) : Status(401));

// ---------------------------------------------------------------- 실행

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => { cts.Cancel(); (store as IDisposable)?.Dispose(); };

Log($"knifewar backend listening on {string.Join(", ", cfg.Prefixes())}  storage={opt.Storage}");
var mmTask = mm.RunAsync(cts.Token);
try { await http.RunAsync(cts.Token); }
catch (System.Net.HttpListenerException e)
{
    Log($"HTTP 시작 실패: {e.Message} — 다른 프로그램이 포트를 쓰고 있거나, 외부 접속 주소(http://+:포트/)는 관리자 권한(urlacl)이 필요합니다.");
}
cts.Cancel();
await mmTask;
(store as IDisposable)?.Dispose();

// ---------------------------------------------------------------- 형식

public sealed class AppConfig
{
    public string Urls { get; set; } = "http://127.0.0.1:8080";
    public KnifeWarOptions KnifeWar { get; set; }

    public IEnumerable<string> Prefixes() => (Urls ?? "http://127.0.0.1:8080")
        .Split(';', StringSplitOptions.RemoveEmptyEntries)
        .Select(u => u.Trim().Replace("0.0.0.0", "+").TrimEnd('/') + "/");
}

public sealed record DevLogin(string DeviceId, string Name);
public sealed record RegisterReq(string Email, string Password, string Name, string AgreedTerms = "", bool Age14 = false);
public sealed record TermsReq(string Version);
public sealed record LoginReq(string Email, string Password);
public sealed record CodeReq(string Code);
public sealed record ResetRequestReq(string Email);
public sealed record ResetReq(string Email, string Code, string Password);
public sealed record NameReq(string Name);
public sealed record DeckReq(string Deck);
public sealed record ReportReq(string MatchId, string AccountId, string Reason, string Note);
public sealed record BanReq(string AccountId, int Hours, string Reason);
public sealed record IdReq(string Id);
public sealed record ClientAdminReq(string Version, string Notice, bool? Maintenance);
public sealed record SteamLogin(string Ticket, string Name);
public sealed record AstrelLogin(string IdToken);
public sealed record FriendAdd(string DisplayId);
public sealed record FriendRef(string Id);
public sealed record FriendInvite(string Id, string Kind);
public sealed record RoomJoin(string Code, Dictionary<string, int> Pings);
public sealed record RoomMove(int Slot);
public sealed record JoinQueue(Dictionary<string, int> Pings);
public sealed record PartyJoin(string Code);
public sealed record PartyKick(string AccountId);
public sealed record RegisterServer(string Region, string Address, int Port, string Version);
public sealed record ServerRef(string ServerId);
public sealed record ServerHeartbeat(string ServerId, string MatchId, int Players = -1);
public sealed record ServerReleased(string ServerId, string MatchId);
public sealed record PlayerResult(string AccountId, int Kills, int Deaths);
public sealed record MatchResultReq(string MatchId, int WinnerTeam, List<PlayerResult> Players);
