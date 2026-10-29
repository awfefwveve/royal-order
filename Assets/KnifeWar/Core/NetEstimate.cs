using Unity.Mathematics;

namespace KnifeWar.Core
{
    /// <summary>
    /// 몬스터 이벤트 동기화 방식의 트래픽 추정.
    /// 이동은 클라이언트가 같은 규칙으로 직접 계산하므로, 서버는 이벤트 + 소량의 위치 보정만 보낸다.
    /// 실제 넷코드를 붙이기 전에 "몬스터 수 → 트래픽 비용"을 미리 보기 위한 계산기다.
    /// </summary>
    public static class NetEstimate
    {
        public const int SpawnBytes = 10;      // 슬롯 u16 + 종류 u8 + 팀 u8 + 틱 u32 + 좌우 i16
        public const int HpBytes = 4;          // 슬롯 u16 + 체력 u16
        public const int DeathBytes = 2;       // 슬롯 u16
        public const int StateBytes = 7;       // 슬롯 u16 + 상태 u8 + 위치 i32
        public const int CorrectionBytes = 6;  // 슬롯 u16 + 위치 i32
        public const int PacketOverhead = 44;  // UDP/IP 28 + 넷코드 헤더 추정 16

        /// <summary>몬스터 전체 위치 보정을 몇 틱에 걸쳐 한 바퀴 돌릴지. 128틱 기준 64 = 0.5초마다 1회.</summary>
        public const int CorrectionPeriodTicks = 64;

        /// <summary>한 틱에 클라이언트 1명에게 보내는 몬스터 데이터 (바이트).</summary>
        public static int BytesPerTick(int spawn, int hp, int death, int state, int alive)
        {
            int corrections = (alive + CorrectionPeriodTicks - 1) / CorrectionPeriodTicks;
            return spawn * SpawnBytes + hp * HpBytes + death * DeathBytes + state * StateBytes
                   + corrections * CorrectionBytes;
        }

        /// <summary>클라이언트 1명당 송신량 (KB/s). 매 틱 패킷 1개 전송 가정.</summary>
        public static float KBytesPerSecPerClient(float avgBytesPerTick, int sendRate)
        {
            return (avgBytesPerTick + PacketOverhead) * sendRate / 1024f;
        }

        /// <summary>12명 한 판의 서버 송신량 (GB/시간).</summary>
        public static float GBPerMatchHour(float kbPerSecPerClient, int players = 12)
        {
            return kbPerSecPerClient * players * 3600f / (1024f * 1024f);
        }
    }
}
