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
    }

    /// <summary>
    /// 벤치마크용 임시 몬스터 3종. 실제 밸런스 값은 기획 확정 후 데이터 파일로 뺀다.
    /// </summary>
    public static class MonsterDefs
    {
        public const int TypeCount = 3;

        /// <summary>모든 몬스터 중 가장 큰 반지름. 공간 분할 탐색 여유값으로 쓴다.</summary>
        public const int MaxRadiusMm = 600;

        public static NativeArray<MonsterDef> Create(int tickRate, Allocator allocator)
        {
            var defs = new NativeArray<MonsterDef>(TypeCount, allocator);
            //                 속도(mm/s) 체력 반지름 사거리 데미지 공격간격(s)
            defs[0] = Make(tickRate, 3000, 100, 400, 600, 10, 0.8f);   // 졸병
            defs[1] = Make(tickRate, 2000, 300, 600, 800, 25, 1.2f);   // 탱커
            defs[2] = Make(tickRate, 4000, 60, 300, 2500, 8, 1.0f);    // 원거리
            return defs;
        }

        static MonsterDef Make(int tickRate, int speedMmPerSec, int hp, int radiusMm,
            int rangeMm, int damage, float intervalSec)
        {
            return new MonsterDef
            {
                SpeedMmPerTick = math.max(1, speedMmPerSec / tickRate),
                MaxHp = hp,
                RadiusMm = radiusMm,
                AttackRangeMm = rangeMm,
                AttackDamage = damage,
                AttackIntervalTicks = math.max(1, (int)math.round(intervalSec * tickRate)),
            };
        }
    }
}
