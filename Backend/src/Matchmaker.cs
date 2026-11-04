namespace KnifeWar.Backend;

/// <summary>
/// 리전별 매칭 (혼자 또는 파티).
/// - 티켓 = 같이 들어가는 묶음 (혼자면 1명, 파티면 최대 6명). 파티는 항상 같은 팀.
/// - 리전 후보: 묶음 안 모든 사람의 핑이 MaxPingMs 이하인 리전 (오래 기다리면 범위를 넓힘)
/// - 가장 오래 기다린 티켓을 기준으로, MMR 차이 허용 범위를 기다린 시간만큼 넓혀가며 12명을 모은다
/// - 오래 기다렸는데 인원이 모자라면 봇으로 채워서 시작 (일반전 정책, 설정값)
/// - 팀 배정: 큰 묶음부터, 인원이 적은 팀(같으면 MMR 합이 낮은 팀)으로
/// </summary>
public sealed class Matchmaker
{
    public enum TicketState { Searching, Matched, Cancelled, Ended }

    public sealed class Member
    {
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public sealed class Ticket
    {
        public string Id { get; set; } = "";
        public string AccountId { get; set; } = "";      // 대표 (파티장)
        public string PartyId { get; set; }
        public List<Member> Members { get; set; } = new();
        public Dictionary<string, int> Pings { get; set; } = new();   // 리전별: 멤버 중 가장 나쁜 핑
        public long CreatedAt { get; set; }
        public TicketState State { get; set; }
        public string Status { get; set; } = "searching";
        public Dictionary<string, MatchResult> Results { get; set; } = new();   // 멤버별 입장 정보
        public long MatchedAt { get; set; }
        public int Size => Members.Count;
        public string MatchId => Results.Values.FirstOrDefault()?.MatchId;
    }

    public sealed class MatchResult
    {
        public string MatchId { get; set; } = "";
        public string Region { get; set; } = "";
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public string JoinToken { get; set; } = "";
        public int Slot { get; set; }
        public int Team { get; set; }
    }

    readonly object _lock = new();
    readonly Dictionary<string, Ticket> _tickets = new();          // ticket id → ticket
    readonly Dictionary<string, string> _byAccount = new();        // account id → ticket id (파티 멤버 전원)
    readonly KnifeWarOptions _opt;
    readonly IStore _store;
    readonly ServerRegistry _servers;
    readonly TokenService _tokens;
    readonly Action<string> _log;

    public Matchmaker(KnifeWarOptions opt, IStore store, ServerRegistry servers, TokenService tokens, Action<string> log)
    {
        _opt = opt; _store = store; _servers = servers; _tokens = tokens; _log = log;
    }

    MatchmakingOptions M => _opt.Matchmaking;
    int TeamSize => M.PlayersPerMatch / 2;

    /// <summary>혼자 대기열.</summary>
    public Ticket Join(Account a, Dictionary<string, int> pings)
        => JoinGroup(new List<(Account, Dictionary<string, int>)> { (a, pings) }, null);

    /// <summary>
    /// 묶음 대기열 (파티). members[0] = 대표. 누구라도 진행 중인 경기가 있으면 그 티켓을 돌려준다 (재접속).
    /// 실패하면 null (인원 초과).
    /// </summary>
    public Ticket JoinGroup(List<(Account account, Dictionary<string, int> pings)> members, string partyId)
    {
        if (members.Count == 0 || members.Count > TeamSize) return null;
        lock (_lock)
        {
            var leader = members[0].account;
            foreach (var (a, _) in members)
                if (_byAccount.TryGetValue(a.Id, out var tid) && _tickets.TryGetValue(tid, out var old) && old.State == TicketState.Matched)
                    return old;   // 진행 중인 경기로 돌려보낸다 (게임을 껐다 켜도 재접속)

            // 같은 사람들로 이미 찾는 중이면 핑만 갱신
            if (_byAccount.TryGetValue(leader.Id, out var lt) && _tickets.TryGetValue(lt, out var cur) && cur.State == TicketState.Searching
                && cur.Size == members.Count && members.All(m => _byAccount.TryGetValue(m.account.Id, out var x) && x == cur.Id))
            {
                cur.Pings = Combine(members.Select(m => m.pings));
                return cur;
            }

            // 멤버가 다른 검색에 걸려 있으면 그 검색은 취소
            foreach (var (a, _) in members) LeaveLocked(a.Id);

            var t = new Ticket
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                AccountId = leader.Id,
                PartyId = partyId,
                Members = members.Select(m => new Member { AccountId = m.account.Id, Name = m.account.Name }).ToList(),
                Pings = Combine(members.Select(m => m.pings)),
                CreatedAt = TokenService.Now(),
                State = TicketState.Searching,
            };
            _tickets[t.Id] = t;
            foreach (var m in t.Members) _byAccount[m.AccountId] = t.Id;
            return t;
        }
    }

