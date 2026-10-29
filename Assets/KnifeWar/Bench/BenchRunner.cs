using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using KnifeWar.Core;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace KnifeWar.Bench
{
    public enum BenchMode
    {
        ServerSim,     // 서버 CPU: 128틱에서 몇 마리까지 버티나
        ClientRender,  // 유저 PC: 60fps에서 몇 마리까지 그리나
    }

    /// <summary>
    /// 1단계 프로토타입 — 몬스터 최대치 벤치마크.
    /// 몬스터 수를 단계적으로 늘리면서 서버 틱 시간 / 클라 프레임을 측정하고, 한계를 넘는 지점을 찾는다.
    ///
    /// 명령줄 인자 (빌드 실행 시):
    ///   -kwMode server|client  -tickRate 128  -workers 0  -budget 40
    ///   -start 500  -step 500  -max 30000  -targetFps 60
    /// </summary>
    public sealed class BenchRunner : MonoBehaviour
    {
        [Header("공통")]
        public BenchMode Mode = BenchMode.ClientRender;
        public int TickRate = 128;

        [Tooltip("잡 워커 스레드 수. 0 = 메인 스레드만 (vCPU 1개 서버 흉내), -1 = 유니티 기본값")]
        public int Workers = -1;

        public int StartCount = 500;
        public int StepCount = 500;
        public int MaxCount = 30000;

        [Header("서버 벤치")]
        [Tooltip("한 틱 시간 중 몬스터에 쓸 수 있는 비율(%). 나머지는 플레이어·넷코드 몫")]
        [Range(5f, 100f)] public float BudgetPercent = 40f;
        public int WarmupTicks = 384;
        public int MeasureTicks = 1024;
        public int KnightQueriesPerTick = 12;

        [Header("클라 벤치")]
        public float TargetFps = 60f;
        public float RenderStepSeconds = 6f;
        public float RenderWarmupSeconds = 2f;
        public Mesh MonsterMesh;
        public Material Team0Material;
        public Material Team1Material;

        const int MaxSpawnPerTickPerTeam = 64;
        const int MaxSimTicksPerFrame = 8;
        const int InstanceBatch = 1023;

        MonsterSim _sim;
        NativeArray<MeleeQuery> _queries;
        NativeArray<int> _results;
        int _target;
        bool _done;
        int _spawnSerial;
        string _resultLine = "";
        string _outPath;
        readonly List<string> _csv = new List<string>();

        // 서버 측정
        int _phaseTick;
        readonly List<float> _samples = new List<float>(4096);
        double _bytesSum;
        long _byteTicks;
        int _bestServer;
        string _lastServerRow = "";

        // 클라 측정
        float _acc;
        float _stepTime;
        readonly List<float> _frameMs = new List<float>(4096);
        double _simMsSum;
        int _simMsCount;
        int _bestClient;
        string _lastClientRow = "";
        NativeArray<Matrix4x4> _inst0, _inst1;
        NativeArray<int> _instCount;
        RenderParams _rp0, _rp1;
        float _fps;

        // ------------------------------------------------------------------

        void Awake()
        {
            ApplyCommandLine();
#if UNITY_SERVER
            Mode = BenchMode.ServerSim;
#endif
            if (Workers >= 0)
            {
                try
                {
                    JobsUtility.JobWorkerCount = Mathf.Clamp(Workers, 0, JobsUtility.JobWorkerMaximumCount);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[KnifeWar] 워커 수 설정 실패: {e.Message}");
                }
            }

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            int capacity = MaxCount + 2 * MaxSpawnPerTickPerTeam + 16;
            _sim = new MonsterSim(capacity, TickRate, LaneConfig.Default);
            _queries = new NativeArray<MeleeQuery>(math.max(1, KnightQueriesPerTick), Allocator.Persistent);
            _results = new NativeArray<int>(_queries.Length, Allocator.Persistent);
            _target = StartCount;

            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            _outPath = Path.Combine(Application.persistentDataPath,
                $"bench_{(Mode == BenchMode.ServerSim ? "server" : "client")}_{stamp}.csv");

            if (Mode == BenchMode.ServerSim)
            {
                _csv.Add("monsters,alive,avg_ms,p99_ms,max_ms,budget_ms,kb_per_sec_per_client,gb_per_match_hour,pass");
            }
            else
            {
                _csv.Add("monsters,alive,avg_fps,low1_fps,sim_ms_avg,pass");
                SetupRendering();
            }

            Debug.Log($"[KnifeWar] 벤치 시작 mode={Mode} tick={TickRate} workers={JobsUtility.JobWorkerCount} " +
                      $"start={StartCount} step={StepCount} max={MaxCount}");
        }

        void OnDestroy()
        {
            _sim?.Dispose();
            if (_queries.IsCreated) _queries.Dispose();
            if (_results.IsCreated) _results.Dispose();
            if (_inst0.IsCreated) _inst0.Dispose();
            if (_inst1.IsCreated) _inst1.Dispose();
            if (_instCount.IsCreated) _instCount.Dispose();
        }

        void Update()
        {
            if (Mode == BenchMode.ServerSim) UpdateServer();
            else UpdateClient();
        }

        // ------------------------------------------------------------------
        // 공통: 몬스터 수 유지 + 기사 칼 판정 쿼리
        // ------------------------------------------------------------------

        void SimTick()
        {
            _sim.PrepareTick();

            for (byte team = 0; team < 2; team++)
            {
                int want = _target / 2 - _sim.AliveInTeam(team);
                int n = math.min(want, MaxSpawnPerTickPerTeam);
                for (int i = 0; i < n; i++)
                {
                    if (!_sim.TrySpawn(team, (byte)(_spawnSerial++ % MonsterDefs.TypeCount))) break;
                }
            }

            _sim.Step();

            // 전선(다리 중앙) 근처에서 기사들이 매 틱 칼을 휘두른다고 가정 — 최악 조건
            if (KnightQueriesPerTick > 0)
            {
                int mid = _sim.Lane.LengthMm / 2;
                int rewind = (int)math.round(0.1f * TickRate); // 100ms 되감기
                for (int q = 0; q < _queries.Length; q++)
                {
                    byte team = (byte)(q & 1);
                    float dir = team == 0 ? 1f : -1f;
                    _queries[q] = new MeleeQuery
                    {
                        Origin = new float2(mid + (q - _queries.Length / 2) * 400 - dir * 1500f,
                            ((q * 977) % 3000) - 1500),
                        Dir = new float2(dir, 0f),
                        RangeMm = 1800f,
                        RadiusMm = 250f,
                        RewindTick = _sim.Tick - rewind,
                        AttackerTeam = team,
                    };
                }
                _sim.QueryMelee(_queries, _results);
            }
        }

        // ------------------------------------------------------------------
        // 서버 벤치
        // ------------------------------------------------------------------

        void UpdateServer()
        {
            if (_done) return;
            var slice = Stopwatch.StartNew();
            while (!_done && slice.Elapsed.TotalMilliseconds < 30.0)
            {
                long t0 = Stopwatch.GetTimestamp();
                SimTick();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                _phaseTick++;
                if (_phaseTick > WarmupTicks)
                {
                    _samples.Add((float)ms);
                    var c = _sim.Counters;
                    _bytesSum += NetEstimate.BytesPerTick(
                        c[SimCounter.SpawnEvents], c[SimCounter.HpEvents], c[SimCounter.DeathEvents],
                        c[SimCounter.StateEvents], _sim.AliveCount);
                    _byteTicks++;
                }

                if (_phaseTick >= WarmupTicks + MeasureTicks) FinishServerStep();
            }
        }

        void FinishServerStep()
        {
            _samples.Sort();
            float avg = 0f;
            for (int i = 0; i < _samples.Count; i++) avg += _samples[i];
            avg /= math.max(1, _samples.Count);
            float p99 = _samples[math.min(_samples.Count - 1, (int)(_samples.Count * 0.99f))];
            float max = _samples[_samples.Count - 1];
            float budget = 1000f / TickRate * BudgetPercent / 100f;
            float kbps = NetEstimate.KBytesPerSecPerClient((float)(_bytesSum / math.max(1, _byteTicks)), TickRate);
            float gbph = NetEstimate.GBPerMatchHour(kbps);
            bool pass = p99 <= budget;

            _lastServerRow = $"{_target}마리: 평균 {avg:F2}ms / p99 {p99:F2}ms (예산 {budget:F2}ms) / " +
                             $"클라당 {kbps:F1}KB/s → {(pass ? "통과" : "초과")}";
            Debug.Log("[KnifeWar] " + _lastServerRow);
            _csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F3},{3:F3},{4:F3},{5:F3},{6:F2},{7:F3},{8}",
                _target, _sim.AliveCount, avg, p99, max, budget, kbps, gbph, pass ? 1 : 0));

            if (pass) _bestServer = _target;

            _samples.Clear();
            _bytesSum = 0;
            _byteTicks = 0;
            _phaseTick = 0;

            if (!pass || _target + StepCount > MaxCount)
            {
                _resultLine = _bestServer > 0
                    ? $"서버 최대치: {_bestServer}마리 ({TickRate}틱, 워커 {JobsUtility.JobWorkerCount}, 예산 {BudgetPercent}%)"
                    : "시작 수에서 이미 예산 초과 — -start 값을 줄여서 다시 실행하세요";
                Finish();
            }
            else
            {
                _target += StepCount;
            }
        }

        // ------------------------------------------------------------------
        // 클라 렌더 벤치
        // ------------------------------------------------------------------

        void SetupRendering()
        {
            if (MonsterMesh == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                MonsterMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(tmp);
            }
            if (Team0Material == null) Team0Material = MakeMaterial(new Color(0.25f, 0.45f, 0.95f));
            if (Team1Material == null) Team1Material = MakeMaterial(new Color(0.90f, 0.30f, 0.25f));
            Team0Material.enableInstancing = true;
            Team1Material.enableInstancing = true;

            var bounds = new Bounds(Vector3.zero, new Vector3(400f, 100f, 400f));
            _rp0 = new RenderParams(Team0Material) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };
            _rp1 = new RenderParams(Team1Material) { worldBounds = bounds, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false };

            _inst0 = new NativeArray<Matrix4x4>(_sim.Capacity, Allocator.Persistent);
            _inst1 = new NativeArray<Matrix4x4>(_sim.Capacity, Allocator.Persistent);
            _instCount = new NativeArray<int>(2, Allocator.Persistent);
        }

        static Material MakeMaterial(Color c)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { enableInstancing = true, color = c };
            return m;
        }

        void UpdateClient()
        {
            float dt = Time.unscaledDeltaTime;

            // 시뮬레이션 (고정 틱)
            if (!_done)
            {
                _acc += dt;
                float tickDt = 1f / TickRate;
                int n = 0;
                long t0 = Stopwatch.GetTimestamp();
                while (_acc >= tickDt && n < MaxSimTicksPerFrame)
                {
                    SimTick();
                    _acc -= tickDt;
                    n++;
                }
                if (n == MaxSimTicksPerFrame) _acc = 0f;
                if (n > 0)
                {
                    _simMsSum += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency / n;
                    _simMsCount++;
                }
            }

            // 렌더링 (GPU 인스턴싱)
            new BuildInstancesJob
            {
                Capacity = _sim.Capacity,
                HalfLengthM = _sim.Lane.LengthMm / 2000f,
                Time = Time.time,
                Alive = _sim.Alive,
                Team = _sim.Team,
                Type = _sim.Type,
                Pos = _sim.Pos,
                Lat = _sim.Lat,
                Out0 = _inst0,
                Out1 = _inst1,
                Counts = _instCount,
            }.Run();

            Draw(_rp0, _inst0, _instCount[0]);
            Draw(_rp1, _inst1, _instCount[1]);

            if (dt > 0f) _fps = math.lerp(_fps, 1f / dt, 0.05f);
            if (_done) return;

            // 측정
            _stepTime += dt;
            if (_stepTime > RenderWarmupSeconds) _frameMs.Add(dt * 1000f);
            if (_stepTime >= RenderStepSeconds) FinishClientStep();
        }

        void Draw(in RenderParams rp, NativeArray<Matrix4x4> data, int count)
        {
            for (int start = 0; start < count; start += InstanceBatch)
            {
                Graphics.RenderMeshInstanced(rp, MonsterMesh, 0, data, math.min(InstanceBatch, count - start), start);
            }
        }

        void FinishClientStep()
        {
            _frameMs.Sort();
            float avgMs = 0f;
            for (int i = 0; i < _frameMs.Count; i++) avgMs += _frameMs[i];
            avgMs /= math.max(1, _frameMs.Count);
            float low1Ms = _frameMs.Count > 0 ? _frameMs[math.min(_frameMs.Count - 1, (int)(_frameMs.Count * 0.99f))] : 0f;
            float avgFps = avgMs > 0f ? 1000f / avgMs : 0f;
            float low1Fps = low1Ms > 0f ? 1000f / low1Ms : 0f;
            float simMs = _simMsCount > 0 ? (float)(_simMsSum / _simMsCount) : 0f;
            bool pass = avgFps >= TargetFps;

            _lastClientRow = $"{_target}마리: 평균 {avgFps:F0}fps / 1% 최저 {low1Fps:F0}fps / 시뮬 {simMs:F2}ms → {(pass ? "통과" : "미달")}";
            Debug.Log("[KnifeWar] " + _lastClientRow);
            _csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F1},{3:F1},{4:F3},{5}",
                _target, _sim.AliveCount, avgFps, low1Fps, simMs, pass ? 1 : 0));

            if (pass) _bestClient = _target;

            _frameMs.Clear();
            _stepTime = 0f;
            _simMsSum = 0;
            _simMsCount = 0;

            if (!pass || _target + StepCount > MaxCount)
            {
                _resultLine = _bestClient > 0
                    ? $"클라 최대치: {_bestClient}마리 ({TargetFps}fps 기준, 이 PC 사양)"
                    : "시작 수에서 이미 목표 fps 미달 — -start 값을 줄여서 다시 실행하세요";
                Finish();
            }
            else
            {
                _target += StepCount;
            }
        }

        // ------------------------------------------------------------------

        void Finish()
        {
            _done = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_outPath));
                var sb = new StringBuilder();
                foreach (var line in _csv) sb.AppendLine(line);
                File.WriteAllText(_outPath, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[KnifeWar] {_resultLine}\n결과 파일: {_outPath}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[KnifeWar] 결과 저장 실패: {e.Message}");
            }

            if (Application.isBatchMode) Application.Quit(0);
        }

        void OnGUI()
        {
            var r = new Rect(12, 12, 900, 22);
            GUI.Label(r, $"KnifeWar 몬스터 벤치 — {(Mode == BenchMode.ServerSim ? "서버 시뮬" : "클라 렌더")} / {TickRate}틱 / 워커 {JobsUtility.JobWorkerCount}");
            r.y += 22; GUI.Label(r, $"목표 {_target}마리 / 생존 {_sim?.AliveCount ?? 0} / fps {_fps:F0}");
            r.y += 22; GUI.Label(r, Mode == BenchMode.ServerSim ? _lastServerRow : _lastClientRow);
            if (_done)
            {
                r.y += 22; GUI.Label(r, _resultLine);
                r.y += 22; GUI.Label(r, "결과 파일: " + _outPath);
            }
        }

        void ApplyCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                string k = args[i];
                string v = args[i + 1];
                switch (k)
                {
                    case "-kwMode":
                        Mode = v.Equals("server", StringComparison.OrdinalIgnoreCase) ? BenchMode.ServerSim : BenchMode.ClientRender;
                        break;
                    case "-tickRate": int.TryParse(v, out TickRate); break;
                    case "-workers": int.TryParse(v, out Workers); break;
                    case "-budget": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out BudgetPercent); break;
                    case "-start": int.TryParse(v, out StartCount); break;
                    case "-step": int.TryParse(v, out StepCount); break;
                    case "-max": int.TryParse(v, out MaxCount); break;
                    case "-targetFps": float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out TargetFps); break;
                }
            }
            TickRate = math.max(8, TickRate);
            StepCount = math.max(1, StepCount);
            StartCount = math.max(2, StartCount);
            MaxCount = math.max(StartCount, MaxCount);
        }
    }

    /// <summary>살아있는 몬스터를 팀별 인스턴스 행렬로 변환 (레인 좌표 → 월드 좌표).</summary>
    [BurstCompile(CompileSynchronously = true)]
    struct BuildInstancesJob : IJob
    {
        public int Capacity;
        public float HalfLengthM;
        public float Time;
        [ReadOnly] public NativeArray<byte> Alive;
        [ReadOnly] public NativeArray<byte> Team;
        [ReadOnly] public NativeArray<byte> Type;
        [ReadOnly] public NativeArray<int> Pos;
        [ReadOnly] public NativeArray<short> Lat;
        public NativeArray<Matrix4x4> Out0;
        public NativeArray<Matrix4x4> Out1;
        public NativeArray<int> Counts;

        public void Execute()
        {
            int n0 = 0, n1 = 0;
            var face0 = quaternion.RotateY(math.PI * 0.5f);
            var face1 = quaternion.RotateY(-math.PI * 0.5f);

            for (int m = 0; m < Capacity; m++)
            {
                if (Alive[m] == 0) continue;

                float s = Type[m] == 1 ? 0.6f : (Type[m] == 2 ? 0.35f : 0.45f);
                float bob = math.abs(math.sin(Time * 8f + m * 0.37f)) * 0.15f; // 애니메이션 흉내
                float3 p = new float3(Pos[m] / 1000f - HalfLengthM, 0.25f + s + bob, Lat[m] / 1000f);

                if (Team[m] == 0) Out0[n0++] = float4x4.TRS(p, face0, new float3(s));
                else Out1[n1++] = float4x4.TRS(p, face1, new float3(s));
            }

            Counts[0] = n0;
            Counts[1] = n1;
        }
    }
}
