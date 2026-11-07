namespace KnifeWar.Backend;

/// <summary>
/// 접속 상태와 초대 (메모리). 로그인한 클라가 API를 부를 때마다 "접속 중"으로 표시되고, 90초 소식이 없으면 오프라인.
/// 초대는 친구에게만, 2분 동안 유효. 받는 쪽은 /friends 를 물을 때 같이 받는다.
/// </summary>
public sealed class Presence
{
    const int OnlineSeconds = 90;
    readonly Dictionary<string, long> _seen = new();

    public sealed class Invite
    {
        public string Id { get; set; } = "";
        public string FromId { get; set; } = "";
        public string FromName { get; set; } = "";
        public string Kind { get; set; } = "";     // party | room
        public string Code { get; set; } = "";     // 파티 코드 또는 방 코드
        public long Expires { get; set; }
    }
    readonly Dictionary<string, List<Invite>> _invites = new();   // 받는 사람 id → 초대

    public void Touch(string accountId)
    {
        lock (_seen) _seen[accountId] = TokenService.Now();
    }

    public bool Online(string accountId)
    {
        lock (_seen) return _seen.TryGetValue(accountId, out var t) && TokenService.Now() - t < OnlineSeconds;
    }

    public void AddInvite(string toId, Invite inv)
    {
        lock (_invites)
        {
            if (!_invites.TryGetValue(toId, out var list)) _invites[toId] = list = new();
            list.RemoveAll(i => i.Expires < TokenService.Now() || i.FromId == inv.FromId && i.Kind == inv.Kind);
            if (list.Count >= 20) list.RemoveAt(0);
            list.Add(inv);
        }
    }

    public List<Invite> Invites(string toId)
    {
        lock (_invites)
        {
            if (!_invites.TryGetValue(toId, out var list)) return new();
            list.RemoveAll(i => i.Expires < TokenService.Now());
            return list.ToList();
        }
    }

    public void RemoveInvite(string toId, string inviteId)
    {
        lock (_invites) if (_invites.TryGetValue(toId, out var list)) list.RemoveAll(i => i.Id == inviteId);
    }
}

/// <summary>
/// 사용자 설정 대전 (사설방). 메모리에만 둔다.
/// - 방장이 만들면 6자리 코드. 친구 초대 또는 코드로 들어옴. 최대 12명.
/// - 칸 12개 (0~5 = A팀, 6~11 = B팀). 사람이 없는 칸은 봇이 뛴다.
/// - 방장이 시작하면 게임 서버 하나를 잡아 그 배치 그대로 경기 시작. MMR·전적 반영 없음.
/// - 방을 열어둔 사람은 주기적으로 상태를 묻는다. 60초 소식이 없으면 자동으로 빠짐.
/// </summary>
public sealed class RoomService
{
    public const int Slots = 12;
    const int StaleSeconds = 60;
    const string CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public sealed class Seat
    {
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, int> Pings { get; set; } = new();
        public long LastSeen { get; set; }
        public bool IsBot { get; set; }   // 방장이 넣은 봇 자리 (AccountId = "bot")
    }

    public sealed class Room
    {
        public string Id { get; set; } = "";
        public string Code { get; set; } = "";
        public string HostId { get; set; } = "";
        public Seat[] Seats { get; set; } = new Seat[Slots];
        public string MatchId { get; set; }          // 시작했으면 경기 id
        public long CreatedAt { get; set; }
        public IEnumerable<Seat> People => Seats.Where(s => s != null && !s.IsBot);
    }

    readonly object _lock = new();
    readonly Dictionary<string, Room> _byId = new();
    readonly Dictionary<string, string> _byCode = new();
    readonly Dictionary<string, string> _byAccount = new();
    readonly Matchmaker _mm;

    public RoomService(Matchmaker mm) { _mm = mm; }

    public Room Get(string accountId)
    {
        lock (_lock)
        {
            Prune();
            if (!_byAccount.TryGetValue(accountId, out var id) || !_byId.TryGetValue(id, out var r)) return null;
            var me = r.People.FirstOrDefault(s => s.AccountId == accountId);
            if (me != null) me.LastSeen = TokenService.Now();
            // 경기가 끝났으면 같은 방에서 다시 시작할 수 있게
            if (r.MatchId != null && !_mm.MatchRunning(r.MatchId)) r.MatchId = null;
            return r;
        }
    }

    public Room FindByCode(string code)
    {
        lock (_lock) return _byCode.TryGetValue((code ?? "").Trim().ToUpperInvariant(), out var id) && _byId.TryGetValue(id, out var r) ? r : null;
    }

    public Room Create(Account a, Dictionary<string, int> pings)
    {
        lock (_lock)
        {
            LeaveLocked(a.Id);
            var r = new Room { Id = Guid.NewGuid().ToString("N")[..12], Code = NewCode(), HostId = a.Id, CreatedAt = TokenService.Now() };
            r.Seats[0] = new Seat { AccountId = a.Id, Name = a.DisplayId, Pings = pings ?? new(), LastSeen = TokenService.Now() };
            _byId[r.Id] = r;
            _byCode[r.Code] = r.Id;
            _byAccount[a.Id] = r.Id;
            return r;
        }
    }

