using KnifeWar.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace KnifeWar.Game
{
    /// <summary>
    /// 외부 에셋 없이 코드로 만든 임시 외형. 진짜 모델(SkinSet)이 들어오면 그쪽이 우선이고 이건 쓰이지 않는다.
    /// - 기사: 갑옷(강철) + 팀 색 서코트·망토 + 투구, 오른손에 칼
    /// - 왕: 왕관 + 긴 로브 + 긴 망토, 칼 없음
    /// 판정 히트박스(PlayerBody)와 같은 치수(키 1.8m, 머리 1.68m, 가슴 1.28m …)로 맞춰 "보이는 곳 = 맞는 곳".
    /// </summary>
    public static class ProceduralMaterials
    {
        static Material _steel, _darkSteel, _gold, _leather, _skin, _blade, _clothBlue, _clothRed;

        public static Material Steel => _steel ??= Make("P_Steel", new Color(0.62f, 0.64f, 0.68f), 0.9f, 0.62f);
        public static Material DarkSteel => _darkSteel ??= Make("P_DarkSteel", new Color(0.22f, 0.23f, 0.26f), 0.85f, 0.5f);
        public static Material Gold => _gold ??= Make("P_Gold", new Color(0.78f, 0.6f, 0.26f), 0.95f, 0.7f);
        public static Material Leather => _leather ??= Make("P_Leather", new Color(0.24f, 0.16f, 0.1f), 0f, 0.35f);
        public static Material Skin => _skin ??= Make("P_Skin", new Color(0.78f, 0.6f, 0.48f), 0f, 0.3f);
        public static Material Blade => _blade ??= Make("P_Blade", new Color(0.82f, 0.84f, 0.88f), 1f, 0.8f);
        public static Material Cloth(int team) => team == 0
            ? (_clothBlue ??= Make("P_ClothBlue", new Color(0.18f, 0.29f, 0.42f), 0f, 0.15f))
            : (_clothRed ??= Make("P_ClothRed", new Color(0.42f, 0.12f, 0.16f), 0f, 0.15f));

        static Material Make(string name, Color c, float metallic, float smooth)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            var m = new Material(shader) { name = name, color = c };
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            return m;
        }
    }

    /// <summary>작은 조각(상자·캡슐·구·쐐기)을 만들어 붙이는 도우미. 충돌체는 만들지 않는다.</summary>
    public static class ProceduralParts
    {
        static Mesh _wedge;

        public static Transform Pivot(string name, Transform parent, Vector3 localPos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPos;
            return t;
        }

        public static GameObject Box(string name, Transform parent, Vector3 center, Vector3 size, Material mat)
            => Prim(PrimitiveType.Cube, name, parent, center, size, Quaternion.identity, mat);

        public static GameObject Sphere(string name, Transform parent, Vector3 center, Vector3 size, Material mat)
            => Prim(PrimitiveType.Sphere, name, parent, center, size, Quaternion.identity, mat);

        /// <summary>세로 캡슐: 위 끝이 pivot(0,0,0), 아래로 length 만큼.</summary>
        public static GameObject Limb(string name, Transform parent, float length, float radius, Material mat)
        {
            // 유니티 캡슐은 높이 2(끝 반구 포함). 길이 = 세로 스케일*2 → 세로 스케일 = length/2
            var go = Prim(PrimitiveType.Capsule, name, parent, new Vector3(0f, -length * 0.5f, 0f),
                new Vector3(radius * 2f, length * 0.5f, radius * 2f), Quaternion.identity, mat);
            return go;
        }

        public static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 center, Vector3 size, Quaternion rot, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            var col = go.GetComponent<Collider>();
            if (col != null) Object.Destroy(col);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            go.transform.localRotation = rot;
            go.transform.localScale = size;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
            return go;
        }

        /// <summary>칼날 메시: 앞(+z)으로 갈수록 좁아지는 양날 (가로 w, 두께 t, 길이 len, 끝 뾰족).</summary>
        public static Mesh Wedge()
        {
            if (_wedge != null) return _wedge;
            var m = new Mesh { name = "P_Blade" };
            // 단면: 마름모(양날). 뒤 z=0 단면 → 앞 z=0.78 작은 단면 → z=1 꼭짓점
            Vector3[] back = { new(-0.5f, 0f, 0f), new(0f, 0.5f, 0f), new(0.5f, 0f, 0f), new(0f, -0.5f, 0f) };
            Vector3[] mid = { new(-0.4f, 0f, 0.78f), new(0f, 0.38f, 0.78f), new(0.4f, 0f, 0.78f), new(0f, -0.38f, 0.78f) };
            Vector3 tip = new(0f, 0f, 1f);
            var verts = new System.Collections.Generic.List<Vector3>();
            var tris = new System.Collections.Generic.List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = verts.Count; verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                tris.AddRange(new[] { i, i + 1, i + 2, i, i + 2, i + 3 });
            }
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                int i = verts.Count; verts.Add(a); verts.Add(b); verts.Add(c);
                tris.AddRange(new[] { i, i + 1, i + 2 });
            }
            for (int k = 0; k < 4; k++)
            {
                int n = (k + 1) % 4;
                Quad(back[k], mid[k], mid[n], back[n]);
                Tri(mid[k], tip, mid[n]);
            }
            Quad(back[0], back[3], back[2], back[1]);   // 뒤 막기
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            _wedge = m;
            return m;
        }

        /// <summary>칼: 손잡이 끝이 원점, 칼날이 +z. 전체 길이 약 0.36m.</summary>
        public static Transform Knife(string name, Transform parent)
        {
            var root = Pivot(name, parent, Vector3.zero);
            // 손잡이(가죽) 0.11, 가드, 칼날 0.23
            Prim(PrimitiveType.Cylinder, "Grip", root, new Vector3(0f, 0f, 0.055f), new Vector3(0.026f, 0.055f, 0.026f), Quaternion.Euler(90f, 0f, 0f), ProceduralMaterials.Leather);
            Sphere("Pommel", root, new Vector3(0f, 0f, -0.005f), new Vector3(0.034f, 0.034f, 0.034f), ProceduralMaterials.Gold);
            Box("Guard", root, new Vector3(0f, 0f, 0.115f), new Vector3(0.085f, 0.018f, 0.014f), ProceduralMaterials.Gold);
            var blade = new GameObject("Blade");
            blade.transform.SetParent(root, false);
            blade.transform.localPosition = new Vector3(0f, 0f, 0.122f);
            blade.transform.localScale = new Vector3(0.03f, 0.009f, 0.24f);
            blade.AddComponent<MeshFilter>().sharedMesh = Wedge();
            var mr = blade.AddComponent<MeshRenderer>();
            mr.sharedMaterial = ProceduralMaterials.Blade;
            mr.shadowCastingMode = ShadowCastingMode.On;
            return root;
        }
    }

    /// <summary>
    /// 코드로 만든 기사/왕 몸과 절차 애니메이션 (걷기·달리기·앉기·베기·찌르기·소환·사망).
    /// 뼈대 역할의 빈 Transform 들을 돌려서 움직인다. 위치·판정은 PlayerAvatar가 정하고 이건 따라 그리기만 한다.
    /// </summary>
    public sealed class ProceduralRig
    {
        public readonly Transform Root;
        public readonly bool IsKing;
        readonly Transform _hips, _torso, _neck, _head;
        readonly Transform _shoulderL, _shoulderR, _elbowL, _elbowR;
        readonly Transform _hipL, _hipR, _kneeL, _kneeR;
        readonly Transform[] _cape;
        readonly Transform _knife;
        readonly Renderer[] _renderers;

        // 애니메이션 상태
        float _gait;            // 보행 위상 (라디안)
        float _attackT = -1f;   // 공격 진행 시간
        KnifeAttack _attackType;
        float _summonT = -1f;
        float _deathT = -1f;
        float _capeSwing;
        float _idleT;

        // 치수 (PlayerBody 와 맞춤)
        const float HipY = 0.92f;
        const float TorsoH = 0.52f;     // 엉덩이 위 0.92 → 어깨 1.44
        const float ShoulderY = 0.52f;  // hips 기준
        const float ShoulderX = 0.27f;
        const float UpperArm = 0.30f, Forearm = 0.28f;
        const float Thigh = 0.44f, Shin = 0.40f;

        public ProceduralRig(Transform parent, int team, bool king)
        {
            IsKing = king;
            var cloth = ProceduralMaterials.Cloth(team);
            var steel = ProceduralMaterials.Steel;
            var dark = ProceduralMaterials.DarkSteel;

            Root = ProceduralParts.Pivot(king ? "Proc_King" : "Proc_Knight", parent, Vector3.zero);
            _hips = ProceduralParts.Pivot("Hips", Root, new Vector3(0f, HipY, 0f));

            // ---- 몸통 ----
            _torso = ProceduralParts.Pivot("Torso", _hips, Vector3.zero);
            if (king)
            {
                ProceduralParts.Box("Robe", _torso, new Vector3(0f, -0.2f, 0f), new Vector3(0.5f, 1.0f, 0.34f), cloth);          // 발목까지 로브
                ProceduralParts.Box("Chest", _torso, new Vector3(0f, 0.3f, 0.02f), new Vector3(0.46f, 0.46f, 0.3f), ProceduralMaterials.Gold);
                ProceduralParts.Box("Collar", _torso, new Vector3(0f, 0.5f, -0.02f), new Vector3(0.56f, 0.1f, 0.36f), new Material(ProceduralMaterials.Skin) { color = new Color(0.92f, 0.9f, 0.85f) });
            }
            else
            {
                ProceduralParts.Box("Chest", _torso, new Vector3(0f, 0.3f, 0.02f), new Vector3(0.42f, 0.44f, 0.28f), steel);
                ProceduralParts.Box("Belly", _torso, new Vector3(0f, 0.06f, 0f), new Vector3(0.36f, 0.22f, 0.24f), dark);
                ProceduralParts.Box("Tabard", _torso, new Vector3(0f, 0.12f, 0.15f), new Vector3(0.24f, 0.62f, 0.02f), cloth);   // 앞 서코트
                ProceduralParts.Box("Belt", _torso, new Vector3(0f, -0.04f, 0f), new Vector3(0.38f, 0.06f, 0.27f), ProceduralMaterials.Leather);
                ProceduralParts.Box("Skirt", _torso, new Vector3(0f, -0.16f, 0f), new Vector3(0.4f, 0.2f, 0.28f), dark);        // 사슬 치마
            }

            // ---- 머리 ----
            _neck = ProceduralParts.Pivot("Neck", _torso, new Vector3(0f, TorsoH + 0.02f, 0.01f));
            _head = ProceduralParts.Pivot("Head", _neck, new Vector3(0f, 0.04f, 0f));
            ProceduralParts.Sphere("Face", _head, new Vector3(0f, 0.11f, 0.01f), new Vector3(0.21f, 0.24f, 0.22f), ProceduralMaterials.Skin);
            if (king)
            {
                ProceduralParts.Prim(PrimitiveType.Cylinder, "Crown", _head, new Vector3(0f, 0.24f, 0f), new Vector3(0.2f, 0.035f, 0.2f), Quaternion.identity, ProceduralMaterials.Gold);
                for (int i = 0; i < 5; i++)
                {
                    float a = i * Mathf.PI * 2f / 5f;
                    ProceduralParts.Box("Spike", _head, new Vector3(Mathf.Sin(a) * 0.085f, 0.3f, Mathf.Cos(a) * 0.085f), new Vector3(0.025f, 0.06f, 0.025f), ProceduralMaterials.Gold);
                }
                ProceduralParts.Box("Beard", _head, new Vector3(0f, 0.0f, 0.08f), new Vector3(0.12f, 0.1f, 0.06f), new Material(ProceduralMaterials.Leather) { color = new Color(0.55f, 0.52f, 0.5f) });
            }
            else
            {
                ProceduralParts.Sphere("Helm", _head, new Vector3(0f, 0.15f, 0f), new Vector3(0.25f, 0.24f, 0.26f), steel);          // 투구 돔
                ProceduralParts.Box("Nasal", _head, new Vector3(0f, 0.1f, 0.12f), new Vector3(0.025f, 0.12f, 0.02f), steel);          // 코 가리개
                ProceduralParts.Box("HelmRim", _head, new Vector3(0f, 0.07f, 0f), new Vector3(0.26f, 0.03f, 0.27f), dark);
            }

            // ---- 팔 ----
            _shoulderL = ProceduralParts.Pivot("ShoulderL", _torso, new Vector3(-ShoulderX, ShoulderY, 0f));
            _shoulderR = ProceduralParts.Pivot("ShoulderR", _torso, new Vector3(ShoulderX, ShoulderY, 0f));
            _elbowL = BuildArm(_shoulderL, king, cloth, steel, dark, false);
            _elbowR = BuildArm(_shoulderR, king, cloth, steel, dark, true);
            if (!king)
            {
                var hand = _elbowR.Find("Hand");
                _knife = ProceduralParts.Knife("KnifeInHand", hand);
                _knife.localPosition = new Vector3(0.01f, -0.01f, 0.03f);
                _knife.localRotation = Quaternion.Euler(90f, 0f, 0f);    // 칼날이 팔이 뻗은 방향(손의 -y)으로
            }

            // ---- 다리 ----
            _hipL = ProceduralParts.Pivot("HipL", _hips, new Vector3(-0.11f, -0.02f, 0f));
            _hipR = ProceduralParts.Pivot("HipR", _hips, new Vector3(0.11f, -0.02f, 0f));
            _kneeL = BuildLeg(_hipL, king, cloth, steel, dark);
            _kneeR = BuildLeg(_hipR, king, cloth, steel, dark);

            // ---- 망토 (4마디) ----
            int segs = king ? 5 : 4;
            _cape = new Transform[segs];
            Transform prev = ProceduralParts.Pivot("CapeRoot", _torso, new Vector3(0f, ShoulderY - 0.02f, -0.15f));
            float segLen = king ? 0.26f : 0.22f;
            for (int i = 0; i < segs; i++)
            {
                var seg = ProceduralParts.Pivot("Cape" + i, prev, i == 0 ? Vector3.zero : new Vector3(0f, -segLen, 0f));
                ProceduralParts.Box("Cloth", seg, new Vector3(0f, -segLen * 0.5f, 0f), new Vector3(0.44f - i * 0.02f, segLen, 0.02f), cloth);
                _cape[i] = seg;
                prev = seg;
            }

            _renderers = Root.GetComponentsInChildren<Renderer>(true);
        }

        Transform BuildArm(Transform shoulder, bool king, Material cloth, Material steel, Material dark, bool right)
        {
            if (!king) ProceduralParts.Sphere("Pauldron", shoulder, new Vector3(right ? 0.04f : -0.04f, 0.03f, 0f), new Vector3(0.2f, 0.16f, 0.2f), steel);
            ProceduralParts.Limb("UpperArm", shoulder, UpperArm, 0.065f, king ? cloth : dark);
            var elbow = ProceduralParts.Pivot("Elbow", shoulder, new Vector3(0f, -UpperArm, 0f));
            ProceduralParts.Limb("Forearm", elbow, Forearm, 0.06f, king ? cloth : steel);
            var hand = ProceduralParts.Pivot("Hand", elbow, new Vector3(0f, -Forearm - 0.02f, 0f));
            ProceduralParts.Box("Fist", hand, Vector3.zero, new Vector3(0.09f, 0.1f, 0.08f), king ? ProceduralMaterials.Skin : dark);
            return elbow;
        }

        Transform BuildLeg(Transform hip, bool king, Material cloth, Material steel, Material dark)
        {
            ProceduralParts.Limb("Thigh", hip, Thigh, 0.085f, king ? cloth : dark);
            var knee = ProceduralParts.Pivot("Knee", hip, new Vector3(0f, -Thigh, 0f));
            ProceduralParts.Limb("Shin", knee, Shin, 0.065f, king ? cloth : steel);
            ProceduralParts.Box("Foot", knee, new Vector3(0f, -Shin - 0.03f, 0.05f), new Vector3(0.11f, 0.07f, 0.24f), dark);
            return knee;
        }

        public void SetActive(bool on) => Root.gameObject.SetActive(on);
        public bool Active => Root.gameObject.activeSelf;

        public void SetShadowsOnly()
        {
            foreach (var r in _renderers) r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }

        public void PlayAttack(KnifeAttack type) { _attackT = 0f; _attackType = type; }
        public void PlaySummon() { _summonT = 0f; }

        /// <summary>매 프레임: 속도(m/s)·앉기(0~1)·생존 으로 자세를 만든다.</summary>
        public void Animate(float dt, float speed, float crouch, bool alive, bool grounded)
        {
            if (!alive)
            {
                if (_deathT < 0f) _deathT = 0f;
                _deathT += dt;
                float f = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_deathT / 0.55f));
                Root.localRotation = Quaternion.Euler(f * 88f, 0f, f * 6f);
                Root.localPosition = new Vector3(0f, -f * 0.05f, f * 0.25f);
                ResetPose();
                _hips.localRotation = Quaternion.Euler(0f, 0f, 0f);
                _shoulderL.localRotation = Quaternion.Euler(-30f * f, 0f, 25f * f);
                _shoulderR.localRotation = Quaternion.Euler(-40f * f, 0f, -30f * f);
                _attackT = -1f; _summonT = -1f;
                return;
            }
            if (_deathT >= 0f) { _deathT = -1f; Root.localRotation = Quaternion.identity; Root.localPosition = Vector3.zero; }

            _idleT += dt;
            float runNorm = Mathf.Clamp01(speed / PlayerAvatar.RunSpeed);
            // 보행 위상: 한 사이클(두 걸음) 에 걷기 1.6m ~ 달리기 2.9m. 군인 걸음처럼 또박또박 (걷기 ≈1.3Hz, 달리기 ≈2.1Hz).
            float stride = Mathf.Lerp(1.6f, 2.9f, runNorm);
            _gait += speed * dt * (Mathf.PI * 2f) / stride;
            float g = Mathf.Sin(_gait);          // +: 왼다리 앞
            float gc = Mathf.Cos(_gait);         // 다리가 앞으로 지나갈 때 무릎 접힘
            float moveNorm = Mathf.Clamp01(speed / 2.0f);
            float legAmp = Mathf.Lerp(0f, Mathf.Lerp(30f, 46f, runNorm), moveNorm);
            float bob = Mathf.Abs(Mathf.Sin(_gait * 2f)) * (0.02f + 0.03f * runNorm) * moveNorm;

            // 앉기
            float cr = crouch;
            _hips.localPosition = new Vector3(0f, HipY - 0.42f * cr + bob, 0.05f * cr);
            _torso.localRotation = Quaternion.Euler(14f * cr + 6f * runNorm + Mathf.Sin(_idleT * 1.2f) * 1.2f, 0f, g * 3f * moveNorm);

            // 다리 (앉으면 무릎 깊이 접힘): 디딘 다리는 곧게, 넘어오는 다리는 무릎을 들어서 (발발거림 없이 한 걸음씩)
            float thighL = g * legAmp + 48f * cr, thighR = -g * legAmp + 48f * cr;
            float kneeL = Mathf.Max(0f, gc) * legAmp * 1.5f + 75f * cr, kneeR = Mathf.Max(0f, -gc) * legAmp * 1.5f + 75f * cr;
            _hipL.localRotation = Quaternion.Euler(-thighL, 0f, 0f);
            _hipR.localRotation = Quaternion.Euler(-thighR, 0f, 0f);
            _kneeL.localRotation = Quaternion.Euler(kneeL, 0f, 0f);
            _kneeR.localRotation = Quaternion.Euler(kneeR, 0f, 0f);
            if (!grounded)
            {
                _hipL.localRotation = Quaternion.Euler(-20f, 0f, 0f); _hipR.localRotation = Quaternion.Euler(10f, 0f, 0f);
                _kneeL.localRotation = Quaternion.Euler(35f, 0f, 0f); _kneeR.localRotation = Quaternion.Euler(20f, 0f, 0f);
            }

            // 팔: 걸을 때 반대 흔들림, 기사는 오른손 칼을 앞에 든 자세
            float armAmp = legAmp * 0.6f;
            float idleSway = Mathf.Sin(_idleT * 1.5f) * 2f;
            Quaternion armL = Quaternion.Euler(-g * armAmp + 8f + idleSway, 0f, 10f);
            Quaternion armR = IsKing
                ? Quaternion.Euler(g * armAmp + 8f + idleSway, 0f, -10f)
                : Quaternion.Euler(g * armAmp * 0.5f - 35f, -20f, -14f);   // 칼 든 팔은 앞으로 (매달린 팔은 -X 가 앞)
            Quaternion elbL = Quaternion.Euler(-20f - 10f * runNorm, 0f, 0f);
            Quaternion elbR = IsKing ? Quaternion.Euler(-20f - 10f * runNorm, 0f, 0f) : Quaternion.Euler(-60f, 10f, 0f);

            // 소환 (왕): 양팔 들어 올림
            if (_summonT >= 0f)
            {
                _summonT += dt;
                float t = Mathf.Clamp01(_summonT / 0.8f);
                float up = Mathf.Sin(t * Mathf.PI);
                armL = Quaternion.Euler(-150f * up + 8f, 0f, 25f * up + 10f);
                armR = Quaternion.Euler(-150f * up + 8f, 0f, -25f * up - 10f);
                elbL = elbR = Quaternion.Euler(-30f * up - 10f, 0f, 0f);
                if (t >= 1f) _summonT = -1f;
            }

            // 공격 (기사): 상대가 모션을 보고 반응해야 하므로 크고 분명하게. 판정 타이밍과 맞춤 —
            //   베기: 0.06초에 닿고 0.4초 주기. 어깨 위 오른쪽에서 왼쪽 아래로 크게 내려치는 사선 베기 + 몸통 비틀기.
            //   찌르기: 0.2초 동안 팔을 뒤로 당겼다가(예고) 0.2초에 닿고 0.3초 쭉 뻗어 멈춤 + 몸 앞으로 기울임, 1.0초 주기.
            if (_attackT >= 0f && !IsKing)
            {
                _attackT += dt;
                if (_attackType == KnifeAttack.Stab)
                {
                    float t = Mathf.Clamp01(_attackT / 1.0f);
                    float k;      // 0 = 기본, -1 = 뒤로 당김, 1 = 쭉 뻗음
                    if (t < 0.20f) k = -Mathf.SmoothStep(0f, 1f, t / 0.20f);                           // 예고: 뒤로 당김
                    else if (t < 0.26f) k = Mathf.Lerp(-1f, 1f, (t - 0.20f) / 0.06f);                // 순간 찌름
                    else if (t < 0.55f) k = 1f;                                                      // 뻗은 채 멈춤
                    else k = 1f - Mathf.SmoothStep(0f, 1f, (t - 0.55f) / 0.45f);                     // 복귀
                    float back = Mathf.Max(0f, -k), fwd = Mathf.Max(0f, k);
                    armR = Quaternion.Euler(-35f + 30f * back - 70f * fwd, -20f + 15f * fwd, -14f + 20f * back);   // 뒤로 당김 → 앞으로 쭉
                    elbR = Quaternion.Euler(-60f - 50f * back + 55f * fwd, 10f, 0f);
                    armL = Quaternion.Euler(8f - 25f * fwd, 0f, 10f + 15f * fwd);                      // 왼팔은 균형 잡느라 뒤로
                    _torso.localRotation *= Quaternion.Euler(16f * fwd - 6f * back, -22f * fwd + 12f * back, 0f);
                    _hips.localPosition += new Vector3(0f, -0.04f * fwd, 0.10f * fwd);               // 앞으로 내딛음
                    if (t >= 1f) _attackT = -1f;
                }
                else
                {
                    float t = Mathf.Clamp01(_attackT / 0.40f);
                    // 0~0.06초: 오른쪽 위로 들어 올림 → 0.06~0.24초: 왼쪽 아래로 크게 베어 내림 → 복귀
                    float sw = t < 0.15f ? -Mathf.SmoothStep(0f, 1f, t / 0.15f)
                        : (t < 0.6f ? Mathf.Lerp(-1f, 1f, Mathf.SmoothStep(0f, 1f, (t - 0.15f) / 0.45f))
                        : Mathf.Lerp(1f, 0f, (t - 0.6f) / 0.4f));
                    float u = (sw + 1f) * 0.5f;                                                      // 0 = 오른쪽 위, 1 = 왼쪽 아래
                    armR = Quaternion.Euler(Mathf.Lerp(-120f, -40f, u), Mathf.Lerp(-80f, 70f, u), Mathf.Lerp(-40f, 10f, u));
                    elbR = Quaternion.Euler(Mathf.Lerp(-50f, -10f, u), 0f, 0f);
                    armL = Quaternion.Euler(8f + 20f * Mathf.Abs(sw), 0f, 10f + 20f * Mathf.Max(0f, sw));
                    _torso.localRotation *= Quaternion.Euler(10f * Mathf.Max(0f, sw), 38f * sw, -8f * sw);
                    if (t >= 1f) _attackT = -1f;
                }
            }

            _shoulderL.localRotation = armL;
            _shoulderR.localRotation = armR;
            _elbowL.localRotation = elbL;
            _elbowR.localRotation = elbR;
            _head.localRotation = Quaternion.Euler(-6f * cr, 0f, 0f);

            // 망토: 달리면 뒤로 날림, 멈추면 늘어짐 + 살랑임
            float target = 10f + 55f * runNorm;
            _capeSwing = Mathf.Lerp(_capeSwing, target, Mathf.Clamp01(dt * 4f));
            for (int i = 0; i < _cape.Length; i++)
            {
                float wave = Mathf.Sin(_idleT * 2.2f + i * 0.9f) * (2f + 6f * runNorm);
                _cape[i].localRotation = Quaternion.Euler((i == 0 ? _capeSwing : _capeSwing * 0.25f) + wave, 0f, 0f);   // +X = 뒤로 날림
            }
        }

        void ResetPose()
        {
            _hips.localPosition = new Vector3(0f, HipY, 0f);
            _torso.localRotation = Quaternion.identity;
            _hipL.localRotation = _hipR.localRotation = _kneeL.localRotation = _kneeR.localRotation = Quaternion.identity;
            _elbowL.localRotation = _elbowR.localRotation = Quaternion.identity;
            foreach (var c in _cape) c.localRotation = Quaternion.Euler(8f, 0f, 0f);
        }
    }
}
