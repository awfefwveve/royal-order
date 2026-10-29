using System;

namespace KnifeWar.Core
{
    public enum PlayerClass : byte
    {
        Knight = 0,  // 황실기사단 — 칼
        Wizard = 1,  // 황실마법사 — 소환·버프
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
    /// - 준비 단계에서 각자 기사/마법사 선택, 인원 제한 없음. 안 고르면 이전 라운드 직업 유지.
    /// - 라운드 중 부활 없음.
    /// - 승리(임시): 상대 기사 + 마법사 전멸. 시간 초과 시 생존자 많은 팀, 같으면 무승부(점수 없음).
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
        readonly PlayerClass?[] _pick;

        public event Action<RoundPhase> PhaseChanged;

        public RoundRules(RoundConfig config, int tickRate)
        {
            Config = config;
            TickRate = tickRate;
            MaxPlayers = config.PlayersPerTeam * 2;
            Team = new byte[MaxPlayers];
            Connected = new bool[MaxPlayers];
            Alive = new bool[MaxPlayers];
            Class = new PlayerClass[MaxPlayers];
            _pick = new PlayerClass?[MaxPlayers];
            for (int i = 0; i < MaxPlayers; i++) Team[i] = (byte)(i < config.PlayersPerTeam ? 0 : 1);
        }

        public int SecondsLeft(int tick) => Math.Max(0, (PhaseEndTick - tick + TickRate - 1) / TickRate);

        /// <summary>준비 단계에서만 받는다. 팀원에게 실시간으로 보여줄 값은 GetPick으로 읽는다.</summary>
        public bool PickClass(int player, PlayerClass c)
        {
            if (Phase != RoundPhase.Select || !Connected[player]) return false;
            _pick[player] = c;
            return true;
        }

        public PlayerClass GetPick(int player) => _pick[player] ?? Class[player];

        public void Kill(int player)
        {
            if (Phase == RoundPhase.Live) Alive[player] = false;
        }

        public void SetConnected(int player, bool connected)
        {
            Connected[player] = connected;
            if (!connected) Alive[player] = false; // 나가면 그 라운드는 사망 처리
        }

        public void Tick(int tick)
        {
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
                    int a0 = AliveCount(0), a1 = AliveCount(1);
                    if (a0 == 0 || a1 == 0) EndRound(tick, a0 == 0 && a1 == 0 ? -1 : (a0 == 0 ? 1 : 0));
                    else if (tick >= PhaseEndTick) EndRound(tick, a0 == a1 ? -1 : (a0 > a1 ? 0 : 1));
                    break;
                }

                case RoundPhase.RoundEnd:
                    if (tick >= PhaseEndTick) StartSelect(tick);
                    break;
            }
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
            for (int i = 0; i < MaxPlayers; i++) _pick[i] = null;
            SetPhase(RoundPhase.Select, tick + Config.SelectSeconds * TickRate);
        }

        void StartLive(int tick)
        {
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (_pick[i].HasValue) Class[i] = _pick[i].Value;
                Alive[i] = Connected[i];
            }
            SetPhase(RoundPhase.Live, tick + Config.RoundSeconds * TickRate);
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

        /// <summary>클라이언트: 직업 선택 현황 반영 (준비 단계에 팀원 선택을 실시간으로 보여주기 위해).</summary>
        public void ApplyPickMirror(int player, PlayerClass pick) => _pick[player] = pick;

        void SetPhase(RoundPhase phase, int endTick)
        {
            Phase = phase;
            PhaseEndTick = endTick;
            PhaseChanged?.Invoke(phase);
        }
    }
}
