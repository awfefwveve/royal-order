using System;

namespace KnifeWar.Core
{
    public enum PlayerClass : byte
    {
        Knight = 0,  // 황실기사단 — 칼
        King = 1,    // 왕 — 팀당 1명, 소환·버프만 (직접 공격 없음)
    }

    public enum RoundPhase : byte
    {
        WaitingForPlayers = 0,
        Select = 1,    // 직업 선택 (준비 단계)
        Live = 2,
        RoundEnd = 3,
        MatchEnd = 4,
    }

    /// <summary>
    /// 라운드 규칙 수치. 승리 조건과 라운드 수는 기획 미정이라 임시값.
    /// </summary>
    public struct RoundConfig
    {
        public int PlayersPerTeam;
        public int RoundsToWin;
        public int SelectSeconds;
        public int RoundSeconds;
        public int RoundEndSeconds;

        public static RoundConfig Default => new RoundConfig
        {
            PlayersPerTeam = 6,
            RoundsToWin = 7,        // 임시: 7선승
            SelectSeconds = 15,
            RoundSeconds = 120,     // 임시
            RoundEndSeconds = 5,
        };
    }

    /// <summary>
    /// 발로란트식 라운드 진행 (서버 전용, 넷코드 독립).
    /// - 왕은 팀원 투표로 뽑는다. 1라운드와 그 뒤 3라운드마다(1, 4, 7, …) 준비 단계에 투표.
    ///   준비 단계에서 "왕 희망"(후보)을 켜고, 팀원은 후보 중 한 명에게 투표. 가장 많은 표가 왕.
    ///   무표·동점이면: 1라운드는 희망자 중 무작위(희망자가 없으면 팀원 중 무작위), 그 뒤는 이전 왕이 그대로.
    ///   투표가 없는 라운드는 왕 유지. 왕이 나갔으면 그 라운드 시작 때 무작위로 새 왕.
    /// - 라운드 중 부활 없음.
    /// - 승리: 상대 왕이 죽으면 즉시 라운드 승리. 왕이 없는 팀은 전멸로 판정.
    ///   시간 초과 시 생존자 많은 팀, 같으면 무승부(점수 없음).
    /// </summary>
    public sealed class RoundRules
    {
        public readonly RoundConfig Config;
        public readonly int TickRate;
        public readonly int MaxPlayers;

        public RoundPhase Phase { get; private set; } = RoundPhase.WaitingForPlayers;
        public int RoundNumber { get; private set; }
        public int PhaseEndTick { get; private set; }
        public int LastRoundWinner { get; private set; } = -1;  // -1 = 무승부
        public int MatchWinner { get; private set; } = -1;
        public readonly int[] Score = new int[2];

        public readonly byte[] Team;
        public readonly bool[] Connected;
        public readonly bool[] Alive;
        public readonly PlayerClass[] Class;
        readonly PlayerClass?[] _pick;      // 준비 단계: King = 왕 희망(후보)
        readonly int[] _vote;               // 준비 단계: 투표한 팀원 칸 (-1 = 안 함)
        readonly bool[] _kingAtStart;
        readonly Random _rng;

        /// <summary>왕 투표를 하는 라운드 간격 (1, 4, 7, …).</summary>
        public const int VoteEveryRounds = 3;
        public static bool IsVoteRound(int round) => round >= 1 && (round - 1) % VoteEveryRounds == 0;
        public bool VoteRoundNow => Phase == RoundPhase.Select && IsVoteRound(RoundNumber);
        /// <summary>다음에 왕 투표가 있는 라운드 번호.</summary>
        public int NextVoteRound => RoundNumber <= 0 ? 1 : ((RoundNumber - 1) / VoteEveryRounds + 1) * VoteEveryRounds + 1;

        public event Action<RoundPhase> PhaseChanged;

        public RoundRules(RoundConfig config, int tickRate) : this(config, tickRate, Environment.TickCount) { }

        public RoundRules(RoundConfig config, int tickRate, int seed)
        {
            Config = config;
            TickRate = tickRate;
            MaxPlayers = config.PlayersPerTeam * 2;
            Team = new byte[MaxPlayers];
            Connected = new bool[MaxPlayers];
            Alive = new bool[MaxPlayers];
            DeadTick = new int[MaxPlayers];
            Class = new PlayerClass[MaxPlayers];
            _pick = new PlayerClass?[MaxPlayers];
            _vote = new int[MaxPlayers];
            _kingAtStart = new bool[MaxPlayers];
            _rng = new Random(seed);
            for (int i = 0; i < MaxPlayers; i++) { Team[i] = (byte)(i < config.PlayersPerTeam ? 0 : 1); _vote[i] = -1; }
        }

        public int SecondsLeft(int tick) => Math.Max(0, (PhaseEndTick - tick + TickRate - 1) / TickRate);

