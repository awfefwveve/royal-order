using System.Collections.Generic;
using KnifeWar.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace KnifeWar.Game
{
    /// <summary>
    /// 캐릭터 외형 (SkinSet의 모델). 보여주기 전용 — 이동·판정은 PlayerAvatar가 하고 이건 따라 그리기만 한다.
    /// - 직업별 모델 교체 (기사/왕), 죽으면 숨김
    /// - Animator 파라미터 자동 구동 (속도·앉기·착지·사망·공격·소환)
    /// - 3인칭 칼을 오른손 뼈에 붙임
    /// - 내 캐릭터는 그림자만 (1인칭)
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class AvatarModel : MonoBehaviour
    {
        PlayerAvatar _a;
        SkinSet _skin;
        readonly GameObject[] _models = new GameObject[2];
        readonly Animator[] _anims = new Animator[2];
        readonly HashSet<int>[] _params = { new HashSet<int>(), new HashSet<int>() };
        int _hSpeed, _hCrouch, _hGrounded, _hDead, _hSlash, _hStab, _hSummon;
        Vector3 _lastPos;
        float _speed, _vy;

        public bool HasModel => _models[0] != null;

        public static AvatarModel Attach(PlayerAvatar a, SkinSet skin)
        {
            if (skin == null) return null;
            var knight = skin.CharacterPrefab(a.Team, PlayerClass.Knight);
            if (knight == null) return null;
            var m = a.gameObject.AddComponent<AvatarModel>();
            m.Init(a, skin);
            return m;
        }

        void Init(PlayerAvatar a, SkinSet skin)
        {
            _a = a;
            _skin = skin;
            _hSpeed = Animator.StringToHash(skin.SpeedParam);
            _hCrouch = Animator.StringToHash(skin.CrouchParam);
            _hGrounded = Animator.StringToHash(skin.GroundedParam);
            _hDead = Animator.StringToHash(skin.DeadParam);
            _hSlash = Animator.StringToHash(skin.SlashTrigger);
            _hStab = Animator.StringToHash(skin.StabTrigger);
            _hSummon = Animator.StringToHash(skin.SummonTrigger);

            var knight = skin.CharacterPrefab(a.Team, PlayerClass.Knight);
            var wizard = skin.CharacterPrefab(a.Team, PlayerClass.King);
            _models[0] = Spawn(knight, 0);
            _models[1] = wizard != knight ? Spawn(wizard, 1) : null;

            // 기본 캡슐 몸은 숨김 (모자는 왕 모델이 없을 때만 계속 사용)
            if (a.Body != null) a.Body.gameObject.SetActive(false);
            if (skin.HasWizardModel(a.Team) && a.WizardHat != null) { Destroy(a.WizardHat); a.WizardHat = null; }
            _lastPos = a.Visual.position;
            Refresh();
        }

        GameObject Spawn(GameObject prefab, int cls)
        {
            var go = Instantiate(prefab, _a.Visual, false);
            go.name = cls == 0 ? "Model_Knight" : "Model_Wizard";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            foreach (var col in go.GetComponentsInChildren<Collider>(true)) Destroy(col);   // 판정에 끼어들지 않게
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Destroy(rb);

            if (_skin.FitModelHeight)
            {
                var rs = go.GetComponentsInChildren<Renderer>(true);
                if (rs.Length > 0)
                {
                    var b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    float h = b.size.y;
                    if (h > 0.05f) go.transform.localScale *= 1.8f / h;
                }
            }

            var anim = go.GetComponentInChildren<Animator>();
            if (anim != null)
            {
                anim.applyRootMotion = false;   // 위치는 서버 판정이 정한다
                anim.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                foreach (var p in anim.parameters) _params[cls].Add(p.nameHash);
                _anims[cls] = anim;

                if (_skin.KnifeInHand != null && anim.isHuman)
                {
                    var hand = anim.GetBoneTransform(HumanBodyBones.RightHand);
                    if (hand != null && cls == 0) Instantiate(_skin.KnifeInHand, hand, false).name = "KnifeInHand";
                }
            }
            return go;
        }

        /// <summary>직업·생존 상태가 바뀌면 (PlayerAvatar가 부름).</summary>
        public void Refresh()
        {
            if (_a == null) return;
            int cls = _a.Class == PlayerClass.King && _models[1] != null ? 1 : 0;
            for (int i = 0; i < 2; i++)
                if (_models[i] != null) _models[i].SetActive(_a.Alive && i == cls);
            SetBool(cls, _hDead, !_a.Alive);
        }

        /// <summary>내 캐릭터: 몸은 안 보이고 그림자만.</summary>
        public void SetShadowsOnly()
        {
            for (int i = 0; i < 2; i++)
                if (_models[i] != null)
                    foreach (var r in _models[i].GetComponentsInChildren<Renderer>(true))
                        r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }

        public void Swing(KnifeAttack type) => Trigger(Current, type == KnifeAttack.Stab ? _hStab : _hSlash);
        public void Summon() => Trigger(Current, _hSummon);

        int Current => _a.Class == PlayerClass.King && _models[1] != null ? 1 : 0;

        void LateUpdate()
        {
            if (_a == null || _a.Visual == null) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector3 p = _a.Visual.position;
            Vector3 d = p - _lastPos;
            _lastPos = p;
            if (d.sqrMagnitude > 25f) d = Vector3.zero;   // 순간이동은 무시
            float flat = new Vector2(d.x, d.z).magnitude / dt;
            _speed = Mathf.Lerp(_speed, flat, Mathf.Clamp01(dt * 12f));
            _vy = Mathf.Lerp(_vy, d.y / dt, Mathf.Clamp01(dt * 12f));

            int c = Current;
            var anim = _anims[c];
            if (anim == null || !anim.isActiveAndEnabled) return;
            if (_params[c].Contains(_hSpeed)) anim.SetFloat(_hSpeed, _speed);
            if (_params[c].Contains(_hCrouch)) anim.SetFloat(_hCrouch, _a.Crouch);
            if (_params[c].Contains(_hGrounded)) anim.SetBool(_hGrounded, Mathf.Abs(_vy) < 1.5f);
        }

        void SetBool(int cls, int hash, bool v)
        {
            var anim = _anims[cls];
            if (anim != null && anim.isActiveAndEnabled && _params[cls].Contains(hash)) anim.SetBool(hash, v);
        }

        void Trigger(int cls, int hash)
        {
            var anim = _anims[cls];
            if (anim != null && anim.isActiveAndEnabled && _params[cls].Contains(hash)) anim.SetTrigger(hash);
        }
    }
}
