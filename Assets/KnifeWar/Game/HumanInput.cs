using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 키보드·마우스 → PlayerIntent.
    /// WASD 이동, Space 점프, Ctrl 앉기, Shift 걷기, 좌클 베기, 우클 찌르기,
    /// 준비 단계 1/2 = 직업 선택, 라운드 중(마법사) 1/2/3 = 소환, Q = 버프. Esc 메뉴는 GameHud가 처리.
    /// </summary>
    public sealed class HumanInput
    {
        public float Sensitivity = 2.0f;
        public bool Blocked;   // Esc 메뉴가 열려 있으면 입력 무시
        float _yaw, _pitch;

        public void Reset(float yaw)
        {
            _yaw = yaw;
            _pitch = 0f;
        }

        public PlayerIntent Sample(RoundPhase phase)
        {
            if (Blocked) return Idle();
            if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                return Idle(); // 커서 잠그는 클릭은 공격으로 치지 않는다
            }

            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (locked)
            {
                _yaw += Input.GetAxisRaw("Mouse X") * Sensitivity;
                _pitch -= Input.GetAxisRaw("Mouse Y") * Sensitivity;
                _pitch = Mathf.Clamp(_pitch, -88f, 88f);
            }

            var i = new PlayerIntent
            {
                Yaw = _yaw,
                Pitch = _pitch,
                Summon = -1,
                PickClass = -1,
            };

            float x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            float y = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            i.Move = new Vector2(x, y);
            i.Jump = Input.GetKey(KeyCode.Space);
            i.Crouch = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C);
            i.Walk = Input.GetKey(KeyCode.LeftShift);

            if (phase == RoundPhase.Select)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) i.PickClass = 0;
                if (Input.GetKeyDown(KeyCode.Alpha2)) i.PickClass = 1;
            }
            else if (phase == RoundPhase.Live)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1)) i.Summon = 0;
                if (Input.GetKeyDown(KeyCode.Alpha2)) i.Summon = 1;
                if (Input.GetKeyDown(KeyCode.Alpha3)) i.Summon = 2;
                if (Input.GetKeyDown(KeyCode.Q)) i.Buff = true;
                if (locked)
                {
                    if (Input.GetMouseButtonDown(0)) i.Attack = KnifeAttack.Slash;
                    else if (Input.GetMouseButtonDown(1)) i.Attack = KnifeAttack.Stab;
                }
            }
            return i;
        }

        PlayerIntent Idle() => new PlayerIntent { Yaw = _yaw, Pitch = _pitch, Summon = -1, PickClass = -1 };
    }
}