    /// <summary>코드로 참가. 실패: not_found, full, started.</summary>
    public (Room room, string error) Join(Account a, string code, Dictionary<string, int> pings)
    {
        lock (_lock)
        {
            Prune();
            if (!_byCode.TryGetValue((code ?? "").Trim().ToUpperInvariant(), out var id) || !_byId.TryGetValue(id, out var r)) return (null, "not_found");
            var mine = r.People.FirstOrDefault(s => s.AccountId == a.Id);
            if (mine != null) { mine.Pings = pings ?? mine.Pings; return (r, null); }
            if (r.MatchId != null && _mm.MatchRunning(r.MatchId)) return (null, "started");
            // 인원이 적은 팀의 첫 빈 칸
            int t0 = r.Seats.Take(6).Count(s => s != null), t1 = r.Seats.Skip(6).Count(s => s != null);
            int slot = FirstFree(r, t0 <= t1 ? 0 : 6);
            if (slot < 0) slot = FirstFree(r, t0 <= t1 ? 6 : 0);
            if (slot < 0) return (null, "full");
            LeaveLocked(a.Id);
            r.Seats[slot] = new Seat { AccountId = a.Id, Name = a.DisplayId, Pings = pings ?? new(), LastSeen = TokenService.Now() };
            _byAccount[a.Id] = r.Id;
            return (r, null);
        }
    }

    static int FirstFree(Room r, int from)
    {
        for (int i = from; i < from + 6; i++) if (r.Seats[i] == null) return i;
        return -1;
    }

    /// <summary>빈 칸으로 자리 옮기기.</summary>
    public bool Move(string accountId, int slot)
    {
        lock (_lock)
        {
            if (slot < 0 || slot >= Slots || !_byAccount.TryGetValue(accountId, out var id) || !_byId.TryGetValue(id, out var r)) return false;
            if (r.MatchId != null || r.Seats[slot] != null) return false;
            int cur = Array.FindIndex(r.Seats, s => s != null && s.AccountId == accountId);
            if (cur < 0) return false;
            r.Seats[slot] = r.Seats[cur];
            r.Seats[cur] = null;
            return true;
        }
    }

    /// <summary>방장이 빈 칸에 봇을 넣거나(add) 봇 자리를 비운다. 실패: host_only, bad_slot, started.</summary>
    public string SetBot(string hostId, int slot, bool add)
    {
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(hostId, out var id) || !_byId.TryGetValue(id, out var r) || r.HostId != hostId) return "host_only";
            if (r.MatchId != null && _mm.MatchRunning(r.MatchId)) return "started";
            if (slot < 0 || slot >= Slots) return "bad_slot";
            var s = r.Seats[slot];
            if (add)
            {
                if (s != null) return "bad_slot";
                r.Seats[slot] = new Seat { AccountId = "bot", Name = "", IsBot = true, LastSeen = TokenService.Now() };
            }
            else
            {
                if (s == null || !s.IsBot) return "bad_slot";
                r.Seats[slot] = null;
            }
            return null;
        }
    }

    public bool Kick(string hostId, string target)
    {
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(hostId, out var id) || !_byId.TryGetValue(id, out var r) || r.HostId != hostId || target == hostId) return false;
            if (r.People.All(s => s.AccountId != target)) return false;
            LeaveLocked(target);
            return true;
        }
    }

    public void Leave(string accountId)
    {
        lock (_lock) LeaveLocked(accountId);
    }

    /// <summary>방장이 시작. 실패: host_only, started, no_server.</summary>
    public string Start(string hostId, Func<string, Account> getAccount)
    {
        lock (_lock)
        {
            if (!_byAccount.TryGetValue(hostId, out var id) || !_byId.TryGetValue(id, out var r) || r.HostId != hostId) return "host_only";
            if (r.MatchId != null && _mm.MatchRunning(r.MatchId)) return "started";
            var players = new List<(Account account, int slot, Dictionary<string, int> pings)>();
            var bots = new List<int>();
            for (int i = 0; i < Slots; i++)
            {
                var s = r.Seats[i];
                if (s == null) continue;
                if (s.IsBot) { bots.Add(i); continue; }
                var a = getAccount(s.AccountId);
                if (a != null) players.Add((a, i, s.Pings));
            }
            // 양 팀에 최소 1명(사람 또는 봇)은 있어야 경기가 시작된다
            bool a0 = r.Seats.Take(6).Any(s => s != null), a1 = r.Seats.Skip(6).Any(s => s != null);
            if (!a0 || !a1) return "need_both_teams";
            string matchId = _mm.CreateCustomMatch(players, bots, out string error);
            if (matchId == null) return error;
            r.MatchId = matchId;
            return null;
        }
    }

    void LeaveLocked(string accountId)
    {
        if (!_byAccount.Remove(accountId, out var id) || !_byId.TryGetValue(id, out var r)) return;
        for (int i = 0; i < Slots; i++) if (r.Seats[i] != null && !r.Seats[i].IsBot && r.Seats[i].AccountId == accountId) r.Seats[i] = null;
        if (!r.People.Any())
        {
            _byId.Remove(r.Id);
            _byCode.Remove(r.Code);
        }
        else if (r.HostId == accountId) r.HostId = r.People.First().AccountId;
    }

    void Prune()
    {
        long now = TokenService.Now();
        foreach (var r in _byId.Values.ToList())
            foreach (var s in r.People.Where(s => now - s.LastSeen > StaleSeconds).ToList())
            {
                // 경기 중인 사람은 방 화면을 안 보고 있음 → 유지
                if (r.MatchId != null && _mm.MatchRunning(r.MatchId)) continue;
                LeaveLocked(s.AccountId);
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
