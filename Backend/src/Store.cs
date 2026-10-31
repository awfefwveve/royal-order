using System.Text.Json;

namespace KnifeWar.Backend;

public sealed class Account
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";     // steam | dev
    public string ExternalId { get; set; } = "";   // steamid64 또는 개발용 기기 id
    public string Name { get; set; } = "";
    public string HomeRegion { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastLoginAt { get; set; }
    public bool Banned { get; set; }
    public Dictionary<string, RegionStats> Stats { get; set; } = new();   // 리전별 랭크·전적
    public AccountExtra Extra { get; set; } = new();                      // 자체 계정·제재·최근 경기

    /// <summary>지금 제재 중인가 (영구 또는 기간).</summary>
    public bool IsBanned(long now) => Banned || Extra.BanUntil > now;
    public string DisplayId => string.IsNullOrEmpty(Extra.Tag) ? Name : Name + "#" + Extra.Tag;
}

/// <summary>
/// 자체 계정 정보 (라이엇·넥슨식 자체 브랜드 계정). 저장소에는 한 덩어리(json)로 들어간다.
/// </summary>
public sealed class AccountExtra
{
    public string Tag { get; set; } = "";             // 이름#태그 (같은 이름 여러 명 가능)
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";    // PBKDF2-SHA256 (Passwords.cs)
    public bool EmailVerified { get; set; }
    public string VerifyCode { get; set; } = "";
    public string ResetCode { get; set; } = "";
    public long ResetExpires { get; set; }
    public int FailedLogins { get; set; }
    public long LockedUntil { get; set; }
    public int TokenVersion { get; set; }             // 올리면 기존 로그인 전부 무효 (비밀번호 변경·모든 기기 로그아웃)
    public long BanUntil { get; set; }                // 기간 제재 (0 = 없음)
    public string BanReason { get; set; } = "";
    public List<string> RecentMatches { get; set; } = new();
    public string TermsVersion { get; set; } = "";    // 마지막으로 동의한 약관 버전
    public long TermsAgreedAt { get; set; }   // 최근 경기 id (최신이 앞, 최대 20)
}

/// <summary>플레이어 신고.</summary>
public sealed class Report
{
    public string Id { get; set; } = "";
    public string ReporterId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public string MatchId { get; set; } = "";
    public string Reason { get; set; } = "";     // cheat | abuse | troll | name | afk
    public string Note { get; set; } = "";
    public long CreatedAt { get; set; }
    public bool Handled { get; set; }
}

public sealed class RegionStats
{
    public int Mmr { get; set; } = 1000;
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
}

public sealed class MatchRecord
{
    public string Id { get; set; } = "";
    public string Region { get; set; } = "";
    public string ServerId { get; set; } = "";
    public long CreatedAt { get; set; }
    public long EndedAt { get; set; }
    public int WinnerTeam { get; set; } = -1;   // -1 무승부, -2 무효(서버 장애·전원 이탈)
    public List<MatchPlayer> Players { get; set; } = new();
    public const int Abandoned = -2;
}

public sealed class MatchPlayer
{
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Slot { get; set; }
    public int Team { get; set; }
    public int MmrBefore { get; set; }
    public int MmrAfter { get; set; }
    public int Kills { get; set; }
    public int Deaths { get; set; }
}

/// <summary>
/// 저장소 인터페이스. 지금은 파일(JSON) 구현, 상용 단계에서 PostgreSQL 구현으로 교체한다.
/// </summary>
public interface IStore
{
    Account FindAccount(string provider, string externalId);
    Account GetAccount(string id);
    void SaveAccount(Account a);
    void SaveMatch(MatchRecord m);
    MatchRecord GetMatch(string id);
    IReadOnlyList<Account> TopAccounts(string region, int count);
    bool NameTagTaken(string name, string tag);
    void SaveReport(Report r);
    IReadOnlyList<Report> Reports(bool onlyOpen, int count);
    bool HasReport(string reporterId, string targetId, string matchId);
}

