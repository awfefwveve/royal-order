using System;

namespace KnifeWar.Core
{
    /// <summary>
    /// 왕의 카드 경제 (서버 전용, 클라는 미러만).
    /// - 왕마다 마나가 차고(기본 12/초, 마나 우물마다 +3), 카드 비용을 내고 쓴다.
    /// - 덱 8장은 라운드마다 섞고 손패 4장을 뽑는다. 쓴 카드는 덱 맨 뒤로, 다음 카드를 뽑는다 (카드는 무한이 아니라 마나·순환으로 제한).
    /// - 소환 카드 → 성 1층 출구에 몬스터. 팀 전체 필드 몬스터 상한을 넘으면 거부.
    /// - 건물 카드 → 팀 건물 (병영: 12초마다 졸병, 마나 우물: 마나 회복). 팀당 개수 제한.
    /// - 필드 카드(볼케이노) → 팀 필드 효과 60초 + 화염 골렘 1마리. 화염 속성은 체력·속도 증가 (MonsterSim.TeamFire).
    /// - 버프 카드(진군의 나팔) → 아군 기사 이동 속도 8초.
    /// 모든 계산은 정수·고정 시드라 서버 재현이 가능하다.
    /// </summary>
    public sealed class WizardRules
    {
        public const int MaxMana = 100;
        public const int HandSize = CardDefs.HandSize;

        public readonly int TickRate;
        public readonly int MaxPlayers;
        public int TeamMonsterCap;

        // 마나는 정수(밀리마나)로 관리해서 서버 계산을 결정적으로 유지
        readonly int[] _milliMana;
        readonly int _regenMilliPerTick;
        readonly int _wellMilliPerTick;

        // 덱·손패
        readonly byte[][] _deck;        // 플레이어별 8장 (순환 순서)
        readonly int[] _cursor;         // 다음에 뽑을 덱 위치
        readonly byte[][] _hand;        // 플레이어별 4장 (CardDefs.None = 비어 있음)
        readonly uint[] _rng;

        public readonly BuffSlot[] TeamBuffs = { new BuffSlot(), new BuffSlot() };
        public readonly BuffSlot[] TeamField = { new BuffSlot(), new BuffSlot() };
        public readonly TeamBuildings[] Buildings = { new TeamBuildings(), new TeamBuildings() };

        public WizardRules(int maxPlayers, int tickRate, int teamMonsterCap, float manaPerSecond = 12f)
        {
            MaxPlayers = maxPlayers;
            TickRate = tickRate;
            TeamMonsterCap = teamMonsterCap;
            _milliMana = new int[maxPlayers];
            _regenMilliPerTick = Math.Max(1, (int)Math.Round(manaPerSecond * 1000f / tickRate));
            _wellMilliPerTick = Math.Max(1, (int)Math.Round(BuildingType.ManaWellPerSec * 1000f / tickRate));
            _deck = new byte[maxPlayers][];
            _hand = new byte[maxPlayers][];
            _cursor = new int[maxPlayers];
            _rng = new uint[maxPlayers];
            for (int p = 0; p < maxPlayers; p++)
            {
                _deck[p] = CardDefs.Normalize(null);
                _hand[p] = new byte[HandSize];
                for (int i = 0; i < HandSize; i++) _hand[p][i] = CardDefs.None;
            }
        }

        // ---- 덱 ----

        /// <summary>플레이어 덱 지정 (입장 때). 규칙에 안 맞으면 기본 덱. 다음 라운드부터 적용.</summary>
        public void SetDeck(int player, byte[] deck) => _deck[player] = CardDefs.Normalize(deck);

        public byte[] DeckOf(int player) => _deck[player];
        public byte Hand(int player, int slot) => slot >= 0 && slot < HandSize ? _hand[player][slot] : CardDefs.None;
        public byte NextCard(int player) => _deck[player][_cursor[player] % CardDefs.DeckSize];

        public static int CostOf(int cardId) => CardDefs.IsValidId(cardId) ? CardDefs.Get(cardId).Cost : int.MaxValue;

        public float Mana(int player) => _milliMana[player] / 1000f;
        public bool CanAfford(int player, int cardId) => CardDefs.IsValidId(cardId) && _milliMana[player] >= CostOf(cardId) * 1000;

        // ---- 클라이언트 미러 ----