        /// <summary>
        /// 투표 라운드의 준비 단계에서만: 기본으로 팀원 전원이 후보. Knight = "왕 안 할래"(후보에서 빠짐), King = 다시 후보.
        /// 실제 왕은 라운드 시작 때 투표 결과로 정해진다 (1표라도 단독 최다면 그 사람).
        /// </summary>
        public bool PickClass(int player, PlayerClass c)
        {
            if (!VoteRoundNow || player < 0 || player >= MaxPlayers || !Connected[player]) return false;
            _pick[player] = c;
            return true;
        }

        /// <summary>투표 라운드의 준비 단계에서만: 같은 팀 후보에게 한 표 (다시 누르면 바꿈, -1 이면 취소).</summary>
        public bool Vote(int player, int target)
        {
            if (!VoteRoundNow || player < 0 || player >= MaxPlayers || !Connected[player]) return false;
            if (target < 0) { _vote[player] = -1; return true; }
            if (target >= MaxPlayers || !Connected[target] || Team[target] != Team[player] || !IsCandidate(target)) return false;
            _vote[player] = target;
            return true;
        }

        /// <summary>준비 단계 표시용: 후보면 King, 아니면 Knight (투표가 없는 라운드는 현재 직업).</summary>
        public PlayerClass GetPick(int player) => _pick[player] ?? Class[player];
        /// <summary>후보: 접속해 있고 "왕 안 할래"를 누르지 않은 모든 팀원 (기본 전원).</summary>
        public bool IsCandidate(int player) => player >= 0 && player < MaxPlayers && Connected[player] && _pick[player] != PlayerClass.Knight;
        public int VoteOf(int player) => _vote[player];

        public int VoteCount(int candidate)
        {
            int n = 0;
            for (int i = 0; i < MaxPlayers; i++) if (Connected[i] && _vote[i] == candidate) n++;
            return n;
        }

        /// <summary>지금 그 팀의 왕 칸 (-1 = 없음).</summary>
        public int KingOf(int team)
        {
            for (int i = 0; i < MaxPlayers; i++) if (Team[i] == team && Connected[i] && Class[i] == PlayerClass.King) return i;
            return -1;
        }

        public void Kill(int player)
        {
            if (Phase == RoundPhase.Live) { Alive[player] = false; DeadTick[player] = _tick; }
        }

        /// <summary>죽은 틱 (현현 때 가장 오래 죽어 있던 기사부터).</summary>
        public readonly int[] DeadTick;
        int _tick;

        /// <summary>왕의 현현: 죽은 아군 기사를 되살린다. 라운드 중·접속 중·기사만. 성공하면 true.</summary>
        public bool Revive(int player)
        {
            if (Phase != RoundPhase.Live || !Connected[player] || Alive[player] || Class[player] != PlayerClass.Knight) return false;
            Alive[player] = true;
            return true;
        }

        /// <summary>팀에서 되살릴 기사: 가장 오래 죽어 있던 사람. 없으면 -1.</summary>
        public int OldestDead(int team)
        {
            int best = -1;
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (Team[i] != team || !Connected[i] || Alive[i] || Class[i] != PlayerClass.Knight) continue;
                if (best < 0 || DeadTick[i] < DeadTick[best]) best = i;
            }
            return best;
        }

        public void SetConnected(int player, bool connected)
        {
            Connected[player] = connected;
            if (!connected) Alive[player] = false; // 나가면 그 라운드는 사망 처리
        }

        /// <summary>
        /// 한 틱 진행. tieBreak: 시간 초과인데 양 팀 생존자 수가 같을 때 승자를 정하는 함수 (예: 필드 몬스터 체력 합이 큰 팀). null·-1 이면 무승부.
        /// </summary>
        public void Tick(int tick, Func<int> tieBreak = null)
        {
            _tick = tick;
            switch (Phase)
            {
                case RoundPhase.WaitingForPlayers:
                    if (TeamHasPlayer(0) && TeamHasPlayer(1)) StartSelect(tick);
                    break;

                case RoundPhase.Select:
                    if (tick >= PhaseEndTick) StartLive(tick);
                    break;

                case RoundPhase.Live:
                {
                    // 왕이 죽으면 그 팀은 바로 라운드 패배 (둘 다 같은 틱이면 무승부)
                    bool k0 = KingDown(0), k1 = KingDown(1);
                    if (k0 || k1) { EndRound(tick, k0 && k1 ? -1 : (k0 ? 1 : 0)); break; }
                    int a0 = AliveCount(0), a1 = AliveCount(1);
                    if (a0 == 0 || a1 == 0) EndRound(tick, a0 == 0 && a1 == 0 ? -1 : (a0 == 0 ? 1 : 0));
                    else if (tick >= PhaseEndTick) EndRound(tick, a0 == a1 ? (tieBreak?.Invoke() ?? -1) : (a0 > a1 ? 0 : 1));
                    break;
                }

                case RoundPhase.RoundEnd:
                    if (tick >= PhaseEndTick) StartSelect(tick);
                    break;
            }
        }