public sealed class FileStore : IStore, IDisposable
{
    readonly string _dir;
    readonly object _lock = new();
    readonly Dictionary<string, Account> _accounts = new();
    readonly Dictionary<string, string> _byExternal = new();
    readonly Dictionary<string, MatchRecord> _matches = new();
    readonly List<Report> _reports = new();
    readonly Timer _flush;
    bool _dirty;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public FileStore(string dir)
    {
        _dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(_dir);
        Load();
        _flush = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    string AccountsPath => Path.Combine(_dir, "accounts.json");
    string MatchesPath => Path.Combine(_dir, "matches.json");
    string ReportsPath => Path.Combine(_dir, "reports.json");

    void Load()
    {
        if (File.Exists(AccountsPath))
            foreach (var a in JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(AccountsPath)) ?? new())
            {
                _accounts[a.Id] = a;
                _byExternal[a.Provider + ":" + a.ExternalId] = a.Id;
            }
        if (File.Exists(MatchesPath))
            foreach (var m in JsonSerializer.Deserialize<List<MatchRecord>>(File.ReadAllText(MatchesPath)) ?? new())
                _matches[m.Id] = m;
        if (File.Exists(ReportsPath))
            _reports.AddRange(JsonSerializer.Deserialize<List<Report>>(File.ReadAllText(ReportsPath)) ?? new());
        foreach (var a in _accounts.Values) a.Extra ??= new AccountExtra();
    }

    /// <summary>다른 곳에서 계정·경기 객체를 고치는 동안 저장이 끼어들지 않게 잡는 잠금 (결과 처리에서 사용).</summary>
    public object WriteLock => _lock;

    public void Flush()
    {
        // 타이머 스레드: 예외가 나가면 프로세스가 죽으므로 반드시 잡는다
        try
        {
            lock (_lock)
            {
                if (!_dirty) return;
                WriteAtomic(AccountsPath, JsonSerializer.Serialize(_accounts.Values.ToList(), Json));
                WriteAtomic(MatchesPath, JsonSerializer.Serialize(_matches.Values.OrderByDescending(m => m.CreatedAt).Take(5000).ToList(), Json));
                WriteAtomic(ReportsPath, JsonSerializer.Serialize(_reports.TakeLast(20000).ToList(), Json));
                _dirty = false;
            }
        }
        catch (Exception e) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} store flush failed (다음 주기에 재시도): {e.Message}"); }
    }

    static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    public Account FindAccount(string provider, string externalId)
    {
        lock (_lock) return _byExternal.TryGetValue(provider + ":" + externalId, out var id) ? _accounts[id] : null;
    }

    public Account GetAccount(string id)
    {
        lock (_lock) return id != null && _accounts.TryGetValue(id, out var a) ? a : null;
    }

    public void SaveAccount(Account a)
    {
        lock (_lock)
        {
            _accounts[a.Id] = a;
            _byExternal[a.Provider + ":" + a.ExternalId] = a.Id;
            _dirty = true;
        }
    }

    public void SaveMatch(MatchRecord m)
    {
        lock (_lock) { _matches[m.Id] = m; _dirty = true; }
    }

    public MatchRecord GetMatch(string id)
    {
        lock (_lock) return _matches.TryGetValue(id, out var m) ? m : null;
    }

    public IReadOnlyList<Account> TopAccounts(string region, int count)
    {
        lock (_lock)
            return _accounts.Values
                .Where(a => a.Stats.TryGetValue(region, out var s) && s.Matches > 0)
                .OrderByDescending(a => a.Stats[region].Mmr)
                .Take(count).ToList();
    }

    public bool NameTagTaken(string name, string tag)
    {
        lock (_lock) return _accounts.Values.Any(a => a.Extra.Tag == tag && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public void SaveReport(Report r)
    {
        lock (_lock)
        {
            int i = _reports.FindIndex(x => x.Id == r.Id);
            if (i >= 0) _reports[i] = r; else _reports.Add(r);
            _dirty = true;
        }
    }

    public IReadOnlyList<Report> Reports(bool onlyOpen, int count)
    {
        lock (_lock) return _reports.Where(r => !onlyOpen || !r.Handled).Reverse().Take(count).ToList();
    }

    public bool HasReport(string reporterId, string targetId, string matchId)
    {
        lock (_lock) return _reports.Any(r => r.ReporterId == reporterId && r.TargetId == targetId && r.MatchId == matchId);
    }

    public void Dispose()
    {
        _flush.Dispose();
        Flush();
    }
}
