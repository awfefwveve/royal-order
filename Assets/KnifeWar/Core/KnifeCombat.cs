using System;
using Unity.Collections;
using Unity.Mathematics;

namespace KnifeWar.Core
{
    public enum KnifeAttack : byte
    {
        None = 0,
        Slash = 1,  // 좌클
        Stab = 2,   // 우클
    }

    /// <summary>칼 공격 한 종류의 수치. ms로 정의하고 틱으로 변환해서 쓴다.</summary>
    public struct KnifeAttackDef
    {
        public float Range;
        public float Radius;
        public int Damage;
        public int WindupTicks;     // 클릭 → 판정까지
        public int RecoveryTicks;   // 판정 → 다음 공격 가능까지
        public float BackstabMultiplier;

        public static KnifeAttackDef Make(int tickRate, float range, float radius, int damage,
            int windupMs, int recoveryMs, float backstab)
        {
            return new KnifeAttackDef
            {
                Range = range,
                Radius = radius,
                Damage = damage,
                WindupTicks = math.max(0, (int)math.round(windupMs * tickRate / 1000f)),
                RecoveryTicks = math.max(1, (int)math.round(recoveryMs * tickRate / 1000f)),
                BackstabMultiplier = backstab,
            };
        }
    }

    /// <summary>
    /// 임시 수치. 블랙스쿼드 체감에 맞춰 플레이테스트로 조정한다.
    /// 체력 100 기준: 베기 몸통 2방, 찌르기 몸통 1방(앞 70/뒤 즉사), 머리는 더 강하게.
    /// </summary>
    public static class KnifeTuning
    {
        public const int MaxHp = 100;

        public static KnifeAttackDef Slash(int tickRate) => KnifeAttackDef.Make(tickRate, 1.6f, 0.30f, 50, 60, 380, 1.5f);
        public static KnifeAttackDef Stab(int tickRate) => KnifeAttackDef.Make(tickRate, 1.4f, 0.18f, 70, 220, 820, 3f);

        public static float PartMultiplier(HitPart part)
        {
            switch (part)
            {
                case HitPart.Head: return 1.6f;
                case HitPart.Chest: return 1.0f;
                case HitPart.Stomach: return 0.95f;
                case HitPart.Arm: return 0.75f;
                case HitPart.Leg: return 0.7f;
                default: return 0f;
            }
        }

        public static int ComputeDamage(in KnifeAttackDef def, in MeleeHit hit)
        {
            float d = def.Damage * PartMultiplier(hit.Part);
            if (hit.FromBehind) d *= def.BackstabMultiplier;
            return (int)math.round(d);
        }
    }

    /// <summary>클라이언트가 매 틱 보내는 입력 중 칼 관련 부분.</summary>
    public struct KnifeInput
    {
        public KnifeAttack Pressed;
        public float Yaw;
        public float Pitch;      // 라디안, + = 위
        public int ViewTick;     // 클라 화면에 보이던 다른 플레이어들의 틱 (보간 기준)
        public float ViewAlpha;  // 그 틱과 다음 틱 사이 비율
    }

    public struct KnifeHitEvent
    {
        public int Attacker;
        public int Victim;
        public KnifeAttack Attack;
        public HitPart Part;
        public bool FromBehind;
        public int Damage;
        public bool Kill;
    }

    /// <summary>판정 시점이 된 공격 한 번 (맞았든 안 맞았든). 몬스터 판정 등 추가 처리에 쓴다.</summary>
    public struct KnifeSwing
    {
        public int Attacker;
        public KnifeAttack Attack;
        public MeleeSweep Sweep;
        public bool HitPlayer;
        public float HitDistance;
    }

    /// <summary>
    /// 서버 권위 칼 전투. 클라이언트는 입력만 보내고, 맞았는지는 서버가 되감기로 결정한다.
    /// 사용 순서 (매 틱): SubmitInput(각 플레이어) → Tick() → 이벤트 처리 → History.Record()
    /// </summary>
    public sealed class KnifeCombat : IDisposable
    {
        public readonly int MaxPlayers;
        public readonly int TickRate;
        public readonly KnifeAttackDef SlashDef, StabDef;
        public readonly PlayerHistory History;

        public NativeArray<PlayerPose> Poses;   // 이번 틱 서버 확정 자세 (이동 코드가 채운다)
        public NativeArray<byte> Teams;
        public NativeArray<int> Hp;
        public NativeArray<byte> CanAttack;     // 기사만 1 (마법사는 칼 없음)

        // 공격 진행 상태
        NativeArray<int> _readyTick;
        NativeArray<byte> _pendingAttack;
        NativeArray<int> _fireTick;
        NativeArray<int> _inputTick;
        NativeArray<KnifeInput> _pendingInput;
        NativeArray<float> _aimYaw, _aimPitch;
        NativeArray<Capsule> _scratch;

        public NativeList<KnifeHitEvent> Events;
        public NativeList<KnifeSwing> Swings;
        public int Tick { get; private set; }

        public KnifeCombat(int maxPlayers, int tickRate)
        {
            MaxPlayers = maxPlayers;
            TickRate = tickRate;
            SlashDef = KnifeTuning.Slash(tickRate);
            StabDef = KnifeTuning.Stab(tickRate);
            History = new PlayerHistory(maxPlayers, tickRate);

            var p = Allocator.Persistent;
            Poses = new NativeArray<PlayerPose>(maxPlayers, p);
            Teams = new NativeArray<byte>(maxPlayers, p);
            Hp = new NativeArray<int>(maxPlayers, p);
            CanAttack = new NativeArray<byte>(maxPlayers, p);
            _readyTick = new NativeArray<int>(maxPlayers, p);
            _pendingAttack = new NativeArray<byte>(maxPlayers, p);
            _fireTick = new NativeArray<int>(maxPlayers, p);
            _inputTick = new NativeArray<int>(maxPlayers, p);
            _pendingInput = new NativeArray<KnifeInput>(maxPlayers, p);
            _aimYaw = new NativeArray<float>(maxPlayers, p);
            _aimPitch = new NativeArray<float>(maxPlayers, p);
            _scratch = new NativeArray<Capsule>(PlayerBody.CapsuleCount, p);
            Events = new NativeList<KnifeHitEvent>(32, p);
            Swings = new NativeList<KnifeSwing>(32, p);
        }

