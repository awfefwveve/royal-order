using System;
using Unity.Collections;
using Unity.Mathematics;

namespace KnifeWar.Core
{
    public enum HitPart : byte
    {
        None = 0,
        Head = 1,
        Chest = 2,
        Stomach = 3,
        Arm = 4,
        Leg = 5,
    }

    /// <summary>판정용 캡슐 (선분 A-B + 반지름). 월드 좌표, 미터 단위.</summary>
    public struct Capsule
    {
        public float3 A;
        public float3 B;
        public float Radius;
        public HitPart Part;
    }

    /// <summary>플레이어 한 명의 한 틱 상태 (판정에 필요한 것만).</summary>
    public struct PlayerPose
    {
        public float3 Root;   // 발 위치
        public float Yaw;     // 라디안, 0 = +Z 방향
        public float Crouch;  // 0 = 서 있음, 1 = 완전히 앉음 (중간값 = 앉는 중)
        public byte Alive;
    }

    /// <summary>
    /// 부위별 캡슐 히트박스. 지금은 고정 포즈 기준이고, 캐릭터 모델이 정해지면 뼈대 위치로 교체한다.
    /// 로컬 좌표: x = 오른쪽, y = 위, z = 앞. 서 있을 때 키 1.8m.
    /// </summary>
    public static class PlayerBody
    {
        public const int CapsuleCount = 9;
        public const float StandEyeHeight = 1.65f;
        public const float CrouchEyeHeight = 1.15f;

        // 앉으면 위쪽 부위가 이만큼 내려간다 (머리 기준)
        const float CrouchDrop = 0.5f;

        public static float EyeHeight(float crouch) => math.lerp(StandEyeHeight, CrouchEyeHeight, crouch);

        public static void Build(in PlayerPose pose, NativeArray<Capsule> outCaps, int offset)
        {
            float d = CrouchDrop * pose.Crouch;
            float s = math.sin(pose.Yaw), c = math.cos(pose.Yaw);

            // 위로 갈수록 앉을 때 더 많이 내려간다 (다리는 접히고 상체는 통째로 내려감)
            Set(outCaps, offset + 0, pose, s, c, new float3(0f, 1.62f - d, 0.02f), new float3(0f, 1.74f - d, 0.02f), 0.115f, HitPart.Head);
            Set(outCaps, offset + 1, pose, s, c, new float3(-0.12f, 1.28f - d, 0f), new float3(0.12f, 1.28f - d, 0f), 0.17f, HitPart.Chest);
            Set(outCaps, offset + 2, pose, s, c, new float3(-0.10f, 1.02f - d * 0.9f, 0f), new float3(0.10f, 1.02f - d * 0.9f, 0f), 0.15f, HitPart.Stomach);
            Set(outCaps, offset + 3, pose, s, c, new float3(-0.27f, 1.40f - d, 0f), new float3(-0.30f, 0.98f - d, 0.08f), 0.06f, HitPart.Arm);
            Set(outCaps, offset + 4, pose, s, c, new float3(0.27f, 1.40f - d, 0f), new float3(0.30f, 0.98f - d, 0.08f), 0.06f, HitPart.Arm);
            Set(outCaps, offset + 5, pose, s, c, new float3(-0.10f, 0.86f - d * 0.8f, 0f), new float3(-0.11f, 0.45f - d * 0.4f, 0.02f), 0.085f, HitPart.Leg);
            Set(outCaps, offset + 6, pose, s, c, new float3(0.10f, 0.86f - d * 0.8f, 0f), new float3(0.11f, 0.45f - d * 0.4f, 0.02f), 0.085f, HitPart.Leg);
            Set(outCaps, offset + 7, pose, s, c, new float3(-0.11f, 0.45f - d * 0.4f, 0.02f), new float3(-0.11f, 0.08f, 0f), 0.065f, HitPart.Leg);
            Set(outCaps, offset + 8, pose, s, c, new float3(0.11f, 0.45f - d * 0.4f, 0.02f), new float3(0.11f, 0.08f, 0f), 0.065f, HitPart.Leg);
        }

        static void Set(NativeArray<Capsule> o, int i, in PlayerPose p, float s, float c, float3 a, float3 b, float r, HitPart part)
        {
            o[i] = new Capsule { A = ToWorld(p.Root, s, c, a), B = ToWorld(p.Root, s, c, b), Radius = r, Part = part };
        }

        static float3 ToWorld(float3 root, float s, float c, float3 l)
        {
            // yaw 회전 (y축): 로컬 z(앞)가 (sin yaw, 0, cos yaw)가 되도록
            return new float3(root.x + l.x * c + l.z * s, root.y + l.y, root.z - l.x * s + l.z * c);
        }
    }

