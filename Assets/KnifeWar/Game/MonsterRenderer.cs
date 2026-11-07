using System.Collections.Generic;
using KnifeWar.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace KnifeWar.Game
{
    /// <summary>
    /// 몬스터를 GPU 인스턴싱으로 그린다 (오브젝트 생성 없음). 팀 2 × 종류 7 = 14묶음.
    /// SkinSet에 몬스터 메시·재질이 있으면 그걸로, 없으면 코드로 조립한 임시 모양 (ProceduralMonsterMeshes).
    /// 높이는 MapLayout.HeightAt (성 경사로·2층) 을 따른다 — 시뮬레이션은 2D라서 그릴 때만 올린다.
    /// </summary>
    public sealed class MonsterRenderer : MonoBehaviour
    {
        const int Batch = 1023;
        const int Types = MonsterDefs.TypeCount;
        const int Groups = 2 * Types;   // [team * Types + type]

        MatchController _m;
        readonly Mesh[] _mesh = new Mesh[Types];
        readonly RenderParams[] _rp = new RenderParams[Groups];
        NativeArray<Matrix4x4> _out;      // [group * Capacity + n]
        NativeArray<int> _counts;
        NativeArray<float3> _scale;       // 종류별 크기 (x, y, z)
        NativeArray<float> _lift;         // 종류별 바닥에서 띄우는 높이 (캡슐은 중심이 가운데)
        NativeArray<Matrix4x4> _arrowOut; // [team * MaxArrows + n]
        NativeArray<int> _arrowCounts;
        Mesh _arrowMesh;
        readonly RenderParams[] _arrowRp = new RenderParams[2];

        public void Init(MatchController m)
        {
            _m = m;
            // 헤드리스(-nographics)·그래픽 장치 없음 → 그리지 않음. 인스턴싱 미지원 GPU → 한 마리씩 그림
            _canDraw = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
            _instancing = SystemInfo.supportsInstancing;
            var skin = SkinSet.Current;
            _scale = new NativeArray<float3>(Types, Allocator.Persistent);
            _lift = new NativeArray<float>(Types, Allocator.Persistent);
            for (int t = 0; t < Types; t++)
            {
                var custom = skin != null && skin.MonsterMesh != null && t < skin.MonsterMesh.Length ? skin.MonsterMesh[t] : null;
                if (custom != null)
                {
                    float k = skin.MonsterScale != null && t < skin.MonsterScale.Length && skin.MonsterScale[t] > 0f ? skin.MonsterScale[t] : 1f;
                    _mesh[t] = custom;
                    _scale[t] = new float3(k, k, k);
                }
                else
                {
                    _mesh[t] = ProceduralMonsterMeshes.Get(t);   // 발이 원점
                    _scale[t] = new float3(1f, 1f, 1f);
                }
                _lift[t] = 0f;
            }

            var bounds = new Bounds(Vector3.zero, new Vector3(400f, 60f, 60f));
            Material fire = null;
            for (int team = 0; team < 2; team++)
            {
                Material mat = null;
                if (skin != null) mat = team == 0 ? skin.MonsterTeam0 : skin.MonsterTeam1;
                if (mat == null) mat = team == 0 ? (m.MonsterBlueMat != null ? m.MonsterBlueMat : m.BlueMat) : (m.MonsterRedMat != null ? m.MonsterRedMat : m.RedMat);
                if (mat != null && !mat.enableInstancing)
                {
                    mat = new Material(mat) { enableInstancing = true };   // 인스턴싱이 꺼진 재질이면 켠 사본을 쓴다
                }
                for (int t = 0; t < Types; t++)
                {
                    Material use = mat;
                    if (t == MonsterDefs.FireGolem && (skin == null || skin.MonsterMesh == null || t >= skin.MonsterMesh.Length || skin.MonsterMesh[t] == null))
                        use = fire ??= ProceduralMonsterMeshes.FireMaterial(mat);
                    _rp[team * Types + t] = new RenderParams(use)
                    {
                        worldBounds = bounds,
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                    };
                }
            }

            _out = new NativeArray<Matrix4x4>(m.Monsters.Capacity * Groups, Allocator.Persistent);
            _counts = new NativeArray<int>(Groups, Allocator.Persistent);
            _arrowOut = new NativeArray<Matrix4x4>(2 * MonsterSim.MaxArrows, Allocator.Persistent);
            _arrowCounts = new NativeArray<int>(2, Allocator.Persistent);
            _arrowMesh = ProceduralMonsterMeshes.Arrow();
            for (int team = 0; team < 2; team++) _arrowRp[team] = new RenderParams(_rp[team * Types].material) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off };
        }

        void OnDestroy()
        {
            if (_out.IsCreated) _out.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_scale.IsCreated) _scale.Dispose();
            if (_lift.IsCreated) _lift.Dispose();
            if (_arrowOut.IsCreated) _arrowOut.Dispose();
            if (_arrowCounts.IsCreated) _arrowCounts.Dispose();
        }

        bool _canDraw = true, _instancing = true;

        void LateUpdate()
        {
            if (_m == null || _m.Monsters == null || !_out.IsCreated || !_canDraw) return;
            var s = _m.Monsters;
            s.Complete();

            new BuildJob
            {
                Capacity = s.Capacity,
                LaneHalf = MapLayout.LaneHalf,
                RampStart = MapLayout.RampStartX,
                RampEnd = MapLayout.RampEndX,
                Floor2 = MapLayout.Floor2Y,
                Time = Time.time,
                Alive = s.Alive,
                Team = s.Team,
                Type = s.Type,
                State = s.State,
                Pos = s.Pos,
                Lat = s.Lat,
                Scale = _scale,
                Lift = _lift,
                Out = _out,
                Counts = _counts,
                ArrowAlive = s.ArrowAlive,
                ArrowTeam = s.ArrowTeam,
                ArrowPos = s.ArrowPos,
                ArrowLat = s.ArrowLat,
                ArrowVx = s.ArrowVx,
                ArrowVy = s.ArrowVy,
                ArrowOut = _arrowOut,
                ArrowCounts = _arrowCounts,
            }.Run();

            int cap = s.Capacity;
            for (int g = 0; g < Groups; g++)
            {
                int count = _counts[g];
                if (count == 0 || _rp[g].material == null) continue;
                var mesh = _mesh[g % Types];
                if (_instancing)
                {
                    for (int start = 0; start < count; start += Batch)
                        Graphics.RenderMeshInstanced(_rp[g], mesh, 0, _out, math.min(Batch, count - start), g * cap + start);
                }
                else
                {
                    // 인스턴싱이 안 되는 GPU: 한 마리씩 (느리지만 보이긴 한다)
                    for (int i = 0; i < count; i++) Graphics.RenderMesh(_rp[g], mesh, 0, _out[g * cap + i]);
                }
            }
            for (int team = 0; team < 2; team++)
            {
                int count = _arrowCounts[team];
                if (count == 0 || _arrowRp[team].material == null) continue;
                if (_instancing) Graphics.RenderMeshInstanced(_arrowRp[team], _arrowMesh, 0, _arrowOut, count, team * MonsterSim.MaxArrows);
                else for (int i = 0; i < count; i++) Graphics.RenderMesh(_arrowRp[team], _arrowMesh, 0, _arrowOut[team * MonsterSim.MaxArrows + i]);
            }
        }

        [BurstCompile]
        struct BuildJob : IJob
        {
            public int Capacity;
            public float LaneHalf, RampStart, RampEnd, Floor2;
            public float Time;
            [ReadOnly] public NativeArray<byte> Alive;
            [ReadOnly] public NativeArray<byte> Team;
            [ReadOnly] public NativeArray<byte> Type;
            [ReadOnly] public NativeArray<byte> State;
            [ReadOnly] public NativeArray<int> Pos;
            [ReadOnly] public NativeArray<short> Lat;
            [ReadOnly] public NativeArray<float3> Scale;
            [ReadOnly] public NativeArray<float> Lift;
            public NativeArray<Matrix4x4> Out;
            public NativeArray<int> Counts;
            [ReadOnly] public NativeArray<byte> ArrowAlive;
            [ReadOnly] public NativeArray<byte> ArrowTeam;
            [ReadOnly] public NativeArray<int> ArrowPos, ArrowLat, ArrowVx, ArrowVy;
            public NativeArray<Matrix4x4> ArrowOut;
            public NativeArray<int> ArrowCounts;

            float Ground(float x)
            {
                float ax = math.abs(x);
                return ax <= RampStart ? 0f : (ax >= RampEnd ? Floor2 : (ax - RampStart) / (RampEnd - RampStart) * Floor2);
            }

            public void Execute()
            {
                for (int g = 0; g < Groups; g++) Counts[g] = 0;
                var face0 = quaternion.RotateY(math.PI * 0.5f);
                var face1 = quaternion.RotateY(-math.PI * 0.5f);
                var windupTilt = quaternion.RotateX(-0.3f);   // 준비: 뒤로 젖힘 (피하라는 신호)
                var strikeTilt = quaternion.RotateX(0.25f);   // 공격: 앞으로 숙임
                for (int m = 0; m < Capacity; m++)
                {
                    if (Alive[m] == 0) continue;
                    int type = math.clamp(Type[m], 0, Types - 1);
                    int team = Team[m] == 0 ? 0 : 1;
                    byte st = State[m];
                    bool attacking = st == (byte)MonsterState.Attack;
                    bool windup = st == (byte)MonsterState.Windup;
                    float bob = attacking
                        ? math.abs(math.sin(Time * 14f + m)) * 0.12f
                        : windup ? 0.18f : math.abs(math.sin(Time * 9f + m * 0.37f)) * 0.07f;
                    float x = Pos[m] / 1000f - LaneHalf;
                    float3 p = new float3(x, Ground(x) + Lift[type] + bob, Lat[m] / 1000f);
                    var face = team == 0 ? face0 : face1;
                    var rot = windup ? math.mul(face, windupTilt) : attacking ? math.mul(face, strikeTilt) : face;
                    int g = team * Types + type;
                    int n = Counts[g];
                    Out[g * Capacity + n] = (Matrix4x4)float4x4.TRS(p, rot, Scale[type]);
                    Counts[g] = n + 1;
                }

                ArrowCounts[0] = ArrowCounts[1] = 0;
                for (int i = 0; i < MonsterSim.MaxArrows; i++)
                {
                    if (ArrowAlive[i] == 0) continue;
                    int team = ArrowTeam[i] == 0 ? 0 : 1;
                    float x = ArrowPos[i] / 1000f - LaneHalf;
                    float3 p = new float3(x, Ground(x) + 1.3f, ArrowLat[i] / 1000f);
                    float3 v = new float3(ArrowVx[i], 0f, ArrowVy[i]);
                    var rot = math.lengthsq(v) > 0f ? quaternion.LookRotationSafe(v, math.up()) : quaternion.identity;
                    int n = ArrowCounts[team];
                    ArrowOut[team * MonsterSim.MaxArrows + n] = (Matrix4x4)float4x4.TRS(p, rot, new float3(1f, 1f, 1f));
                    ArrowCounts[team] = n + 1;
                }
            }
        }
    }

    /// <summary>
    /// 진짜 몬스터 모델이 없을 때 쓰는 임시 모양: 기본 도형(캡슐·구·상자)을 하나의 메시로 합친다.
    /// 발이 원점(y=0), 앞이 +z. 인스턴싱으로 수백 마리를 그려도 드로우콜은 종류당 1개.
    /// </summary>
    public static class ProceduralMonsterMeshes
    {
        static readonly Mesh[] _cache = new Mesh[MonsterDefs.TypeCount];
        static Mesh _capsule, _sphere, _cube;

        public static Mesh Get(int type)
        {
            type = Mathf.Clamp(type, 0, MonsterDefs.TypeCount - 1);
            if (_cache[type] != null) return _cache[type];
            EnsurePrims();
            var parts = new List<CombineInstance>();
            void Cap(float x, float y, float z, float h, float r) => Add(parts, _capsule, new Vector3(x, y, z), new Vector3(r * 2f, h * 0.5f, r * 2f));
            void Sph(float x, float y, float z, float r) => Add(parts, _sphere, new Vector3(x, y, z), new Vector3(r * 2f, r * 2f, r * 2f));
            void Box(float x, float y, float z, float sx, float sy, float sz) => Add(parts, _cube, new Vector3(x, y, z), new Vector3(sx, sy, sz));

            switch (type)
            {
                case MonsterDefs.Footman:      // 졸병: 작은 캡슐 + 창
                    Cap(0f, 0.45f, 0f, 0.9f, 0.2f);
                    Box(0.22f, 0.7f, 0.1f, 0.04f, 1.4f, 0.04f);
                    break;
                case MonsterDefs.Shield:       // 방패병: 굵은 캡슐 + 큰 방패 앞에
                    Cap(0f, 0.6f, 0f, 1.2f, 0.3f);
                    Box(0f, 0.6f, 0.36f, 0.7f, 0.9f, 0.08f);
                    break;
                case MonsterDefs.Archer:       // 궁수: 가는 캡슐 + 활
                    Cap(0f, 0.5f, 0f, 1.0f, 0.17f);
                    Box(0.2f, 0.55f, 0.05f, 0.03f, 0.9f, 0.06f);
                    break;
                case MonsterDefs.Ogre:         // 오우거: 큰 캡슐 + 머리 + 두 팔
                    Cap(0f, 1.0f, 0f, 2.0f, 0.55f);
                    Sph(0f, 2.15f, 0.1f, 0.36f);
                    Box(-0.75f, 1.1f, 0.15f, 0.3f, 1.2f, 0.3f);
                    Box(0.75f, 1.1f, 0.15f, 0.3f, 1.2f, 0.3f);
                    break;
                case MonsterDefs.TwinOgre:     // 트윈헤드 오우거: 더 큰 몸 + 머리 둘
                    Cap(0f, 1.1f, 0f, 2.2f, 0.62f);
                    Sph(-0.3f, 2.35f, 0.1f, 0.34f);
                    Sph(0.3f, 2.35f, 0.1f, 0.34f);
                    Box(-0.85f, 1.2f, 0.15f, 0.34f, 1.3f, 0.34f);
                    Box(0.85f, 1.2f, 0.15f, 0.34f, 1.3f, 0.34f);
                    break;
                case MonsterDefs.DeathKnight:  // 데스나이트: 키 큰 캡슐 + 투구 + 대검
                    Cap(0f, 0.9f, 0f, 1.8f, 0.3f);
                    Box(0f, 1.85f, 0f, 0.42f, 0.32f, 0.42f);
                    Box(0.42f, 1.0f, 0.2f, 0.08f, 1.6f, 0.16f);
                    Box(0f, 1.3f, -0.32f, 0.9f, 1.2f, 0.05f);   // 망토
                    break;
                default:                       // 화염 골렘: 상자 몸 + 머리 + 굵은 팔
                    Box(0f, 1.1f, 0f, 1.4f, 1.8f, 1.0f);
                    Box(0f, 2.3f, 0.1f, 0.7f, 0.6f, 0.7f);
                    Box(-1.0f, 1.0f, 0.1f, 0.5f, 1.7f, 0.5f);
                    Box(1.0f, 1.0f, 0.1f, 0.5f, 1.7f, 0.5f);
                    Box(-0.4f, 0.25f, 0f, 0.5f, 0.5f, 0.6f);
                    Box(0.4f, 0.25f, 0f, 0.5f, 0.5f, 0.6f);
                    break;
            }

            var mesh = new Mesh { name = "Monster_" + type };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            _cache[type] = mesh;
            return mesh;
        }

        static Mesh _arrow;

        /// <summary>화살: 가는 막대(앞 +z, 길이 0.9m) + 촉.</summary>
        public static Mesh Arrow()
        {
            if (_arrow != null) return _arrow;
            EnsurePrims();
            var parts = new List<CombineInstance>();
            Add(parts, _cube, new Vector3(0f, 0f, 0f), new Vector3(0.04f, 0.04f, 0.9f));
            Add(parts, _cube, new Vector3(0f, 0f, 0.47f), new Vector3(0.09f, 0.09f, 0.12f));
            Add(parts, _cube, new Vector3(0f, 0.05f, -0.4f), new Vector3(0.02f, 0.1f, 0.14f));
            var mesh = new Mesh { name = "Arrow" };
            mesh.CombineMeshes(parts.ToArray(), true, true);
            mesh.RecalculateBounds();
            _arrow = mesh;
            return mesh;
        }

        static void Add(List<CombineInstance> parts, Mesh m, Vector3 pos, Vector3 scale)
        {
            parts.Add(new CombineInstance { mesh = m, transform = Matrix4x4.TRS(pos, Quaternion.identity, scale) });
        }

        static void EnsurePrims()
        {
            if (_capsule != null) return;
            _capsule = Prim(PrimitiveType.Capsule);
            _sphere = Prim(PrimitiveType.Sphere);
            _cube = Prim(PrimitiveType.Cube);
        }

        static Mesh Prim(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var m = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return m;
        }

        /// <summary>화염 골렘 재질: 팀 색 대신 용암색 + 발광.</summary>
        public static Material FireMaterial(Material baseMat)
        {
            var shader = baseMat != null ? baseMat.shader : Shader.Find("Standard");
            var m = new Material(shader) { name = "Monster_Fire", enableInstancing = true, color = new Color(0.95f, 0.35f, 0.08f) };
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(1.2f, 0.35f, 0.05f));
            }
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.3f);
            return m;
        }
    }
}