        /// <summary>이번 라운드를 왕으로 시작한 사람이 죽었는지 (왕이 없던 팀은 false → 전멸 규칙).</summary>
        public bool KingDown(int team)
        {
            for (int i = 0; i < MaxPlayers; i++)
                if (Team[i] == team && _kingAtStart[i] && !Alive[i]) return true;
            return false;
        }

        public int AliveCount(int team)
        {
            int n = 0;
            for (int i = 0; i < MaxPlayers; i++) if (Team[i] == team && Alive[i]) n++;
            return n;
        }

        bool TeamHasPlayer(int team)
        {
            for (int i = 0; i < MaxPlayers; i++) if (Team[i] == team && Connected[i]) return true;
            return false;
        }

        void StartSelect(int tick)
        {
            RoundNumber++;
            for (int i = 0; i < MaxPlayers; i++) { _pick[i] = null; _vote[i] = -1; }
            SetPhase(RoundPhase.Select, tick + Config.SelectSeconds * TickRate);
        }

        void StartLive(int tick)
        {
            for (int team = 0; team < 2; team++)
            {
                int king = IsVoteRound(RoundNumber) ? ResolveVote(team) : KingOf(team);
                if (king < 0) king = RandomTeammate(team, true);   // 왕이 나갔거나 없음 → 무작위
                for (int i = 0; i < MaxPlayers; i++)
                    if (Team[i] == team) Class[i] = i == king ? PlayerClass.King : PlayerClass.Knight;
            }
            for (int i = 0; i < MaxPlayers; i++)
            {
                _pick[i] = null;
                _vote[i] = -1;
                Alive[i] = Connected[i];
                _kingAtStart[i] = Alive[i] && Class[i] == PlayerClass.King;
            }
            SetPhase(RoundPhase.Live, tick + Config.RoundSeconds * TickRate);
        }

        /// <summary>투표 결과 → 왕 칸. 최다 득표(단독)면 그 사람, 아니면 1라운드는 희망자 중 무작위, 그 뒤는 이전 왕.</summary>
        int ResolveVote(int team)
        {
            int best = -1, bestVotes = 0; bool tie = false;
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (Team[i] != team || !Connected[i] || !IsCandidate(i)) continue;
                int v = VoteCount(i);
                if (v > bestVotes) { best = i; bestVotes = v; tie = false; }
                else if (v == bestVotes && v > 0) tie = true;
            }
            if (best >= 0 && bestVotes > 0 && !tie) return best;
            if (RoundNumber == 1) return RandomTeammate(team, true);
            int prev = KingOf(team);
            return prev >= 0 ? prev : RandomTeammate(team, true);
        }

        /// <summary>희망자 중 무작위, 없으면(preferCandidates) 팀원 전체 중 무작위. 아무도 없으면 -1.</summary>
        int RandomTeammate(int team, bool preferCandidates)
        {
            int n = 0;
            for (int pass = preferCandidates ? 0 : 1; pass < 2; pass++)
            {
                n = 0;
                for (int i = 0; i < MaxPlayers; i++)
                    if (Team[i] == team && Connected[i] && (pass == 1 || IsCandidate(i))) n++;
                if (n > 0)
                {
                    int k = _rng.Next(n);
                    for (int i = 0; i < MaxPlayers; i++)
                        if (Team[i] == team && Connected[i] && (pass == 1 || IsCandidate(i)) && k-- == 0) return i;
                }
            }
            return -1;
        }

        void EndRound(int tick, int winner)
        {
            LastRoundWinner = winner;
            if (winner >= 0) Score[winner]++;

            if (winner >= 0 && Score[winner] >= Config.RoundsToWin)
            {
                MatchWinner = winner;
                SetPhase(RoundPhase.MatchEnd, int.MaxValue);
                return;
            }
            SetPhase(RoundPhase.RoundEnd, tick + Config.RoundEndSeconds * TickRate);
        }

        /// <summary>클라이언트: 서버가 보낸 라운드 상태를 그대로 반영 (클라는 라운드를 직접 진행하지 않는다).</summary>
        public void ApplyMirror(RoundPhase phase, int roundNumber, int phaseEndTick, int score0, int score1, int lastWinner, int matchWinner)
        {
            RoundNumber = roundNumber;
            PhaseEndTick = phaseEndTick;
            Score[0] = score0;
            Score[1] = score1;
            LastRoundWinner = lastWinner;
            MatchWinner = matchWinner;
            if (Phase != phase)
            {
                Phase = phase;
                PhaseChanged?.Invoke(phase);
            }
        }

        /// <summary>클라이언트: 후보·투표 현황 반영 (준비 단계에 팀원 선택을 실시간으로 보여주기 위해).</summary>
        public void ApplyPickMirror(int player, PlayerClass pick) => _pick[player] = pick;
        public void ApplyVoteMirror(int player, int vote) => _vote[player] = vote;

        void SetPhase(RoundPhase phase, int endTick)
        {
            Phase = phase;
            PhaseEndTick = endTick;
            PhaseChanged?.Invoke(phase);
        }
    }
}