        public void SetManaMirror(int player, float mana) => _milliMana[player] = (int)(mana * 1000f);

        public void SetHandMirror(int player, byte c0, byte c1, byte c2, byte c3, byte next)
        {
            var h = _hand[player];
            h[0] = c0; h[1] = c1; h[2] = c2; h[3] = c3;
            // 다음 카드: 덱 첫 칸에 두고 커서 0 (클라는 순서를 몰라도 다음 한 장만 보여주면 된다)
            _deck[player][0] = CardDefs.IsValidId(next) ? next : _deck[player][0];
            _cursor[player] = 0;
        }

        public void SetFieldMirror(int team, int fieldId, int endTick)
        {
            if (endTick > 0) TeamField[team].Apply(fieldId, endTick); else TeamField[team].Clear();
        }

        // ---- 라운드 ----

        /// <summary>라운드 시작: 마나 초기화, 덱 섞고 손패 뽑기, 건물·필드·버프 제거.</summary>
        public void ResetForRound(int round = 1, int seed = 0, int startMana = 30)
        {
            for (int p = 0; p < MaxPlayers; p++)
            {
                _milliMana[p] = startMana * 1000;
                _rng[p] = (uint)(seed * 2654435761u) ^ (uint)(p * 40503) ^ (uint)(round * 104729) ^ 0x9E3779B9u;
                if (_rng[p] == 0) _rng[p] = 1;
                Shuffle(p);
                _cursor[p] = 0;
                for (int i = 0; i < HandSize; i++) _hand[p][i] = Draw(p);
            }
            TeamBuffs[0].Clear();
            TeamBuffs[1].Clear();
            TeamField[0].Clear();
            TeamField[1].Clear();
            Buildings[0].Clear();
            Buildings[1].Clear();
        }

        uint NextRand(int p)
        {
            uint x = _rng[p];
            x ^= x << 13; x ^= x >> 17; x ^= x << 5;
            _rng[p] = x;
            return x;
        }

        void Shuffle(int p)
        {
            var d = _deck[p];
            for (int i = d.Length - 1; i > 0; i--)
            {
                int j = (int)(NextRand(p) % (uint)(i + 1));
                (d[i], d[j]) = (d[j], d[i]);
            }
        }

        byte Draw(int p)
        {
            var d = _deck[p];
            byte c = d[_cursor[p] % d.Length];
            _cursor[p] = (_cursor[p] + 1) % d.Length;
            return c;
        }

        /// <summary>쓴 카드를 덱 맨 뒤로 돌리고 다음 카드를 뽑는다.</summary>
        void Cycle(int p, int slot)
        {
            // 덱 배열은 순환 큐: 커서부터 (덱 8 - 손패 4) = 4장이 남은 순서, 그 앞 4칸은 손패로 나간 빈 자리.
            // 다음 카드를 커서에서 뽑고, 쓴 카드는 큐의 맨 뒤 = 커서 + 4 (빈 자리 중 가장 오래된 곳)에 넣는다.
            var d = _deck[p];
            int at = _cursor[p];
            byte next = d[at];
            d[(at + d.Length - HandSize) % d.Length] = _hand[p][slot];
            _hand[p][slot] = next;
            _cursor[p] = (at + 1) % d.Length;
        }

        /// <summary>왕이고 살아 있는 플레이어만 마나가 찬다 (마나 우물만큼 추가). 병영은 시간마다 졸병을 낸다.</summary>
        public void Tick(int tick, RoundRules round, MonsterSim sim = null)
        {
            for (int i = 0; i < MaxPlayers; i++)
            {
                if (round.Class[i] != PlayerClass.King || !round.Alive[i]) continue;
                int regen = _regenMilliPerTick + _wellMilliPerTick * Buildings[round.Team[i]].Count(BuildingType.ManaWell);
                _milliMana[i] = Math.Min(MaxMana * 1000, _milliMana[i] + regen);
            }
            if (sim == null) return;
            for (int team = 0; team < 2; team++)
            {
                var b = Buildings[team];
                int barracks = b.Count(BuildingType.Barracks);
                if (barracks == 0) continue;
                if (b.NextSpawnTick < 0) b.NextSpawnTick = tick + BuildingType.BarracksIntervalSec * TickRate;
                if (tick < b.NextSpawnTick) continue;
                b.NextSpawnTick = tick + BuildingType.BarracksIntervalSec * TickRate;
                for (int k = 0; k < barracks; k++)
                {
                    if (sim.AliveInTeam(team) >= TeamMonsterCap) break;
                    sim.TrySpawn((byte)team, MonsterDefs.Footman);
                }
            }
        }

