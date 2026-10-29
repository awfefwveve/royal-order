using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 멀티·백엔드 테스트 도구 (상단 메뉴 KnifeWar/멀티).
    ///
    /// 전체 흐름 테스트 (발로란트식):
    ///   1) 백엔드 실행  2) 데디 서버 실행 (백엔드 연결)  3) 매칭 클라이언트 실행
    ///   → 클라가 로그인·매칭 → 백엔드가 데디 서버에 경기 배정 → 클라가 입장 토큰으로 접속 → 경기 → 결과·MMR 저장
    /// </summary>
    public static class NetTestMenu
    {
        const string ScenePath = "Assets/KnifeWar/Scenes/Game.unity";
        const string GameExe = "Builds/Client/KnifeWar.exe";
        const string BackendDll = "Backend/server/KnifeWar.Backend.dll";

        // ---------------- 빌드 ----------------

        [MenuItem("KnifeWar/멀티/게임 빌드 (Windows)", priority = 30)]
        public static void BuildGame()
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[KnifeWar] 게임 씬이 없어요. '게임 씬 만들기'를 먼저 실행하세요."); return; }
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = GameExe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log($"[KnifeWar] 빌드 성공: {GameExe} ({report.summary.totalTime.TotalSeconds:0}초)");
            else
                Debug.LogError($"[KnifeWar] 빌드 실패: {report.summary.result}");
        }

        [MenuItem("KnifeWar/멀티/리눅스 데디 서버 빌드 (배포용)", priority = 31)]
        public static void BuildLinuxServer()
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[KnifeWar] 게임 씬이 없어요."); return; }
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = "Builds/LinuxServer/KnifeWarServer.x86_64",
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)StandaloneBuildSubtarget.Server,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
                Debug.Log("[KnifeWar] 리눅스 데디 서버 빌드 성공 → Builds/LinuxServer (Deploy/README.md 참고)");
            else
                Debug.LogError("[KnifeWar] 리눅스 서버 빌드 실패 — Unity Hub에서 이 에디터에 'Linux Dedicated Server Build Support' 모듈을 추가하세요.");
        }

        // ---------------- 백엔드 흐름 ----------------

        [MenuItem("KnifeWar/멀티/1. 백엔드 실행", priority = 50)]
        public static void RunBackend()
        {
            string dll = Path.GetFullPath(BackendDll);
            if (!File.Exists(dll)) { Debug.LogError($"[KnifeWar] 백엔드가 없어요: {dll}"); return; }

            // .NET 런타임 확인 (백엔드는 기본 .NET 8+만 필요)
            string runtimes = RunCapture("dotnet", "--list-runtimes");
            Debug.Log("[KnifeWar] dotnet --list-runtimes:\n" + (string.IsNullOrEmpty(runtimes) ? "(dotnet 없음)" : runtimes));
            if (string.IsNullOrEmpty(runtimes) || !runtimes.Contains("Microsoft.NETCore.App"))
            {
                Debug.LogError("[KnifeWar] 백엔드 실행에 필요한 .NET Runtime(8 이상)이 없어요. https://dotnet.microsoft.com/download/dotnet/8.0 에서 '.NET Runtime' Windows x64 설치 후 다시 실행하세요.");
                return;
            }
            Process.Start(new ProcessStartInfo("dotnet", $"\"{dll}\"") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(dll) });
            Debug.Log("[KnifeWar] 백엔드 실행 → http://127.0.0.1:8080 (창을 닫으면 종료)");
        }

        static string RunCapture(string file, string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                });
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return o;
            }
            catch (System.Exception e) { return "ERR " + e.Message; }
        }

        [MenuItem("KnifeWar/멀티/2. 데디 서버 실행 (백엔드 연결, 리전 kr)", priority = 51)]
        public static void RunDedicatedWithBackend() => RunDedicated(64);

        [MenuItem("KnifeWar/멀티/2-1. 데디 서버 실행 (128틱 비교용)", priority = 51)]
        public static void RunDedicated128() => RunDedicated(128);

        static void RunDedicated(int tick)
        {
            string exe = Path.GetFullPath(GameExe);
            if (!File.Exists(exe)) { Debug.LogError("[KnifeWar] 게임 빌드가 없어요. '게임 빌드'를 먼저 실행하세요."); return; }
            string secret = ReadServerSecret();
            if (secret == null) return;
            string args = $"-batchmode -nographics -kw \"server 7777 backend http://127.0.0.1:8080 secret {secret} region kr address 127.0.0.1 tick {tick}\"";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            Debug.Log($"[KnifeWar] 데디 서버 실행 (헤드리스, 포트 7777, 리전 kr, {tick}틱) — 로그: Builds/Client/kw_log_*.txt");
        }

        [MenuItem("KnifeWar/멀티/3. 매칭 클라이언트 3개 실행 (봇 조종, 핑 100ms)", priority = 52)]
        public static void RunMatchClients()
        {
            string exe = Path.GetFullPath(GameExe);
            if (!File.Exists(exe)) { Debug.LogError("[KnifeWar] 게임 빌드가 없어요."); return; }
            for (int i = 1; i <= 3; i++)
            {
                string args = $"-kw \"match backend http://127.0.0.1:8080 name 테스터{i} id t{i} lag 50 bot\" -screen-fullscreen 0 -screen-width 640 -screen-height 360";
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            }
            Debug.Log("[KnifeWar] 매칭 클라이언트 3개 실행 (백엔드 설정상 20초 기다리면 봇으로 채워 시작)");
        }

        // ---------------- 직접 접속 테스트 ----------------

        [MenuItem("KnifeWar/멀티/테스트 클라이언트 실행 (직접 접속, 핑 100ms)", priority = 70)]
        public static void RunClient100() => RunDirectClient(50);

        [MenuItem("KnifeWar/멀티/테스트 클라이언트 3개 실행 (직접 접속, 핑 100ms)", priority = 71)]
        public static void RunClients3()
        {
            for (int i = 0; i < 3; i++) RunDirectClient(50);
        }

        static void RunDirectClient(int oneWayLagMs)
        {
            string exe = Path.GetFullPath(GameExe);
            if (!File.Exists(exe)) { Debug.LogError("[KnifeWar] 게임 빌드가 없어요."); return; }
            string args = $"-kw \"connect 127.0.0.1 lag {oneWayLagMs} bot\" -screen-fullscreen 0 -screen-width 640 -screen-height 360";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            Debug.Log($"[KnifeWar] 테스트 클라이언트 실행 (왕복 핑 +{oneWayLagMs * 2}ms)");
        }

        static string ReadServerSecret()
        {
            string cfg = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(BackendDll)) ?? "", "appsettings.json");
            if (!File.Exists(cfg)) { Debug.LogError($"[KnifeWar] 백엔드 설정이 없어요: {cfg}"); return null; }
            var m = Regex.Match(File.ReadAllText(cfg), "\"ServerSecret\"\\s*:\\s*\"([^\"]+)\"");
            if (!m.Success) { Debug.LogError("[KnifeWar] appsettings.json에서 ServerSecret을 못 찾았어요."); return null; }
            return m.Groups[1].Value;
        }
    }
}
