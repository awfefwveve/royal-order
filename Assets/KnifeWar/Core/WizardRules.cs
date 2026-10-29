using System;

namespace KnifeWar.Core
{
    /// <summary>
    /// 마법사 소환 경제 (서버 전용).
    /// - 마법사마다 마나가 차고, 몬스터 종류별 코스트를 내고 소환한다.
    /// - 팀 전체 필드 몬스터 상한(벤치마크로 확정할 값)을 넘으면 소환 거부. 마법사가 여러 명이면 상한을 나눠 쓴다.
    /// - 버프는 대상(몬스터/아군 기사)이 기획 미정이라, 지속시간이 있는 팀 버프 슬롯만 만들어 둔다.
    /// 수치는 전부 임시.
    /// </summary>
    public sealed class WizardRules
    {
        public const int MaxMana = 100;

        public readonly int TickRate;
        public readonly int MaxPlayers;
        public int TeamMonsterCap;

        // 마나는 정수(밀리마나)로 관리해서 서버 계산을 결정적으로 유지
        readonly int[] _milliMana;
        readonly int _regenMilliPerTick;
        static readonly int[] SummonCost = { 10, 30, 20 }; // 졸병, 탱커, 원거리

        public readonly BuffSlot[] TeamBuffs = { new BuffSlot(), new BuffSlot() };

        public WizardRules(int maxPlayers, int tickRate, int teamMonsterCap, float manaPerSecond = 12f)
        {
            MaxPlayers = maxPlayers;
            TickRate = tickRate;
            TeamMonsterCap = teamMonsterCap;
            _milliMana = new int[maxPlayers];
            _regenMilliPerTick = Math.Max(1, (int)Math.Round(manaPerSecond * 1000f / tickRate));
        }

        public static int CostOf(byte monsterType) => SummonCost[monsterType];

        public float Mana(int player) => _milliMana[player] / 1000f;

        /// <summary>클라이언트: 서버 값 반영.</summary>
        public void SetManaMirror(int player, float mana) => _milliMana[player] = (int)(mana * 1000f);

        public void ResetForRound(int startMana = 30)
        {
            for (int i = 0; i < MaxPlayers; i++) _milliMana[i] = startMana * 1000;
            TeamBuffs[0].Clear();
            TeamBuffs[1].Clear();
        }

        /// <summary>마법사이고 살아 있는 플레이어만 마나가 찬다.</summary>
        public void Tick(int tick, RoundRules round)
        {
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (round.Class[i] != PlayerClass.Wizard || !round.Alive[i]) continue;
                _milliMana[i] = Math.Min(MaxMana * 1000, _milliMana[i] + _regenMilliPerTick);
            }
        }

        public enum SummonResult : byte { Ok, NotWizard, Dead, NotLive, NoMana, TeamCap, NoSlot }

        /// <summary>소환 요청. 성공하면 MonsterSim에 실제로 생성까지 한다.</summary>
        public SummonResult TrySummon(int player, byte monsterType, RoundRules round, MonsterSim sim)
        {
            if (round.Phase != RoundPhase.Live) return SummonResult.NotLive;
            if (round.Class[player] != PlayerClass.Wizard) return SummonResult.NotWizard;
            if (!round.Alive[player]) return SummonResult.Dead;

            int cost = CostOf(monsterType) * 1000;
            if (_milliMana[player] < cost) return SummonResult.NoMana;

            byte team = round.Team[player];
            if (sim.AliveInTeam(team) >= TeamMonsterCap) return SummonResult.TeamCap;
            if (!sim.TrySpawn(team, monsterType)) return SummonResult.NoSlot;

            _milliMana[player] -= cost;
            return SummonResult.Ok;
        }

        /// <summary>팀 버프 시전 (임시: 같은 버프는 지속시간만 갱신).</summary>
        public bool TryBuff(int player, int buffId, int durationSec, int manaCost, int tick, RoundRules round)
        {
            if (round.Phase != RoundPhase.Live || round.Class[player] != PlayerClass.Wizard || !round.Alive[player]) return false;
            if (_milliMana[player] < manaCost * 1000) return false;
            _milliMana[player] -= manaCost * 1000;
            TeamBuffs[round.Team[player]].Apply(buffId, tick + durationSec * TickRate);
            return true;
        }
    }

    public sealed class BuffSlot
    {
        public int BuffId { get; private set; } = -1;
        public int EndTick { get; private set; }

        public bool IsActive(int tick) => BuffId >= 0 && tick < EndTick;

        public void Apply(int buffId, int endTick)
        {
            if (BuffId == buffId && endTick < EndTick) return;
            BuffId = buffId;
            EndTick = endTick;
        }

        public void Clear()
        {
            BuffId = -1;
            EndTick = 0;
        }
    }
}
