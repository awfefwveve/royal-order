namespace KnifeWar.Backend;

/// <summary>
/// 게임 서버(데디) 등록부. 서버는 켜지면 등록하고 주기적으로 하트비트를 보낸다.
/// 매칭이 성사되면 비어 있는 서버에 경기를 배정하고, 서버는 배정을 가져가 경기를 연다.
/// 실제 상용 배포에서는 이 자리에 오케스트레이터(Edgegap 등) 호출이 들어간다 — 인터페이스는 같다.
/// </summary>
public sealed class ServerRegistry
{
    public sealed class GameServer
    {
        public string Id { get; set; } = "";
        public string Region { get; set; } = "";
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public string Version { get; set; } = "";
        public long LastHeartbeat { get; set; }
        public string CurrentMatch { get; set; }   // null = 대기 중
        public Assignment Pending { get; set; }     // 서버가 아직 가져가지 않은 배정
        public int Players { get; set; }            // 하트비트로 받은 접속 인원 (모니터링)
    }

    /// <summary>하트비트가 끊겨 정리된 서버의 경기. Taken=false면 서버가 배정을 가져가기 전에 죽음 → 다시 매칭.</summary>
    public sealed record LostMatch(string MatchId, bool Taken);

    public sealed class Assignment
    {
        public string MatchId { get; set; } = "";
        public string Region { get; set; } = "";
        public List<AssignedPlayer> Players { get; set; } = new();
        public bool Custom { get; set; }   // 사용자 설정: 목록에 없는 칸은 비워 둔다 (봇으로 채우지 않음)
    }

    public sealed class AssignedPlayer
    {
        public string AccountId { get; set; } = "";
        public string Name { get; set; } = "";
        public int Slot { get; set; }
        public int Team { get; set; }
        public bool Bot { get; set; }   // 사용자 설정 방의 봇 자리
    }

    const int HeartbeatTimeoutSec = 15;
    readonly object _lock = new();
    readonly Dictionary<string, GameServer> _servers = new();
    readonly List<LostMatch> _lost = new();

    /// <summary>죽은 서버에 걸려 있던 경기 목록을 꺼낸다 (매칭기가 1초마다 처리). 잠금 순서 꼬임을 피하려고 콜백 대신 큐.</summary>
    public List<LostMatch> DrainLost()
    {
        lock (_lock)
        {
            Prune();
            var l = new List<LostMatch>(_lost);
            _lost.Clear();
            return l;
        }
    }

    public GameServer Register(string region, string address, int port, string version)
    {
        lock (_lock)
        {
            // 같은 주소·포트가 다시 등록하면 (서버 재시작) 이전 기록을 대체
            foreach (var old in _servers.Values.Where(s => s.Address == address && s.Port == port).ToList())
                Drop(old);
            var s = new GameServer
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Region = region, Address = address, Port = port, Version = version,
                LastHeartbeat = TokenService.Now(),
            };
            _servers[s.Id] = s;
            return s;
        }
    }

    public GameServer Get(string id)
    {
        lock (_lock) return id != null && _servers.TryGetValue(id, out var s) ? s : null;
    }

    public bool Heartbeat(string id, int players = -1)
    {
        lock (_lock)
        {
            if (id == null || !_servers.TryGetValue(id, out var s)) return false;
            s.LastHeartbeat = TokenService.Now();
            if (players >= 0) s.Players = players;
            return true;
        }
    }

    /// <summary>리전의 빈 서버 하나를 잡아 경기를 배정. 없으면 null.</summary>
    public GameServer Allocate(Assignment a)
    {
        lock (_lock)
        {
            Prune();
            var s = _servers.Values
                .Where(x => x.Region == a.Region && x.CurrentMatch == null && x.Pending == null)
                .OrderBy(x => x.LastHeartbeat)
                .FirstOrDefault();
            if (s == null) return null;
            s.Pending = a;
            s.CurrentMatch = a.MatchId;
            return s;
        }
    }

    public Assignment TakeAssignment(string id)
    {
        lock (_lock)
        {
            if (!_servers.TryGetValue(id, out var s)) return null;
            s.LastHeartbeat = TokenService.Now();
            var a = s.Pending;
            s.Pending = null;
            return a;
        }
    }

    public void MatchEnded(string id)
    {
        lock (_lock)
            if (_servers.TryGetValue(id, out var s)) { s.CurrentMatch = null; s.Pending = null; }
    }

    public int FreeServers(string region)
    {
        lock (_lock)
        {
            Prune();
            return _servers.Values.Count(x => x.Region == region && x.CurrentMatch == null);
        }
    }

    public IReadOnlyList<GameServer> All()
    {
        lock (_lock) { Prune(); return _servers.Values.ToList(); }
    }

    void Prune()
    {
        long now = TokenService.Now();
        foreach (var dead in _servers.Values.Where(s => now - s.LastHeartbeat > HeartbeatTimeoutSec).ToList())
            Drop(dead);
    }

    void Drop(GameServer s)
    {
        _servers.Remove(s.Id);
        if (s.CurrentMatch != null) _lost.Add(new LostMatch(s.CurrentMatch, s.Pending == null));
    }
}