        public KnifeAttackDef Def(KnifeAttack a) => a == KnifeAttack.Stab ? StabDef : SlashDef;

        public bool IsBusy(int player) => _pendingAttack[player] != 0 || Tick < _readyTick[player];

        /// <summary>이번 틱 입력. 조준은 항상 갱신되고, 공격은 가능할 때만 시작된다.</summary>
        public void SubmitInput(int player, in KnifeInput input)
        {
            _aimYaw[player] = input.Yaw;
            _aimPitch[player] = math.clamp(input.Pitch, -1.55f, 1.55f);

            if (input.Pressed == KnifeAttack.None) return;
            if (CanAttack[player] == 0 || Poses[player].Alive == 0) return;
            if (IsBusy(player)) return;

            var def = Def(input.Pressed);
            _pendingAttack[player] = (byte)input.Pressed;
            _inputTick[player] = Tick;
            _fireTick[player] = Tick + def.WindupTicks;
            _pendingInput[player] = input;
        }

        /// <summary>
        /// 판정 시점이 된 공격을 처리한다.
        /// 공격자는 서버의 현재 위치·조준, 대상은 "클라가 그 순간 보던 화면"으로 되감는다.
        /// </summary>
        public void Step()
        {
            Events.Clear();
            Swings.Clear();

            for (int p = 0; p < MaxPlayers; p++)
            {
                if (_pendingAttack[p] == 0 || Tick < _fireTick[p]) continue;

                var attack = (KnifeAttack)_pendingAttack[p];
                var def = Def(attack);
                var input = _pendingInput[p];
                _pendingAttack[p] = 0;
                _readyTick[p] = Tick + def.RecoveryTicks;

                // 휘두르는 동안 죽었으면 판정 없음
                if (Poses[p].Alive == 0) continue;

                var pose = Poses[p];
                float yaw = _aimYaw[p], pitch = _aimPitch[p];
                float cp = math.cos(pitch);
                var sweep = new MeleeSweep
                {
                    Eye = pose.Root + new float3(0f, PlayerBody.EyeHeight(pose.Crouch), 0f),
                    Dir = new float3(math.sin(yaw) * cp, math.sin(pitch), math.cos(yaw) * cp),
                    Range = def.Range,
                    Radius = def.Radius,
                };

                // 클릭 이후 지난 시간만큼 클라 화면도 진행했다고 본다
                int viewTick = input.ViewTick + (Tick - _inputTick[p]);
                // 이번 틱 자세는 아직 기록 전이므로 최신 기록은 Tick - 1
                var hit = MeleeResolver.Resolve(sweep, p, Teams, History, viewTick, input.ViewAlpha, Tick - 1, _scratch);

                // 되감기 시점엔 살아 있었어도 지금 이미 죽었으면 데미지 없음
                bool valid = hit.Victim >= 0 && Hp[hit.Victim] > 0;
                Swings.Add(new KnifeSwing
                {
                    Attacker = p,
                    Attack = attack,
                    Sweep = sweep,
                    HitPlayer = valid,
                    HitDistance = valid ? hit.Distance : float.MaxValue,
                });
                if (!valid) continue;

                int dmg = KnifeTuning.ComputeDamage(def, hit);
                int hp = math.max(0, Hp[hit.Victim] - dmg);
                Hp[hit.Victim] = hp;
                if (hp == 0)
                {
                    var vp = Poses[hit.Victim];
                    vp.Alive = 0;
                    Poses[hit.Victim] = vp;
                    _pendingAttack[hit.Victim] = 0; // 죽은 사람의 공격은 취소
                }

                Events.Add(new KnifeHitEvent
                {
                    Attacker = p,
                    Victim = hit.Victim,
                    Attack = attack,
                    Part = hit.Part,
                    FromBehind = hit.FromBehind,
                    Damage = dmg,
                    Kill = hp == 0,
                });
            }
        }

        /// <summary>틱 마무리: 확정 자세를 기록하고 다음 틱으로.</summary>
        public void EndTick()
        {
            History.Record(Tick, Poses);
            Tick++;
        }

        public void ResetForRound()
        {
            for (int p = 0; p < MaxPlayers; p++)
            {
                Hp[p] = KnifeTuning.MaxHp;
                _pendingAttack[p] = 0;
                _readyTick[p] = 0;
            }
        }

        public void Dispose()
        {
            History.Dispose();
            if (Poses.IsCreated) Poses.Dispose();
            if (Teams.IsCreated) Teams.Dispose();
            if (Hp.IsCreated) Hp.Dispose();
            if (CanAttack.IsCreated) CanAttack.Dispose();
            if (_readyTick.IsCreated) _readyTick.Dispose();
            if (_pendingAttack.IsCreated) _pendingAttack.Dispose();
            if (_fireTick.IsCreated) _fireTick.Dispose();
            if (_inputTick.IsCreated) _inputTick.Dispose();
            if (_pendingInput.IsCreated) _pendingInput.Dispose();
            if (_aimYaw.IsCreated) _aimYaw.Dispose();
            if (_aimPitch.IsCreated) _aimPitch.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (Events.IsCreated) Events.Dispose();
            if (Swings.IsCreated) Swings.Dispose();
        }
    }
}