        public enum SummonResult : byte { Ok, NotWizard, Dead, NotLive, NoMana, TeamCap, NoSlot, BadSlot, Limit }

        /// <summary>손패의 카드를 쓴다. 성공하면 효과(소환·건물·필드·버프)까지 적용하고 카드를 순환시킨다.</summary>
        public SummonResult TryPlay(int player, int handSlot, RoundRules round, MonsterSim sim, int tick, out int cardId)
        {
            cardId = -1;
            if (round.Phase != RoundPhase.Live) return SummonResult.NotLive;
            if (round.Class[player] != PlayerClass.King) return SummonResult.NotWizard;
            if (!round.Alive[player]) return SummonResult.Dead;
            if (handSlot < 0 || handSlot >= HandSize) return SummonResult.BadSlot;
            byte id = _hand[player][handSlot];
            if (!CardDefs.IsValidId(id)) return SummonResult.BadSlot;
            cardId = id;
            ref readonly var card = ref CardDefs.Get(id);

            int cost = card.Cost * 1000;
            if (_milliMana[player] < cost) return SummonResult.NoMana;
            byte team = round.Team[player];

            switch (card.Kind)
            {
                case CardKind.Summon:
                {
                    if (sim.AliveInTeam(team) >= TeamMonsterCap) return SummonResult.TeamCap;
                    if (!sim.TrySpawn(team, card.MonsterType)) return SummonResult.NoSlot;
                    break;
                }
                case CardKind.Field:
                {
                    if (TeamField[team].IsActive(tick)) return SummonResult.Limit;   // 같은 팀 필드는 동시에 하나
                    TeamField[team].Apply(card.Param, tick + card.DurationSec * TickRate);
                    sim.Complete();
                    sim.TeamFire[team] = (byte)(card.Param == FieldType.Volcano ? 1 : 0);
                    // 볼케이노: 우리 성에서 화염 골렘 1마리 (상한·슬롯이 없으면 골렘만 빠진다)
                    if (sim.AliveInTeam(team) < TeamMonsterCap) sim.TrySpawn(team, card.MonsterType);
                    break;
                }
                case CardKind.Building:
                {
                    var b = Buildings[team];
                    if (b.Count(card.Param) >= card.TeamLimit) return SummonResult.Limit;
                    b.Add(card.Param);
                    if (card.Param == BuildingType.Barracks && b.NextSpawnTick < 0)
                        b.NextSpawnTick = tick + BuildingType.BarracksIntervalSec * TickRate;
                    break;
                }
                case CardKind.Buff:
                {
                    TeamBuffs[team].Apply(card.Param, tick + card.DurationSec * TickRate);
                    break;
                }
            }

            _milliMana[player] -= cost;
            Cycle(player, handSlot);
            return SummonResult.Ok;
        }

        /// <summary>팀 필드가 화염(볼케이노)이고 아직 지속 중인가. 매 틱 MonsterSim.TeamFire 에 넣는다.</summary>
        public bool FireActive(int team, int tick) => TeamField[team].IsActive(tick) && TeamField[team].BuffId == FieldType.Volcano;

        /// <summary>어느 팀이든 볼케이노가 켜져 있나 (맵 배경 표시용).</summary>
        public bool VolcanoVisible(int tick) => FireActive(0, tick) || FireActive(1, tick);
    }

    /// <summary>팀 건물 (종류별 개수 + 병영 다음 생산 틱).</summary>
    public sealed class TeamBuildings
    {
        readonly int[] _count = new int[BuildingType.Count];
        public int NextSpawnTick = -1;

        public int Count(int type) => type >= 0 && type < _count.Length ? _count[type] : 0;
        public void Add(int type) { if (type >= 0 && type < _count.Length) _count[type]++; }
        public void Set(int type, int n) { if (type >= 0 && type < _count.Length) _count[type] = n; }
        public void Clear() { Array.Clear(_count, 0, _count.Length); NextSpawnTick = -1; }
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
