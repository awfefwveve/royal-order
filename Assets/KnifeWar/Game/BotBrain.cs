using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 오프라인 테스트용 봇. 기사는 가까운 적에게 달려가서 베고, 왕는 뒤에서 소환한다.
    /// 일부러 완벽하지 않게: 반응 지연, 조준 흔들림, 가끔 헛스윙.
    /// </summary>
    public sealed class BotBrain
    {
        readonly PlayerAvatar _me;
        readonly System.Random _rng;
        float _reactUntil;
        float _strafe;
        float _strafeTimer;
        float _nextSummon;
        float _nextBuff;
        int _target = -1;

        public PlayerClass PreferredClass;

        public BotBrain(PlayerAvatar me, int seed)
        {
            _me = me;
            _rng = new System.Random(seed);
        }

        float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

        public void OnRoundStart(float now)
        {
            _target = -1;
            _reactUntil = now + Rand(0.2f, 0.8f);
            _nextSummon = now + Rand(0.5f, 2f);
            _nextBuff = now + Rand(6f, 15f);
        }

        public PlayerIntent Think(MatchController m, float now, float dt)
        {
            var i = new PlayerIntent { Yaw = _me.Yaw, Pitch = _me.Pitch, Summon = -1, PickClass = -1 };
            var phase = m.Round.Phase;

            if (phase == RoundPhase.Select)
            {
                i.PickClass = (sbyte)PreferredClass;
                return i;
            }
            if (phase != RoundPhase.Live || !_me.Alive) return i;

            float dir = _me.Team == 0 ? 1f : -1f;
            Vector3 pos = _me.transform.position;

            if (_me.Class == PlayerClass.King) return ThinkWizard(m, i, now, pos, dir);

            // ---- 기사 ----
            if (_target < 0 || !m.Avatars[_target].Alive || now > _reactUntil + 3f)
            {
                _target = m.NearestEnemy(_me.Index, out _);
                _reactUntil = now + Rand(0.15f, 0.45f);
            }

            Vector3 goal = new Vector3(pos.x + dir * 10f, pos.y, 0f);
            float dist = float.MaxValue;
            if (_target >= 0)
            {
                Vector3 tp = m.Avatars[_target].transform.position;
                goal = tp;
                dist = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(tp.x, 0f, tp.z));
            }

            // 근처에 적 몬스터가 막고 있으면 먼저 정리
            int monsterSlot = m.NearestEnemyMonster(_me.Index, out Vector3 monsterPos, out float monsterDist);
            bool fightMonster = monsterSlot >= 0 && monsterDist < 2.2f && dist > 3f;
            if (fightMonster) { goal = monsterPos; dist = monsterDist; }

            // 다리 밖으로 안 떨어지게 목표를 다리 안으로
            goal.z = Mathf.Clamp(goal.z, -(MatchController.BridgeHalfWidth - 0.7f), MatchController.BridgeHalfWidth - 0.7f);

            Vector3 to = goal - pos;
            to.y = 0f;
            float wantYaw = Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg;
            float aimNoise = Mathf.Sin(now * 3.1f + _me.Index) * 4f;
            i.Yaw = Mathf.MoveTowardsAngle(_me.Yaw, wantYaw + aimNoise, 540f * dt);

            // 상대 가슴 높이를 조준
            float eyeY = pos.y + PlayerBody.EyeHeight(_me.Crouch);
            float targetY = fightMonster ? pos.y + 0.6f : goal.y + 1.2f;
            float flat = Mathf.Max(0.1f, new Vector2(to.x, to.z).magnitude);
            i.Pitch = Mathf.MoveTowards(_me.Pitch, -Mathf.Atan2(targetY - eyeY, flat) * Mathf.Rad2Deg, 360f * dt);

            // 이동은 월드 기준으로 정하고 (바라보는 방향과 분리), 다리 중앙 쪽으로 계속 보정한다
            _strafeTimer -= dt;
            if (_strafeTimer <= 0f)
            {
                _strafe = Rand(-1f, 1f);
                _strafeTimer = Rand(0.3f, 0.9f);
            }
            Vector3 toFlat = new Vector3(to.x, 0f, to.z);
            Vector3 move = toFlat.sqrMagnitude > 0.0001f ? toFlat.normalized : Vector3.zero;
            if (dist < 0.8f) move = -move * 0.6f;          // 너무 붙으면 살짝 뒤로
            else if (dist < 1.3f) move *= 0.2f;
            if (dist < 4f) move += new Vector3(0f, 0f, _strafe * 0.6f);   // 근접전 좌우 무빙
            i.Move = WorldToMove(SafeOnBridge(pos, move), i.Yaw);
            float yawErr = Mathf.Abs(Mathf.DeltaAngle(_me.Yaw, wantYaw));
            if (now > _reactUntil && dist < 1.55f && yawErr < 25f)
            {
                i.Attack = Rand(0f, 1f) < 0.25f ? KnifeAttack.Stab : KnifeAttack.Slash;
                _reactUntil = now + Rand(0.05f, 0.35f);
            }
            return i;
        }

        /// <summary>다리 가장자리 근처에서 바깥으로 가는 이동을 막고 안쪽으로 당긴다. 스폰 지역(|x| &gt; 60)은 넓어서 제외.</summary>
        static Vector3 SafeOnBridge(Vector3 pos, Vector3 move)
        {
            if (Mathf.Abs(pos.x) > 59.5f) return move;
            float edge = MatchController.BridgeHalfWidth;
            float z = pos.z;
            if (z > edge - 1.1f && move.z > 0f) move.z = 0f;
            if (z < -(edge - 1.1f) && move.z < 0f) move.z = 0f;
            if (Mathf.Abs(z) > edge - 1.4f) move.z -= Mathf.Sign(z) * Mathf.InverseLerp(edge - 1.4f, edge - 0.6f, Mathf.Abs(z)) * 1.5f;
            return Vector3.ClampMagnitude(move, 1f);
        }

        static Vector2 WorldToMove(Vector3 worldMove, float yawDeg)
        {
            float r = yawDeg * Mathf.Deg2Rad;
            Vector3 fwd = new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r));
            Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
            return new Vector2(Vector3.Dot(worldMove, right), Vector3.Dot(worldMove, fwd));
        }

        PlayerIntent ThinkWizard(MatchController m, PlayerIntent i, float now, Vector3 pos, float dir)
        {
            // 자기 진영 쪽 다리 입구 근처에서 대기
            float homeX = _me.Team == 0 ? -52f : 52f;
            Vector3 goal = new Vector3(homeX, pos.y, (_me.Index % 3 - 1) * 1.0f);
            Vector3 to = goal - pos;
            to.y = 0f;
            i.Yaw = Mathf.MoveTowardsAngle(_me.Yaw, dir > 0 ? 90f : -90f, 360f * Time.deltaTime);
            if (to.magnitude > 0.6f) i.Move = WorldToMove(SafeOnBridge(pos, to.normalized), i.Yaw);

            if (now >= _nextSummon)
            {
                float mana = m.Wizard.Mana(_me.Index);
                sbyte pick = (sbyte)(mana >= 30f && Rand(0f, 1f) < 0.35f ? 1 : (Rand(0f, 1f) < 0.4f ? 2 : 0));
                if (mana >= WizardRules.CostOf((byte)pick)) i.Summon = pick;
                _nextSummon = now + Rand(0.4f, 1.6f);
            }
            if (now >= _nextBuff && m.Wizard.Mana(_me.Index) >= MatchController.BuffCost)
            {
                i.Buff = true;
                _nextBuff = now + Rand(10f, 20f);
            }
            return i;
        }
    }
}
