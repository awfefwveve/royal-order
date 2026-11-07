using Unity.Collections;
using Unity.Mathematics;

namespace KnifeWar.Core
{
    /// <summary>몬스터 종류별 수치. 틱 단위로 변환해 저장한다.</summary>
    public struct MonsterDef
    {
        public int SpeedMmPerTick;
        public int MaxHp;
        public int RadiusMm;
        public int AttackRangeMm;
        public int AttackDamage;
        public int AttackIntervalTicks;
        public byte Attr;   // MonsterAttr (화염이면 볼케이노 때 체력·속도 증가)
    }

    /// <summary>
    /// 몬스터 7종 (임시 수치 — 밸런스는 플레이테스트로). 카드 → 종류는 CardDefs.
    /// 0 졸병, 1 방패병, 2 궁수, 3 오우거, 4 트윈헤드 오우거, 5 데스나이트, 6 화염 골렘(볼케이노 전용)
    /// </summary>
    public static class MonsterDefs
    {
        public const int TypeCount = 7;
        public const byte Footman = 0, Shield = 1, Archer = 2, Ogre = 3, TwinOgre = 4, DeathKnight = 5, FireGolem = 6;

        /// <summary>모든 몬스터 중 가장 큰 반지름. 공간 분할 탐색 여유값으로 쓴다.</summary>
        public const int MaxRadiusMm = 900;

        public static NativeArray<MonsterDef> Create(int tickRate, Allocator allocator)
        {
            var defs = new NativeArray<MonsterDef>(TypeCount, allocator);
            //                 속도(mm/s) 체력 반지름 사거리 데미지 공격간격(s)
            defs[0] = Make(tickRate, 3000, 100, 400, 600, 10, 0.8f);                         // 졸병
            defs[1] = Make(tickRate, 2000, 300, 600, 800, 25, 1.2f);                         // 방패병
            defs[2] = Make(tickRate, 3600, 60, 300, 2500, 8, 1.0f);                          // 궁수
            defs[3] = Make(tickRate, 2200, 700, 800, 1200, 45, 1.5f);                        // 오우거
            defs[4] = Make(tickRate, 2000, 1200, 900, 1300, 40, 0.9f);                       // 트윈헤드 오우거 (머리 둘 → 빠른 공격)
            defs[5] = Make(tickRate, 3200, 500, 500, 900, 60, 1.1f, MonsterAttr.Dark);       // 데스나이트
            defs[6] = Make(tickRate, 1800, 1500, 900, 1400, 70, 1.6f, MonsterAttr.Fire);     // 화염 골렘
            return defs;
        }

        static MonsterDef Make(int tickRate, int speedMmPerSec, int hp, int radiusMm,
            int rangeMm, int damage, float intervalSec, MonsterAttr attr = MonsterAttr.None)
        {
            return new MonsterDef
            {
                SpeedMmPerTick = math.max(1, speedMmPerSec / tickRate),
                MaxHp = hp,
                RadiusMm = radiusMm,
                AttackRangeMm = rangeMm,
                AttackDamage = damage,
                AttackIntervalTicks = math.max(1, (int)math.round(intervalSec * tickRate)),
                Attr = (byte)attr,
            };
        }
    }
}
