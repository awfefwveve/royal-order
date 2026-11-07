using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>한 틱의 조작 의도. 사람(키보드·마우스)·봇·네트워크 입력이 모두 같은 형식.</summary>
    public struct PlayerIntent
    {
        public Vector2 Move;        // x = 좌우, y = 앞뒤 (-1~1)
        public bool Jump;
        public bool Crouch;
        public bool Walk;           // 발로란트식 Shift 걷기
        public float Yaw;           // 도 (Unity Y 회전)
        public float Pitch;         // 도 (+ = 아래, Unity 카메라 기준)
        public KnifeAttack Attack;  // 이번에 누른 공격
        public sbyte Summon;        // -1 = 없음, 0~3 = 손패 카드 칸 (왕)
        public bool Resurrect;      // 왕: 현현 (마나 100으로 죽은 아군 기사 되살리기)
        public sbyte PickClass;     // -1 = 없음, 0 = 희망 취소(기사), 1 = 왕 희망 (투표 라운드 준비 단계)
        public sbyte Vote;          // -1 = 없음, 0~11 = 그 칸에 투표, 127 = 투표 취소
    }

    /// <summary>예측·보정에 필요한 이동 상태.</summary>
    public struct MoveState
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Crouch;
    }

    public enum AvatarDrive : byte
    {
        Simulated = 0,     // 이 컴퓨터가 틱마다 이동을 계산 (서버의 모든 플레이어, 클라의 내 캐릭터)
        Interpolated = 1,  // 서버 스냅샷 사이를 보간해서 보여주기만 함 (클라의 다른 플레이어)
    }

    /// <summary>
    /// 플레이어 한 명의 몸.
    /// - transform = 판정·물리 위치 (틱 단위로만 바뀜, 발 위치)
    /// - Visual = 화면에 보이는 몸 (프레임마다 두 틱 사이를 보간해서 부드럽게)
    /// 이동 코드는 서버와 클라가 같은 함수(SimulateTick)를 같은 dt로 돌린다 → 클라 예측이 서버와 일치.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerAvatar : MonoBehaviour
    {
        public const float RunSpeed = 6.2f;
        public const float WalkSpeed = 3.2f;
        public const float CrouchSpeed = 2.4f;
        const float Gravity = -22f;
        const float JumpSpeed = 6.4f;
        const float StandHeight = 1.8f;
        const float CrouchHeight = 1.3f;

        public int Index;
        public byte Team;
        public string DisplayName;
        public AvatarDrive Drive;

        public Transform Visual;
        public Transform Eye;
        public Renderer Body;
        public GameObject WizardHat;
        public Transform Knife;
        public AvatarModel Model;      // 외형 모델 (SkinSet에 있을 때만)
        public bool HideEyeKnife;      // 남이 볼 때: 모델 손에 칼이 있으면 눈앞 칼은 숨김

        public float Yaw;
        public float Pitch;
        public float Crouch;
        public bool Alive = true;
        public bool Frozen;
        public float SpeedMultiplier = 1f;
        public PlayerClass Class;
        public byte TeleportSeq;    // 서버가 순간이동시킬 때마다 증가 → 클라 예측이 그 지점으로 맞춤

        // 내 캐릭터는 시점을 프레임 단위로 즉시 반영 (틱 단위면 마우스가 끊겨 보인다)
        public bool LocalLook;
        public float LookYaw, LookPitch;

        CharacterController _cc;
        Vector3 _velocity;
        Vector3 _prevTickPos, _currTickPos;
        Vector3 _visualOffset;     // 예측 보정 시 화면이 튀지 않게 서서히 줄이는 오차
        float _knifeSwingT = -1f;
        KnifeAttack _knifeSwingType;

        public Vector3 Velocity => _velocity;

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _cc.height = StandHeight;
            _cc.radius = 0.35f;
            _cc.center = new Vector3(0f, StandHeight * 0.5f, 0f);
            _cc.stepOffset = 0.3f;
            _cc.slopeLimit = 50f;
            _cc.minMoveDistance = 0f;
        }

        public void SetDrive(AvatarDrive d)
        {
            Drive = d;
            _cc.enabled = Alive && d == AvatarDrive.Simulated;
        }

        public void Teleport(Vector3 feet, float yaw)
        {
            _cc.enabled = false;
            transform.position = feet;
            _cc.enabled = Alive && Drive == AvatarDrive.Simulated;
            Yaw = yaw;
            Pitch = 0f;
            Crouch = 0f;
            _velocity = Vector3.zero;
            transform.rotation = Quaternion.Euler(0f, Yaw, 0f);
            _prevTickPos = _currTickPos = feet;
            _visualOffset = Vector3.zero;
            TeleportSeq++;
            ApplyHeight();
        }

        public bool Present { get; private set; } = true;

        /// <summary>빈 칸: 통째로 숨긴다 (모델·이름표·판정 전부). 다시 사람이 들어오면 true.</summary>
        public void SetPresent(bool present)
        {
            if (Present == present) return;
            Present = present;
            if (Visual != null) Visual.gameObject.SetActive(present);
            if (!present) { Alive = false; _cc.enabled = false; }
        }

        public void SetAlive(bool alive)
        {
            Alive = alive;
            if (Body != null) Body.enabled = alive;
            if (WizardHat != null) WizardHat.SetActive(alive && Class == PlayerClass.King);
            _cc.enabled = alive && Drive == AvatarDrive.Simulated;
            if (Knife != null) Knife.gameObject.SetActive(alive && Class == PlayerClass.Knight && !HideEyeKnife);
            if (Model != null) Model.Refresh();
        }

        public void SetClass(PlayerClass c)
        {
            if (Class == c && _classApplied) return;
            _classApplied = true;
            Class = c;
            if (WizardHat != null) WizardHat.SetActive(Alive && c == PlayerClass.King);
            if (Knife != null) Knife.gameObject.SetActive(Alive && c == PlayerClass.Knight && !HideEyeKnife);
            if (Model != null) Model.Refresh();
        }

        bool _classApplied;

        /// <summary>눈앞 칼 표시 여부를 다시 맞춘다 (HideEyeKnife를 바꾼 뒤).</summary>
        public void RefreshKnife()
        {
            if (Knife != null) Knife.gameObject.SetActive(Alive && Class == PlayerClass.Knight && !HideEyeKnife);
        }

        // ------------------------------------------------------------------
        // 틱 이동 (서버·클라 공통)
        // ------------------------------------------------------------------

        public void SimulateTick(in PlayerIntent intent, float dt)
        {
            Yaw = intent.Yaw;
            Pitch = Mathf.Clamp(intent.Pitch, -88f, 88f);
            transform.rotation = Quaternion.Euler(0f, Yaw, 0f);
            if (!Alive || !_cc.enabled) return;

            float crouchTarget = intent.Crouch ? 1f : 0f;
            Crouch = Mathf.MoveTowards(Crouch, crouchTarget, dt * 6f);
            ApplyHeight();

            Vector3 wish = Vector3.zero;
            if (!Frozen)
            {
                Vector3 fwd = transform.forward, right = transform.right;
                wish = right * intent.Move.x + fwd * intent.Move.y;
                if (wish.sqrMagnitude > 1f) wish.Normalize();
            }

            float speed = intent.Crouch ? CrouchSpeed : (intent.Walk ? WalkSpeed : RunSpeed);
            speed *= SpeedMultiplier;

            bool grounded = _cc.isGrounded;
            float control = grounded ? 1f : 0.25f;
            Vector3 horiz = new Vector3(_velocity.x, 0f, _velocity.z);
            horiz = Vector3.Lerp(horiz, wish * speed, Mathf.Clamp01(dt * 14f * control));
            _velocity.x = horiz.x;
            _velocity.z = horiz.z;

            if (grounded && _velocity.y < 0f) _velocity.y = -2f;
            if (grounded && intent.Jump && !Frozen && Crouch < 0.5f) _velocity.y = JumpSpeed;
            _velocity.y += Gravity * dt;

            _cc.Move(_velocity * dt);
        }

        /// <summary>틱 끝에 부른다: 화면 보간용 위치 기록.</summary>
        public void CommitTick()
        {
            _prevTickPos = _currTickPos;
            _currTickPos = transform.position;
        }

        public MoveState GetMoveState() => new MoveState { Position = transform.position, Velocity = _velocity, Crouch = Crouch };

        /// <summary>예측 보정: 서버 상태로 되돌린다 (이후 남은 입력을 다시 돌린다).</summary>
        public void SetMoveState(in MoveState s)
        {
            Vector3 before = Visual != null ? Visual.position : transform.position;
            _cc.enabled = false;
            transform.position = s.Position;
            _cc.enabled = Alive && Drive == AvatarDrive.Simulated;
            _velocity = s.Velocity;
            Crouch = s.Crouch;
            ApplyHeight();
            _prevTickPos = _currTickPos = s.Position;
            _visualOffset = before - s.Position;
        }

        /// <summary>보간 모드: 스냅샷 보간 결과를 그대로 적용.</summary>
        public void SetInterpolatedPose(Vector3 pos, float yaw, float pitch, float crouch)
        {
            transform.position = pos;
            Yaw = yaw;
            Pitch = pitch;
            Crouch = crouch;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            _prevTickPos = _currTickPos = pos;
            ApplyHeight();
        }

        void ApplyHeight()
        {
            float h = Mathf.Lerp(StandHeight, CrouchHeight, Crouch);
            if (_cc.enabled)
            {
                _cc.height = h;
                _cc.center = new Vector3(0f, h * 0.5f, 0f);
            }
        }

        // ------------------------------------------------------------------
        // 화면 (프레임마다)
        // ------------------------------------------------------------------

        void LateUpdate()
        {
            if (Visual == null) return;
            float dt = Time.deltaTime;

            if (Drive == AvatarDrive.Simulated)
            {
                float alpha = Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime);
                _visualOffset = Vector3.Lerp(_visualOffset, Vector3.zero, Mathf.Clamp01(dt * 12f));
                if (_visualOffset.sqrMagnitude > 4f) _visualOffset = Vector3.zero;
                Visual.position = Vector3.Lerp(_prevTickPos, _currTickPos, alpha) + _visualOffset;
            }
            else
            {
                Visual.localPosition = Vector3.zero;
            }

            float yaw = LocalLook ? LookYaw : Yaw;
            float pitch = LocalLook ? LookPitch : Pitch;
            Visual.rotation = Quaternion.Euler(0f, yaw, 0f);
            if (Eye != null)
            {
                Eye.localPosition = new Vector3(0f, PlayerBody.EyeHeight(Crouch), 0f);
                Eye.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
            AnimateKnife(dt);
        }

        /// <summary>칼 휘두르는 연출 (판정과 무관, 보여주기용).</summary>
        public void PlaySwing(KnifeAttack type)
        {
            _knifeSwingT = 0f;
            _knifeSwingType = type;
            if (Model != null) Model.Swing(type);
        }

        void AnimateKnife(float dt)
        {
            if (Knife == null) return;
            var restPos = new Vector3(0.28f, -0.25f, 0.45f);
            var restRot = Quaternion.Euler(10f, -15f, -35f);
            if (_knifeSwingT < 0f)
            {
                Knife.localPosition = Vector3.Lerp(Knife.localPosition, restPos, dt * 12f);
                Knife.localRotation = Quaternion.Slerp(Knife.localRotation, restRot, dt * 12f);
                return;
            }

            // 찌르기 0.8초: 0.2초 뒤로 당김(예고) → 0.2초에 닿음 → 뻗은 채 잠깐 멈춤 → 복귀. 베기 0.3초 (주기 0.4초).
            float dur = _knifeSwingType == KnifeAttack.Stab ? 0.8f : 0.3f;
            _knifeSwingT += dt;
            float t = Mathf.Clamp01(_knifeSwingT / dur);
            if (_knifeSwingType == KnifeAttack.Stab)
            {
                float k;
                if (t < 0.25f) k = -Mathf.Sin(t / 0.25f * Mathf.PI * 0.5f) * 0.18f;
                else if (t < 0.33f) k = Mathf.Lerp(-0.18f, 0.5f, (t - 0.25f) / 0.08f);
                else if (t < 0.6f) k = 0.5f;
                else k = Mathf.Lerp(0.5f, 0f, Mathf.SmoothStep(0f, 1f, (t - 0.6f) / 0.4f));
                Knife.localPosition = restPos + new Vector3(-0.15f, 0.08f, k);
                Knife.localRotation = Quaternion.Euler(80f, 0f, 0f);
            }
            else
            {
                float a = Mathf.Lerp(-70f, 80f, Mathf.SmoothStep(0f, 1f, t));
                Knife.localPosition = restPos + new Vector3(-0.2f * t, 0.1f - 0.15f * t, 0.1f);
                Knife.localRotation = Quaternion.Euler(20f, a, -60f);
            }
            if (t >= 1f) _knifeSwingT = -1f;
        }
    }
}
