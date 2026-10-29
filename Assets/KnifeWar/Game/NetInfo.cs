namespace KnifeWar.Game
{
    /// <summary>네트워크 상태 표시용 (네트워크 코드가 채우고 HUD가 읽는다).</summary>
    public static class NetInfo
    {
        public static string Status = "";
        public static float PingMs = -1f;
        public static bool Unstable;      // 잠깐 스냅샷이 안 옴 (HUD에 경고)

        public static void Reset()
        {
            Status = "";
            PingMs = -1f;
            Unstable = false;
        }
    }
}