    /// <summary>멤버 핑을 합친다: 모두가 잰 리전만, 값은 가장 나쁜 사람 기준.</summary>
    Dictionary<string, int> Combine(IEnumerable<Dictionary<string, int>> all)
    {
        Dictionary<string, int> result = null;
        foreach (var p in all)
        {
            var c = Clean(p);
            if (c.Count == 0) continue;   // 핑을 못 잰 멤버는 제외 (개발 환경 등)
            if (result == null) { result = c; continue; }
            foreach (var k in result.Keys.ToList())
                if (c.TryGetValue(k, out var v)) result[k] = Math.Max(result[k], v); else result.Remove(k);
        }
        return result ?? new Dictionary<string, int>();
    }

    Dictionary<string, int> Clean(Dictionary<string, int> pings)
    {
        var known = _opt.Regions.Select(r => r.Id).ToHashSet();
        var d = new Dictionary<string, int>();
        if (pings != null)
            foreach (var kv in pings)
                if (known.Contains(kv.Key) && kv.Value > 0 && kv.Value < 2000) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>검색 취소. 파티 검색이면 파티 전체가 취소된다.</summary>
    public void Leave(string accountId)
    {
        lock (_lock) LeaveLocked(accountId);
    }

    void LeaveLocked(string accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var id) || !_tickets.TryGetValue(id, out var t)) return;
        if (t.State != TicketState.Searching) return;   // 진행 중 경기는 취소 대상 아님
        t.State = TicketState.Cancelled;
        t.Status = "cancelled";
        foreach (var m in t.Members)
            if (_byAccount.TryGetValue(m.AccountId, out var x) && x == t.Id) _byAccount.Remove(m.AccountId);
    }

    public Ticket Status(string accountId)
    {
        lock (_lock) return _byAccount.TryGetValue(accountId, out var id) && _tickets.TryGetValue(id, out var t) ? t : null;
    }

    public MatchResult ResultFor(Ticket t, string accountId)
    {
        lock (_lock) return t != null && t.Results.TryGetValue(accountId, out var r) ? r : null;
    }

    /// <summary>리전에서 찾는 중인 사람 수.</summary>
    public int Searching(string region)
    {
        lock (_lock) return _tickets.Values.Where(t => t.State == TicketState.Searching && Candidates(t).Contains(region)).Sum(t => t.Size);
    }

    /// <summary>경기가 끝남 (결과 보고·무효 처리) → 그 경기 티켓은 더 이상 재접속 대상이 아님.</summary>
    public void EndMatch(string matchId)
    {
        lock (_lock)
            foreach (var t in _tickets.Values)
                if (t.State == TicketState.Matched && t.MatchId == matchId)
                {
                    t.State = TicketState.Ended;
                    t.Status = "ended";
                }
    }

    /// <summary>서버가 배정을 가져가기 전에 죽음 → 같은 묶음을 대기열로 되돌린다 (기다린 시간 유지).</summary>
    void Requeue(string matchId)
    {
        foreach (var t in _tickets.Values)
            if (t.State == TicketState.Matched && t.MatchId == matchId)
            {
                t.State = TicketState.Searching;
                t.Status = "searching";
                t.Results.Clear();
            }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception e) { _log("matchmaker tick failed: " + e); }
            try { await Task.Delay(1000, ct); } catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>대기 시간에 따라 넓어지는 후보 리전 (핑 순).</summary>
    List<string> Candidates(Ticket t)
    {
        long wait = TokenService.Now() - t.CreatedAt;
        int maxPing = wait >= M.PingWidenAfterSeconds * 2 ? int.MaxValue
            : wait >= M.PingWidenAfterSeconds ? M.MaxPingMs * 2 : M.MaxPingMs;
        var list = t.Pings.Where(kv => kv.Value <= maxPing).OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        // 핑 정보가 하나도 없으면 첫 번째 리전 (개발용)
        if (list.Count == 0 && t.Pings.Count == 0 && _opt.Regions.Count > 0) list.Add(_opt.Regions[0].Id);
        return list;
    }

    int MmrOf(string accountId, string region)
    {
        var a = _store.GetAccount(accountId);
        return a != null && a.Stats.TryGetValue(region, out var s) ? s.Mmr : 1000;
    }

    /// <summary>묶음 MMR = 멤버 평균 + 파티 보정 (같이 하는 파티는 조금 더 강하게 본다).</summary>
    int MmrOf(Ticket t, string region)
    {
        double avg = t.Members.Average(m => MmrOf(m.AccountId, region));
        return (int)Math.Round(avg + (t.Size > 1 ? 25 * (t.Size - 1) : 0));
    }

    void Tick()
    {
        lock (_lock)
        {
            long now = TokenService.Now();

            // 죽은 서버의 경기 처리
            foreach (var lost in _servers.DrainLost())
            {
                if (!lost.Taken)
                {
                    _log($"server lost before taking match {lost.MatchId} → requeue players");
                    Requeue(lost.MatchId);
                }
                else
                {
                    _log($"server lost during match {lost.MatchId} → match abandoned");
                    EndMatch(lost.MatchId);
                }
                var rec = _store.GetMatch(lost.MatchId);
                if (rec != null && rec.EndedAt == 0) { rec.EndedAt = now; rec.WinnerTeam = MatchRecord.Abandoned; _store.SaveMatch(rec); }
            }

            // 오래된 기록 정리 (진행 중 경기는 2시간까지 재접속 정보 유지)
            foreach (var dead in _tickets.Values.Where(t =>
                         (t.State == TicketState.Cancelled || t.State == TicketState.Ended) && now - t.CreatedAt > 600
                         || t.State == TicketState.Matched && now - t.MatchedAt > 7200).ToList())
            {
                _tickets.Remove(dead.Id);
                foreach (var m in dead.Members)
                    if (_byAccount.TryGetValue(m.AccountId, out var cur) && cur == dead.Id) _byAccount.Remove(m.AccountId);
            }

            var searching = _tickets.Values.Where(t => t.State == TicketState.Searching).OrderBy(t => t.CreatedAt).ToList();
            var used = new HashSet<string>();

            foreach (var anchor in searching)
            {
                if (used.Contains(anchor.Id)) continue;
                long wait = now - anchor.CreatedAt;
                anchor.Status = "searching";

                foreach (var region in Candidates(anchor))
                {
                    int anchorMmr = MmrOf(anchor, region);
                    int window = M.MmrWindowStart + (int)(M.MmrWindowGrowPerSecond * wait);
                    var group = new List<Ticket> { anchor };
                    int players = anchor.Size;
                    foreach (var t in searching)
                    {
                        if (players == M.PlayersPerMatch) break;
                        if (t == anchor || used.Contains(t.Id) || players + t.Size > M.PlayersPerMatch) continue;
                        if (!Candidates(t).Contains(region) || Math.Abs(MmrOf(t, region) - anchorMmr) > window) continue;
                        group.Add(t);
                        players += t.Size;
                    }

                    var teams = AssignTeams(group, region);
                    if (teams == null) continue;   // 묶음 크기 때문에 6:6으로 못 나눔 → 더 기다림

                    bool full = players >= M.PlayersPerMatch;
                    bool fill = M.FillWithBotsAfterSeconds > 0 && wait >= M.FillWithBotsAfterSeconds && players >= M.MinHumansToStart;   // 0 = 봇 채우기 안 함 (경쟁전은 사람 12명)
                    if (!full && !fill) continue;

                    if (_servers.FreeServers(region) == 0)
                    {
                        anchor.Status = "waiting_server";
                        continue;
                    }
                    if (CreateMatch(region, teams))
                    {
                        foreach (var t in group) used.Add(t.Id);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>이 경기가 아직 진행 중인지 (배정됨 ~ 결과/무효 전).</summary>
    public bool MatchRunning(string matchId)
    {
        lock (_lock) return _tickets.Values.Any(t => t.State == TicketState.Matched && t.MatchId == matchId);
    }

    /// <summary>
    /// 사용자 설정 대전: 정해진 칸 그대로 경기 생성 (빈 칸은 서버에서 봇). MMR 반영 없음.
    /// 실패하면 null + error (busy = 누가 다른 경기 중, no_server = 빈 게임 서버 없음).
    /// </summary>
    public string CreateCustomMatch(List<(Account account, int slot, Dictionary<string, int> pings)> players, out string error)
    {
        error = null;
        lock (_lock)
        {
            foreach (var (a, _, _) in players)
                if (_byAccount.TryGetValue(a.Id, out var tid) && _tickets.TryGetValue(tid, out var t0) && t0.State == TicketState.Matched)
                { error = "busy"; return null; }

            // 리전: 모두가 잰 리전 중 가장 나쁜 핑이 제일 낮은 곳 (못 쟀으면 빈 서버가 있는 첫 리전)
            var pings = Combine(players.Select(p => p.pings));
            var order = pings.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
            foreach (var r in _opt.Regions) if (!order.Contains(r.Id)) order.Add(r.Id);
            string region = order.FirstOrDefault(r => _servers.FreeServers(r) > 0);
            if (region == null) { error = "no_server"; return null; }

            string matchId = Guid.NewGuid().ToString("N")[..12];
            var assignment = new ServerRegistry.Assignment { MatchId = matchId, Region = region };
            var record = new MatchRecord { Id = matchId, Region = region, CreatedAt = TokenService.Now(), Custom = true };
            foreach (var (a, slot, _) in players)
            {
                int team = slot < TeamSize ? 0 : 1;
                assignment.Players.Add(new ServerRegistry.AssignedPlayer { AccountId = a.Id, Name = a.Name, Slot = slot, Team = team });
                record.Players.Add(new MatchPlayer { AccountId = a.Id, Name = a.Name, Slot = slot, Team = team, MmrBefore = MmrOf(a.Id, region) });
            }
            var server = _servers.Allocate(assignment);
            if (server == null) { error = "no_server"; return null; }
            record.ServerId = server.Id;
            _store.SaveMatch(record);

            // 사람마다 "배정됨" 티켓 → 클라는 평소처럼 /mm/join·/mm/status 로 접속 정보를 받는다
            long now = TokenService.Now();
            foreach (var (a, slot, _) in players)
            {
                LeaveLocked(a.Id);
                int team = slot < TeamSize ? 0 : 1;
                var t = new Ticket
                {
                    Id = Guid.NewGuid().ToString("N")[..16], AccountId = a.Id,
                    Members = new() { new Member { AccountId = a.Id, Name = a.Name } },
                    CreatedAt = now, MatchedAt = now, State = TicketState.Matched, Status = "matched",
                };
                t.Results[a.Id] = new MatchResult
                {
                    MatchId = matchId, Region = region, Address = server.Address, Port = server.Port,
                    JoinToken = _tokens.IssueJoin(matchId, a.Id, a.Name, slot, TimeSpan.FromMinutes(120)),
                    Slot = slot, Team = team,
                };
                _tickets[t.Id] = t;
                _byAccount[a.Id] = t.Id;
            }
            _log($"custom match {matchId} region={region} humans={players.Count} server={server.Id}");
            return matchId;
        }
    }

    /// <summary>묶음을 두 팀으로 (파티는 쪼개지 않음). 못 나누면 null.</summary>
    List<Ticket>[] AssignTeams(List<Ticket> group, string region)
    {
        var teams = new[] { new List<Ticket>(), new List<Ticket>() };
        int[] count = { 0, 0 };
        long[] mmr = { 0, 0 };
        foreach (var t in group.OrderByDescending(t => t.Size).ThenByDescending(t => MmrOf(t, region)))
        {
            int pick;
            bool fit0 = count[0] + t.Size <= TeamSize, fit1 = count[1] + t.Size <= TeamSize;
            if (!fit0 && !fit1) return null;
            if (fit0 && !fit1) pick = 0;
            else if (fit1 && !fit0) pick = 1;
            else if (count[0] != count[1]) pick = count[0] < count[1] ? 0 : 1;
            else pick = mmr[0] <= mmr[1] ? 0 : 1;
            teams[pick].Add(t);
            count[pick] += t.Size;
            mmr[pick] += (long)MmrOf(t, region) * t.Size;
        }
        return teams;
    }

    bool CreateMatch(string region, List<Ticket>[] teams)
    {
        string matchId = Guid.NewGuid().ToString("N")[..12];
        var assignment = new ServerRegistry.Assignment { MatchId = matchId, Region = region };
        var record = new MatchRecord { Id = matchId, Region = region, CreatedAt = TokenService.Now() };
        var slotOf = new Dictionary<string, (int slot, int team)>();
        for (int team = 0; team < 2; team++)
        {
            int k = 0;
            foreach (var t in teams[team])
                foreach (var m in t.Members)
                {
                    int slot = team * TeamSize + k++;
                    slotOf[m.AccountId] = (slot, team);
                    assignment.Players.Add(new ServerRegistry.AssignedPlayer { AccountId = m.AccountId, Name = m.Name, Slot = slot, Team = team });
                    record.Players.Add(new MatchPlayer { AccountId = m.AccountId, Name = m.Name, Slot = slot, Team = team, MmrBefore = MmrOf(m.AccountId, region) });
                }
        }

        var server = _servers.Allocate(assignment);
        if (server == null) return false;
        record.ServerId = server.Id;
        _store.SaveMatch(record);

        long now = TokenService.Now();
        foreach (var t in teams.SelectMany(x => x))
        {
            t.State = TicketState.Matched;
            t.Status = "matched";
            t.MatchedAt = now;
            t.Results.Clear();
            foreach (var m in t.Members)
            {
                var (slot, team) = slotOf[m.AccountId];
                t.Results[m.AccountId] = new MatchResult
                {
                    MatchId = matchId, Region = region,
                    Address = server.Address, Port = server.Port,
                    JoinToken = _tokens.IssueJoin(matchId, m.AccountId, m.Name, slot, TimeSpan.FromMinutes(120)),
                    Slot = slot, Team = team,
                };
            }
        }
        _log($"match {matchId} region={region} humans={assignment.Players.Count} groups={teams[0].Count + teams[1].Count} server={server.Id}");
        return true;
    }
}
