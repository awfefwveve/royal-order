using Unity.Mathematics;

namespace KnifeWar.Core
{
    /// <summary>
    /// 외나무다리 레인 설정.
    /// 모든 길이는 mm 단위 정수 — 서버와 클라이언트가 똑같은 결과를 내도록(결정적 계산) float를 쓰지 않는다.
    /// </summary>
    public struct LaneConfig
    {
        /// <summary>다리 전체 길이 (mm). 팀0 진영 = 0, 팀1 진영 = LengthMm.</summary>
        public int LengthMm;

        /// <summary>다리 폭의 절반 (mm). 몬스터 좌우 오프셋 범위 = [-HalfWidthMm, +HalfWidthMm].</summary>
        public int HalfWidthMm;

        /// <summary>공간 분할 칸 크기 (mm). 칼 판정·타겟 탐색 시 근처 칸만 검사한다.</summary>
        public int CellSizeMm;

        public int CellCount => (LengthMm + CellSizeMm - 1) / CellSizeMm;

        public int CellOf(int posMm) => math.clamp(posMm / CellSizeMm, 0, CellCount - 1);

        public static LaneConfig Default => new LaneConfig
        {
            LengthMm = 120_000,   // 120m
            HalfWidthMm = 2_000,  // 폭 4m
            CellSizeMm = 1_000,   // 1m 칸
        };
    }
}
