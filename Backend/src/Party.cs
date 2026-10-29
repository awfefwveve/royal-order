namespace KnifeWar.Backend;

/// <summary>
/// 파티 (친구와 같이 매칭). 메모리에만 둔다 — 백엔드가 재시작되면 파티는 다시 만들면 된다.
/// - 파티장이 만들면 6자리 코드가 생긴다. 친구는 코드로 참가 (최대 6명 = 한 팀)
/// - 파티장이 매칭을 시작하면 파티 전원이 한 티켓으로 같은 팀에 들어간다
/// - 멤버는 화면을 열어두는 동안 주기적으로 상태를 묻는다. 60초 동안 소식이 없으면 자동으로 빠짐
/// </summary>
public sealed class PartyService
{
    public const int MaxSize = 6;
    const int StaleSeconds = 60;
    const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 헷갈리는 글자(0 O 1 I) 제외

    public sealed class PartyMember
    {
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, int> Pings { get; set; } = new();
        public long LastSeen { get; set; }
    }

    public sealed class Party
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string LeaderId { get; set; } = "";
        public List<PartyMember> Members { get; set; } = new();
    }

    readonly object _lock = new();
    readonly Dictionary<string, Party> _byId = new();
    readonly Dictionary<string, string> _byCode = new();
    readonly Dictionary<string, string> _byAccount = new();
    readonly Matchmaker _mm;

    public PartyService(Matchmaker mm) { _mm = mm; }

    public Party Get(string accountId)
    {
        lock (_lock)
        {
            Prune();
            if (!_byAccount.TryGetValue(accountId, out var id) || !_byId.TryGetValue(id, out var p)) return null;
            var me = p.Members.FirstOrDefault(m => m.AccountId == accountId);
            if (me != null) me.LastSeen = TokenService.Now();
            return p;
        }
    }

    public Party Create(Account a)
    {
        lock (_lock)
        {
            if (_byAccount.TryGetValue(a.Id, out var existing) && _byId.TryGetValue(existing, out var cur)) return cur;
            var p = new Party { Id = Guid.NewGuid().ToString("N")[..12], Code = NewCode(), LeaderId = a.Id };
            p.Members.Add(new PartyMember { AccountId = a.Id, Name = a.Name, LastSeen = TokenService.Now() });
            _byId[p.Id] = p;
            _byCode[p.Code] = p.Id;
            _byAccount[a.Id] = p.Id;
            return p;
        }
    }

    /// <summary>코드로 참가. 실패 이유: "not_found", "full", "in_match".</summary>
    public (Party party, string error) Join(Account a, string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        lock (_lock)
        {
            Prune();
            if (!_byCode.TryGetValue(code, out var id) || !_byId.TryGetValue(id, out var p)) return (null, "not_found");
            if (p.Members.Any(m => m.AccountId == a.Id)) return (p, null);
            if (p.Members.Count >= MaxSize) return (null, "full");
            var st = _mm.Status(p.LeaderId);
            if (st != null && (st.State == Matchmaker.TicketState.Searching || st.State == Matchmaker.TicketState.Matched) && st.PartyId == p.Id)
                return (null, "in_match");
            LeaveLocked(a.Id);
            p.Members.Add(new PartyMember { AccountId = a.Id, Name = a.Name, LastSeen = TokenService.Now() });
            _byAccount[a.Id] = p.Id;
            _mm.Leave(p.LeaderId);   // 인원이 바뀌었으니 진행 중인 파티 검색은 취소
            return (p, null);
        }
    }

    public void Leave(string accountId)
    {
        lock (_lock) LeaveLocked(accountId);
    }

    public bool Kick(string leaderId, string target)
    {
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(leaderId, out var id) || !_byId.TryGetValue(id, out var p) || p.LeaderId != leaderId) return false;
            if (target == leaderId || p.Members.All(m => m.AccountId != target)) return false;
            LeaveLocked(target);
            return true;
        }
    }

    public void SetPings(string accountId, Dictionary<string, int> pings)
    {
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(accountId, out var id) || !_byId.TryGetValue(id, out var p)) return;
            var me = p.Members.FirstOrDefault(m => m.AccountId == accountId);
            if (me != null) { me.Pings = pings ?? new(); me.LastSeen = TokenService.Now(); }
        }
    }

    void LeaveLocked(string accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var id) || !_byId.TryGetValue(id, out var p)) { _byAccount.Remove(accountId); return; }
        _mm.Leave(accountId);   // 파티 검색 중이었으면 취소 (진행 중 경기는 유지)
        p.Members.RemoveAll(m => m.AccountId == accountId);
        _byAccount.Remove(accountId);
        if (p.Members.Count == 0)
        {
            _byId.Remove(p.Id);
            _byCode.Remove(p.Code);
        }
        else if (p.LeaderId == accountId) p.LeaderId = p.Members[0].AccountId;
    }

    void Prune()
    {
        long now = TokenService.Now();
        foreach (var p in _byId.Values.ToList())
            foreach (var m in p.Members.Where(m => now - m.LastSeen > StaleSeconds).ToList())
            {
                // 경기 중인 멤버는 게임 화면이라 파티를 안 물어볼 수 있다 → 유지
                var st = _mm.Status(m.AccountId);
                if (st != null && st.State == Matchmaker.TicketState.Matched) continue;
                LeaveLocked(m.AccountId);
            }
    }

    string NewCode()
    {
        var rng = Random.Shared;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var c = new string(Enumerable.Range(0, 6).Select(_ => CodeChars[rng.Next(CodeChars.Length)]).ToArray());
            if (!_byCode.ContainsKey(c)) return c;
        }
        return Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    }
}