    /// <summary>
    /// 플레이어 위치 기록 (랙 보정용 링 버퍼).
    /// 클라이언트는 다른 플레이어를 두 틱 사이 보간해서 보므로, 되감기도 "틱 + 보간 비율"로 한다.
    /// </summary>
    public sealed class PlayerHistory : IDisposable
    {
        public readonly int MaxPlayers;
        public readonly int Length;
        NativeArray<PlayerPose> _buf;
        NativeArray<int> _tickOf;

        public PlayerHistory(int maxPlayers, int tickRate, int maxRewindMs = 250)
        {
            MaxPlayers = maxPlayers;
            Length = (int)math.ceil(maxRewindMs * tickRate / 1000f) + 3;
            _buf = new NativeArray<PlayerPose>(maxPlayers * Length, Allocator.Persistent);
            _tickOf = new NativeArray<int>(Length, Allocator.Persistent);
            for (int i = 0; i < Length; i++) _tickOf[i] = int.MinValue;
        }

        /// <summary>틱 끝에 모든 플레이어의 확정 상태를 기록한다.</summary>
        public void Record(int tick, NativeArray<PlayerPose> poses)
        {
            int slot = Mod(tick, Length);
            _tickOf[slot] = tick;
            for (int p = 0; p < MaxPlayers; p++) _buf[slot * MaxPlayers + p] = poses[p];
        }

        public bool Has(int tick) => _tickOf[Mod(tick, Length)] == tick;

        /// <summary>
        /// tick + alpha 시점의 자세 (alpha 0~1 = tick과 tick+1 사이).
        /// 기록 범위 밖이면 가장 가까운 기록으로 고정한다 (되감기 한도 = 핵 방지).
        /// </summary>
        public bool TryGet(int player, int tick, float alpha, int newestTick, out PlayerPose pose)
        {
            int oldest = newestTick - Length + 2;
            if (tick < oldest) { tick = oldest; alpha = 0f; }
            if (tick >= newestTick) { tick = newestTick; alpha = 0f; }

            if (!Has(tick)) { pose = default; return false; }
            var a = _buf[Mod(tick, Length) * MaxPlayers + player];
            if (alpha <= 0f || !Has(tick + 1)) { pose = a; return true; }

            var b = _buf[Mod(tick + 1, Length) * MaxPlayers + player];
            pose = new PlayerPose
            {
                Root = math.lerp(a.Root, b.Root, alpha),
                Yaw = LerpAngle(a.Yaw, b.Yaw, alpha),
                Crouch = math.lerp(a.Crouch, b.Crouch, alpha),
                // 보간 구간 중 한쪽이라도 죽어 있으면 판정하지 않는다
                Alive = (byte)(a.Alive != 0 && b.Alive != 0 ? 1 : 0),
            };
            return true;
        }

        static float LerpAngle(float a, float b, float t)
        {
            float d = b - a;
            while (d > math.PI) d -= 2f * math.PI;
            while (d < -math.PI) d += 2f * math.PI;
            return a + d * t;
        }

        static int Mod(int x, int m)
        {
            int r = x % m;
            return r < 0 ? r + m : r;
        }

        public void Dispose()
        {
            if (_buf.IsCreated) _buf.Dispose();
            if (_tickOf.IsCreated) _tickOf.Dispose();
        }
    }

    /// <summary>칼 한 번 휘두를 때의 판정 형태.</summary>
    public struct MeleeSweep
    {
        public float3 Eye;      // 공격자 눈 위치 (서버의 현재 공격자 위치 기준)
        public float3 Dir;      // 조준 방향
        public float Range;     // 사거리 (m)
        public float Radius;    // 스윕 두께 (m)
    }

    public struct MeleeHit
    {
        public int Victim;      // -1 = 빗나감
        public HitPart Part;
        public float Distance;  // 눈에서 맞은 지점까지 (m)
        public bool FromBehind; // 상대 등 뒤에서 맞춤
    }

