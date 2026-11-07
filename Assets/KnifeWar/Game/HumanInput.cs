using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 키보드·마우스 → PlayerIntent. 키는 GameSettings에서 바꿀 수 있다 (기본값은 아래).
    /// WASD 이동, Space 점프, Ctrl 앉기, Shift 걷기, 좌클 베기, 우클 찌르기,
    /// 준비 단계 1/2 = 직업 선택, 라운드 중(왕) 1/2/3 = 소환, Q = 버프. Esc 메뉴는 GameHud가 처리.
    /// </summary>
    public sealed class HumanInput
    {
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
            // 투표 라운드 준비 단계는 커서로 후보를 누르므로 클릭으로 잠그지 않는다
            if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked && phase != RoundPhase.Select)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                return Idle(); // 커서 잠그는 클릭은 공격으로 치지 않는다
            }

            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (locked)
            {
                float s = GameSettings.Sensitivity;
                _yaw += Input.GetAxisRaw("Mouse X") * s;
                _pitch -= Input.GetAxisRaw("Mouse Y") * s * (GameSettings.InvertY ? -1f : 1f);
                _pitch = Mathf.Clamp(_pitch, -88f, 88f);
            }

            var i = new PlayerIntent
            {
                Yaw = _yaw,
                Pitch = _pitch,
                Summon = -1,
                PickClass = -1,
                Vote = -1,
            };

            float x = (GameSettings.Held(Bind.Right) ? 1f : 0f) - (GameSettings.Held(Bind.Left) ? 1f : 0f);
            float y = (GameSettings.Held(Bind.Forward) ? 1f : 0f) - (GameSettings.Held(Bind.Back) ? 1f : 0f);
            i.Move = new Vector2(x, y);
            i.Jump = GameSettings.Held(Bind.Jump);
            i.Crouch = GameSettings.Held(Bind.Crouch);
            i.Walk = GameSettings.Held(Bind.Walk);

            if (phase == RoundPhase.Select)
            {
                // 투표 라운드: 1 = 희망 취소(기사), 2 = 왕 희망. 투표는 화면의 후보 카드를 누른다
                if (GameSettings.Down(Bind.Slot1)) i.PickClass = 0;
                if (GameSettings.Down(Bind.Slot2)) i.PickClass = 1;
            }
            else if (phase == RoundPhase.Live)
            {
                // 왕: 1~4 = 손패 카드 사용 (화면의 카드를 눌러도 됨)
                if (GameSettings.Down(Bind.Slot1)) i.Summon = 0;
                if (GameSettings.Down(Bind.Slot2)) i.Summon = 1;
                if (GameSettings.Down(Bind.Slot3)) i.Summon = 2;
                if (GameSettings.Down(Bind.Slot4)) i.Summon = 3;
                if (locked)
                {
                    if (GameSettings.Down(Bind.Slash)) i.Attack = KnifeAttack.Slash;
                    else if (GameSettings.Down(Bind.Stab)) i.Attack = KnifeAttack.Stab;
                }
            }
            return i;
        }

        PlayerIntent Idle() => new PlayerIntent { Yaw = _yaw, Pitch = _pitch, Summon = -1, PickClass = -1, Vote = -1 };
    }
}
