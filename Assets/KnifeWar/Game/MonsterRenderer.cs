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
    /// 몬스터를 GPU 인스턴싱으로 그린다 (오브젝트 생성 없음). 팀 2 × 종류 3 = 6묶음.
    /// SkinSet에 몬스터 메시·재질이 있으면 그걸로, 없으면 기본 캡슐.
    /// </summary>
    public sealed class MonsterRenderer : MonoBehaviour
    {
        const int Batch = 1023;
        const int Types = 3;
        const int Groups = 2 * Types;   // [team * Types + type]

        MatchController _m;
        readonly Mesh[] _mesh = new Mesh[Types];
        readonly RenderParams[] _rp = new RenderParams[Groups];
        NativeArray<Matrix4x4> _out;      // [group * Capacity + n]
        NativeArray<int> _counts;
        NativeArray<float3> _scale;       // 종류별 크기 (x, y, z)
        NativeArray<float> _lift;         // 종류별 바닥에서 띄우는 높이 (캡슐은 중심이 가운데)

        public void Init(MatchController m)
        {
            _m = m;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            var capsule = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);

            var skin = SkinSet.Current;
            _scale = new NativeArray<float3>(Types, Allocator.Persistent);
            _lift = new NativeArray<float>(Types, Allocator.Persistent);
            // 기본 캡슐: 졸병 0.8m, 탱커 1.3m, 원거리 1.0m 높이
            float[] ds = { 0.4f, 0.65f, 0.33f }, dh = { 0.42f, 0.65f, 0.5f };
            for (int t = 0; t < Types; t++)
            {
                var custom = skin != null && skin.MonsterMesh != null && t < skin.MonsterMesh.Length ? skin.MonsterMesh[t] : null;
                if (custom != null)
                {
                    float k = skin.MonsterScale != null && t < skin.MonsterScale.Length && skin.MonsterScale[t] > 0f ? skin.MonsterScale[t] : 1f;
                    _mesh[t] = custom;
                    _scale[t] = new float3(k, k, k);
                    _lift[t] = 0f;   // 사용자 메시는 발이 원점
                }
                else
                {
                    _mesh[t] = capsule;
                    _scale[t] = new float3(ds[t], dh[t], ds[t]);
                    _lift[t] = dh[t];
                }
            }

            var bounds = new Bounds(Vector3.zero, new Vector3(300f, 60f, 60f));
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
                    _rp[team * Types + t] = new RenderParams(mat)
                    {
                        worldBounds = bounds,
                        shadowCastingMode = ShadowCastingMode.On,
                        receiveShadows = true,
                    };
            }

            _out = new NativeArray<Matrix4x4>(m.Monsters.Capacity * Groups, Allocator.Persistent);
            _counts = new NativeArray<int>(Groups, Allocator.Persistent);
        }

        void OnDestroy()
        {
            if (_out.IsCreated) _out.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_scale.IsCreated) _scale.Dispose();
            if (_lift.IsCreated) _lift.Dispose();
        }

        void LateUpdate()
        {
            if (_m == null || _m.Monsters == null || !_out.IsCreated) return;
            var s = _m.Monsters;
            s.Complete();

            new BuildJob
            {
                Capacity = s.Capacity,
                HalfLength = MatchController.LaneHalf,
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
            }.Run();

            int cap = s.Capacity;
            for (int g = 0; g < Groups; g++)
            {
                int count = _counts[g];
                if (count == 0 || _rp[g].material == null) continue;
                var mesh = _mesh[g % Types];
                for (int start = 0; start < count; start += Batch)
                    Graphics.RenderMeshInstanced(_rp[g], mesh, 0, _out, math.min(Batch, count - start), g * cap + start);
            }
        }

        [BurstCompile]
        struct BuildJob : IJob
        {
            public int Capacity;
            public float HalfLength;
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

            public void Execute()
            {
                for (int g = 0; g < Groups; g++) Counts[g] = 0;
                var face0 = quaternion.RotateY(math.PI * 0.5f);
                var face1 = quaternion.RotateY(-math.PI * 0.5f);
                for (int m = 0; m < Capacity; m++)
                {
                    if (Alive[m] == 0) continue;
                    int type = math.clamp(Type[m], 0, Types - 1);
                    int team = Team[m] == 0 ? 0 : 1;
                    bool attacking = State[m] == 1;
                    float bob = attacking
                        ? math.abs(math.sin(Time * 14f + m)) * 0.12f
                        : math.abs(math.sin(Time * 9f + m * 0.37f)) * 0.07f;
                    float3 p = new float3(Pos[m] / 1000f - HalfLength, Lift[type] + bob, Lat[m] / 1000f);
                    int g = team * Types + type;
                    int n = Counts[g];
                    Out[g * Capacity + n] = (Matrix4x4)float4x4.TRS(p, team == 0 ? face0 : face1, Scale[type]);
                    Counts[g] = n + 1;
                }
            }
        }
    }
}
