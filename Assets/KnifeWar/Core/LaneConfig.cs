using Unity.Mathematics;

namespace KnifeWar.Core
{
    /// <summary>
    /// 외나무다리 레인 설정.
    /// 모든 길이는 mm 단위 정수 — 서버와 클라이언트가 똑같은 결과를 내도록(결정적 계산) float를 쓰지 않는다.
    /// 레인은 양쪽 성 안(2층 왕의 방)까지 이어진다: 팀0 성 뒤 = 0, 팀1 성 뒤 = LengthMm.
    /// </summary>
    public struct LaneConfig
    {
        /// <summary>레인 전체 길이 (mm). 팀0 진영 끝 = 0, 팀1 진영 끝 = LengthMm.</summary>
        public int LengthMm;

        /// <summary>다리 폭의 절반 (mm). 몬스터 좌우 오프셋 범위 = [-HalfWidthMm, +HalfWidthMm].</summary>
        public int HalfWidthMm;

        /// <summary>공간 분할 칸 크기 (mm). 칼 판정·타겟 탐색 시 근처 칸만 검사한다.</summary>
        public int CellSizeMm;

        /// <summary>소환수가 나타나는 곳: 레인 끝에서 이만큼 안쪽 (성 1층 출구). 팀1은 LengthMm - SpawnMm.</summary>
        public int SpawnMm;

        public int CellCount => (LengthMm + CellSizeMm - 1) / CellSizeMm;

        public int CellOf(int posMm) => math.clamp(posMm / CellSizeMm, 0, CellCount - 1);

        public int SpawnPos(int team) => team == 0 ? SpawnMm : LengthMm - SpawnMm;

        public static LaneConfig Default => new LaneConfig
        {
            LengthMm = (int)(MapLayout.LaneHalf * 2000f),   // 152m (다리 120m + 양쪽 성 16m)
            HalfWidthMm = 6_000,                              // 폭 12m (기사들이 좌우로 보법을 밟을 수 있게)
            CellSizeMm = 1_000,                               // 1m 칸
            SpawnMm = (int)((MapLayout.LaneHalf - MapLayout.GateX - 2f) * 1000f),   // 성문 안쪽 2m (1층 홀)
        };
    }

    /// <summary>
    /// 맵 치수 (m, 월드 좌표). 씬 생성기·몬스터 그리기·봇이 같은 값을 본다.
    /// 다리는 X축: 팀0 성 ← -x … +x → 팀1 성. 다리 윗면 y = 0.
    /// 성: 성문(GateX)에서 들어가면 1층 홀, 경사로(RampStartX→RampEndX)를 올라가면 2층 왕의 방(Floor2Y).
    /// 몬스터 시뮬레이션은 2D(레인 거리·좌우)라서, 높이는 HeightAt 으로 그릴 때만 올린다 — 경사로 모양과 같은 식.
    /// </summary>
    public static class MapLayout
    {
        public const float BridgeHalf = 60f;   // 다리 끝 = 성문
        public const float GateX = 60f;
        public const float HallEndX = 64f;     // 1층 홀 깊이 끝 (여기부터 경사로)
        public const float RampStartX = 64f;
        public const float RampEndX = 71f;
        public const float BackX = 76f;        // 2층 끝 (레인 끝)
        public const float LaneHalf = 76f;     // 레인 좌표 원점 = -LaneHalf
        public const float Floor2Y = 3.5f;     // 2층 바닥 높이
        public const float CastleHalfWidth = 9f;
        public const float TableX = 73.5f;     // 왕의 테이블 (성 뒤쪽 기준 |x|)

        /// <summary>월드 x 에서의 바닥 높이 (다리 0, 경사로, 2층).</summary>
        public static float HeightAt(float worldX)
        {
            float ax = math.abs(worldX);
            if (ax <= RampStartX) return 0f;
            if (ax >= RampEndX) return Floor2Y;
            return (ax - RampStartX) / (RampEndX - RampStartX) * Floor2Y;
        }

        /// <summary>레인 거리(mm) → 월드 x.</summary>
        public static float WorldX(int laneMm) => laneMm / 1000f - LaneHalf;
        public static int LaneX(float worldX) => (int)math.round((worldX + LaneHalf) * 1000f);
    }
}