    /// <summary>
    /// 칼 판정: 눈에서 조준 방향으로 두꺼운 선분(구체 스윕)을 쏴서, 되감은 적 히트박스 중 가장 먼저 닿는 부위를 찾는다.
    /// 같은 대상의 여러 부위가 겹치면 더 가까운 부위가 이긴다 (머리 우선 아님 — 실제로 먼저 닿은 곳).
    /// </summary>
    public static class MeleeResolver
    {
        /// <param name="teams">플레이어별 팀 (0/1)</param>
        /// <param name="scratch">CapsuleCount 이상 크기의 임시 배열</param>
        public static MeleeHit Resolve(in MeleeSweep sweep, int attacker, NativeArray<byte> teams,
            PlayerHistory history, int viewTick, float viewAlpha, int newestTick, NativeArray<Capsule> scratch)
        {
            var best = new MeleeHit { Victim = -1, Part = HitPart.None, Distance = float.MaxValue };
            float3 dir = math.normalizesafe(sweep.Dir);
            float3 a = sweep.Eye;
            float3 b = a + dir * sweep.Range;
            byte myTeam = teams[attacker];

            for (int p = 0; p < history.MaxPlayers; p++)
            {
                if (p == attacker || teams[p] == myTeam) continue;
                if (!history.TryGet(p, viewTick, viewAlpha, newestTick, out var pose) || pose.Alive == 0) continue;

                // 거리 컷: 몸통 중심이 사거리 + 2m 밖이면 건너뛴다
                float3 center = pose.Root + new float3(0f, 1f, 0f);
                float reach = sweep.Range + sweep.Radius + 1.2f;
                if (math.lengthsq(center - a) > reach * reach) continue;

                // 1단계: 두꺼운 스윕으로 "누구를 맞췄나" (관대하게 — 칼 체감)
                PlayerBody.Build(pose, scratch, 0);
                float firstS = float.MaxValue;
                for (int i = 0; i < PlayerBody.CapsuleCount; i++)
                {
                    var cap = scratch[i];
                    SegmentSegment(a, b, cap.A, cap.B, out float s, out float t, out float d2);
                    float r = sweep.Radius + cap.Radius;
                    if (d2 <= r * r && s < firstS) firstS = s;
                }

                float dist = firstS * sweep.Range;
                if (firstS == float.MaxValue || dist >= best.Distance) continue;

                // 2단계: 얇은 조준선으로 "어디를 맞췄나" (정확하게 — 두꺼운 스윕이 머리를 먼저 스치는 오판 방지)
                HitPart part = PrecisePart(a, b, scratch);
                float3 fwd = new float3(math.sin(pose.Yaw), 0f, math.cos(pose.Yaw));
                float3 flat = math.normalizesafe(new float3(dir.x, 0f, dir.z));
                best = new MeleeHit
                {
                    Victim = p,
                    Part = part,
                    Distance = dist,
                    // 공격 방향과 상대가 보는 방향이 같으면 = 등 뒤에서 찌름
                    FromBehind = math.dot(fwd, flat) > 0.5f,
                };
            }
            return best;
        }

        /// <summary>
        /// 조준선(두께 0)이 직접 닿는 부위 중 가장 앞의 것. 하나도 안 닿으면 조준선에 가장 가까운 부위.
        /// </summary>
        static HitPart PrecisePart(float3 a, float3 b, NativeArray<Capsule> caps)
        {
            HitPart direct = HitPart.None, nearest = HitPart.None;
            float directS = float.MaxValue, nearestGap = float.MaxValue;
            for (int i = 0; i < PlayerBody.CapsuleCount; i++)
            {
                var cap = caps[i];
                SegmentSegment(a, b, cap.A, cap.B, out float s, out float t, out float d2);
                if (d2 <= cap.Radius * cap.Radius)
                {
                    if (s < directS) { directS = s; direct = cap.Part; }
                }
                else
                {
                    float gap = math.sqrt(d2) - cap.Radius;
                    if (gap < nearestGap) { nearestGap = gap; nearest = cap.Part; }
                }
            }
            return direct != HitPart.None ? direct : nearest;
        }

        /// <summary>두 선분 사이 최근접점. s, t = 각 선분 위 비율(0~1), d2 = 거리 제곱.</summary>
        public static void SegmentSegment(float3 p1, float3 q1, float3 p2, float3 q2, out float s, out float t, out float d2)
        {
            float3 d1 = q1 - p1, dd2 = q2 - p2, r = p1 - p2;
            float a = math.dot(d1, d1), e = math.dot(dd2, dd2), f = math.dot(dd2, r);
            const float eps = 1e-8f;

            if (a <= eps && e <= eps) { s = t = 0f; d2 = math.lengthsq(p1 - p2); return; }
            if (a <= eps)
            {
                s = 0f;
                t = math.saturate(f / e);
            }
            else
            {
                float c = math.dot(d1, r);
                if (e <= eps)
                {
                    t = 0f;
                    s = math.saturate(-c / a);
                }
                else
                {
                    float b = math.dot(d1, dd2);
                    float denom = a * e - b * b;
                    s = denom != 0f ? math.saturate((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f) { t = 0f; s = math.saturate(-c / a); }
                    else if (t > 1f) { t = 1f; s = math.saturate((b - c) / a); }
                }
            }
            float3 c1 = p1 + d1 * s, c2 = p2 + dd2 * t;
            d2 = math.lengthsq(c1 - c2);
        }
    }
}
