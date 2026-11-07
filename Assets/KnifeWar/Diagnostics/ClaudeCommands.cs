using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KnifeWar.Diagnostics
{
    /// <summary>
    /// 원격 작업용 명령 파일. 유니티가 켜져 있으면 2초마다 Logs/claude_cmd.txt 를 확인해 한 줄씩 실행하고 지운다.
    /// 결과는 Logs/claude_console.txt 에 남는다. (화면을 직접 조작하지 않고 새로고침·빌드를 하기 위함)
    ///   refresh        — 바뀐 코드 다시 불러오기 (컴파일)
    ///   build_windows  — Builds/Client/KnifeWar.exe
    ///   build_linux    — Builds/LinuxServer/KnifeWarServer.x86_64 (헤드리스 데디 서버)
    ///   release        — 버전 +1 빌드 → 패치 → R2 업로드 → 설치 파일 (확인 창 없이)
    ///   showcase       — 게임 화면을 크게 하고 플레이해서 포트폴리오용 스크린샷을 Logs/showcase/ 에 자동 저장
    ///   nettest        — Windows 빌드 → 데디 서버(2선승) + 봇 클라 3개(핑 100ms)를 창 없이 띄워 한 경기 자동 진행
    ///   nettest_report — 그 로그들을 모아 Logs/claude_nettest.txt 로 (오류·접속·라운드·킬·경기 끝), 프로세스 정리
    /// 게임 코드에 의존하지 않아서 게임 코드가 컴파일 실패해도 동작한다.
    /// </summary>
    [InitializeOnLoad]
    static class ClaudeCommands
    {
        static readonly string CmdPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs", "claude_cmd.txt"));
        const string ScenePath = "Assets/KnifeWar/Scenes/Game.unity";
        static double _next;

        static ClaudeCommands()
        {
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool("kw.showcase.max", false)) return;
            SessionState.SetBool("kw.showcase.max", false);
            var gv = GameView(false);
            if (gv != null) gv.maximized = false;
        }

        static EditorWindow GameView(bool create)
        {
            var t = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (t == null) return null;
            var all = Resources.FindObjectsOfTypeAll(t);
            if (all.Length > 0) return (EditorWindow)all[0];
            return create ? EditorWindow.GetWindow(t) : null;
        }

        static void Showcase()
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[claude-cmd] 게임 씬이 없음"); return; }
            if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path != ScenePath)
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene(ScenePath);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(CmdPath), "claude_showcase.txt"), "1");
            var gv = GameView(true);
            if (gv != null) { gv.Focus(); gv.maximized = true; SessionState.SetBool("kw.showcase.max", true); }
            EditorApplication.isPlaying = true;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + 2.0;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists(CmdPath)) return;

            string[] lines;
            try
            {
                // 원격에서 파일을 만들고 내용을 쓰는 사이에 읽으면 빈 파일 → 내용이 들어오고 2초 지난 뒤에만 읽는다
                var fi = new FileInfo(CmdPath);
                if (fi.Length == 0 || (DateTime.Now - fi.LastWriteTime).TotalSeconds < 2.0) return;
                lines = File.ReadAllLines(CmdPath);
                File.Delete(CmdPath);
            }
            catch { return; }

            foreach (var raw in lines)
            {
                string cmd = raw.Trim().ToLowerInvariant();
                if (cmd.Length == 0) continue;
                Debug.Log($"[claude-cmd] {cmd}");
                try
                {
                    if (cmd.StartsWith("vatimport "))   // 구운 애니메이션: vatimport 종류 파일 [windup=a,b] ...
                    {
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(CmdPath), "claude_vatimport.txt"), raw.Trim().Substring(10).Trim());
                        if (!EditorApplication.ExecuteMenuItem("KnifeWar/아트/구운 애니메이션 가져오기 (Logs/claude_vatimport.txt)")) Debug.LogError("[claude-cmd] VAT 메뉴를 못 찾음");
                        continue;
                    }
                    if (cmd.StartsWith("videogen "))   // 동작 영상: videogen 이름 동작 [model=max|h3|seedance]
                    {
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(CmdPath), "claude_videogen.txt"), raw.Trim().Substring(9).Trim());
                        if (!EditorApplication.ExecuteMenuItem("KnifeWar/아트/동작 영상 생성 (Logs/claude_videogen.txt)")) Debug.LogError("[claude-cmd] 영상 생성 메뉴를 못 찾음");
                        continue;
                    }
                    if (cmd.StartsWith("artgen "))   // 아트 생성 파이프라인: artgen monsters | all | 이름 [force] [step=…] | yaw 이름 각도
                    {
                        File.WriteAllText(Path.Combine(Path.GetDirectoryName(CmdPath), "claude_artgen.txt"), cmd.Substring(7).Trim());
                        if (!EditorApplication.ExecuteMenuItem("KnifeWar/아트/생성 (명령 파일 Logs/claude_artgen.txt)")) Debug.LogError("[claude-cmd] 아트 생성 메뉴를 못 찾음");
                        continue;
                    }
                    switch (cmd)
                    {
                        case "sysinfo":
                            Debug.Log($"[claude-cmd] GPU {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize}MB, {SystemInfo.graphicsDeviceType}) / CPU {SystemInfo.processorType} x{SystemInfo.processorCount} / RAM {SystemInfo.systemMemorySize}MB / OS {SystemInfo.operatingSystem}");
                            break;
                        case "refresh":
                            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                            break;
                        case "build_windows":
                            Build(BuildTarget.StandaloneWindows64, "Builds/Client/KnifeWar.exe", false);
                            break;
                        case "showcase_frontend":
                            File.WriteAllText(Path.Combine(Path.GetDirectoryName(CmdPath), "claude_showcase_frontend.txt"), "1");
                            Showcase();
                            return;
                        case "player_target":
                            PlayerTarget();
                            break;
                        case "release":
                            if (!EditorApplication.ExecuteMenuItem("KnifeWar/배포/(자동) 확인 없이 한 번에")) Debug.LogError("[claude-cmd] 배포 메뉴를 못 찾음");
                            break;
                        case "showcase":
                            Showcase();
                            return;   // 플레이 모드로 들어가므로 남은 명령은 다음에
                        case "build_linux":
                            Build(BuildTarget.StandaloneLinux64, "Builds/LinuxServer/KnifeWarServer.x86_64", true);
                            break;
                        case "rebuild_scene":   // 게임 씬을 코드 기준(다리 폭 등)으로 다시 만들기
                            if (!EditorApplication.ExecuteMenuItem("KnifeWar/게임 씬 만들기 (외나무다리 6:6)")) Debug.LogError("[claude-cmd] 씬 만들기 메뉴를 못 찾음");
                            break;
                        case "server_bundle":   // 리눅스 서버 빌드 → 묶어서 패치 서버에 (Vultr 테스트 서버가 받음)
                            Build(BuildTarget.StandaloneLinux64, "Builds/LinuxServer/KnifeWarServer.x86_64", true);
                            PlayerTarget();
                            if (!EditorApplication.ExecuteMenuItem("KnifeWar/배포/테스트 서버 묶음 올리기")) Debug.LogError("[claude-cmd] 서버 묶음 메뉴를 못 찾음");
                            break;
                        case "nettest":
                            NetTestStart(build: true);
                            break;
                        case "nettest_nobuild":
                            NetTestStart(build: false);
                            break;
                        case "nettest_backend":   // 백엔드(테스트 설정, 18080) + 데디 서버(백엔드 연결) + 매칭 클라 3 → 결과 보고·MMR 까지
                            NetTestStart(build: false, backend: true);
                            break;
                        case "nettest_report":
                            NetTestReport();
                            break;
                        default:
                            Debug.LogWarning($"[claude-cmd] 모르는 명령: {cmd}");
                            break;
                    }
                }
                catch (Exception e) { Debug.LogError($"[claude-cmd] {cmd} 실패: {e.Message}"); }
            }
        }


        static void Build(BuildTarget target, string path, bool server)
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[claude-cmd] 게임 씬이 없음"); return; }
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            };
            if (server) options.subtarget = (int)StandaloneBuildSubtarget.Server;
            PlayerSettings.SplashScreen.show = false;   // 유니티 로고 화면 끄기
            PlayerSettings.SplashScreen.showUnityLogo = false;
            var report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            if (s.result == BuildResult.Succeeded)
                Debug.Log($"[claude-cmd] 빌드 성공 {path} ({s.totalSize / (1024 * 1024)}MB, {s.totalTime.TotalSeconds:0}s)");
            else
                Debug.LogError($"[claude-cmd] 빌드 실패 {path}: {s.result}, 오류 {s.totalErrors}개");
            if (server) PlayerTarget();   // 서버 빌드 뒤 에디터가 서버 모드로 남지 않게
        }

        // ---------------- 자동 네트워크 테스트 ----------------

        static string ClientDir => Path.GetFullPath("Builds/Client");
        static string ClientExe => Path.Combine(ClientDir, "KnifeWar.exe");
        static string NetTestPath => Path.Combine(Path.GetDirectoryName(CmdPath), "claude_nettest.txt");

        const string TestBackendUrl = "http://127.0.0.1:18080";
        const string TestServerSecret = "nettest-server-secret-only-for-local-test-0000";
        static string TestBackendDir => Path.GetFullPath("Backend/test");
        static string BackendBuildDll => Path.GetFullPath("Backend/build/KnifeWar.Backend.dll");
        static int _backendPid = -1;

        /// <summary>
        /// 데디 서버 1 + 봇 클라 3 (전부 창 없이). 경기는 2선승이라 길어야 5~6분. 끝나면 nettest_report.
        /// backend=true: 테스트용 백엔드(18080, 파일 저장, 15초 뒤 봇 채움)를 먼저 띄우고, 서버는 백엔드에 등록, 클라는 매칭으로 들어간다.
        /// </summary>
        static void NetTestStart(bool build, bool backend = false)
        {
            NetTestKill();
            if (build) Build(BuildTarget.StandaloneWindows64, "Builds/Client/KnifeWar.exe", false);
            if (!File.Exists(ClientExe)) { Debug.LogError("[claude-cmd] 게임 빌드가 없음: " + ClientExe); return; }
            foreach (var f in Directory.GetFiles(ClientDir, "kw_log_*.txt")) { try { File.Delete(f); } catch { } }

            var pids = new System.Collections.Generic.List<int>();
            int Start(string kw)
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ClientExe,
                    $"-batchmode -nographics -kw \"{kw}\"") { UseShellExecute = true, WorkingDirectory = ClientDir });
                return p?.Id ?? -1;
            }
            if (backend)
            {
                if (!File.Exists(BackendBuildDll)) { Debug.LogError("[claude-cmd] 백엔드 빌드가 없음: " + BackendBuildDll); return; }
                if (!File.Exists(Path.Combine(TestBackendDir, "appsettings.json"))) { Debug.LogError("[claude-cmd] 테스트 백엔드 설정이 없음: Backend/test/appsettings.json"); return; }
                try { Directory.Delete(Path.Combine(TestBackendDir, "data"), true); } catch { }   // 매번 깨끗한 저장소
                var bp = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", $"\"{BackendBuildDll}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = TestBackendDir,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                _backendPid = bp?.Id ?? -1;
                File.WriteAllText(BackendPidPath, _backendPid.ToString());
                var blog = Path.Combine(TestBackendDir, "backend_log.txt");
                File.WriteAllText(blog, "");
                if (bp != null)
                {
                    bp.OutputDataReceived += (_, e) => { if (e.Data != null) lock (blog) File.AppendAllText(blog, e.Data + "\n"); };
                    bp.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (blog) File.AppendAllText(blog, "ERR " + e.Data + "\n"); };
                    bp.BeginOutputReadLine(); bp.BeginErrorReadLine();
                }
                System.Threading.Thread.Sleep(4000);
                pids.Add(Start($"server 7777 backend {TestBackendUrl} secret {TestServerSecret} region kr address 127.0.0.1 rounds 2"));
                System.Threading.Thread.Sleep(3000);
                for (int i = 1; i <= 3; i++) pids.Add(Start($"match backend {TestBackendUrl} name Tester{i} id nettest{i} lag 50 bot"));
            }
            else
            {
                pids.Add(Start("server 7777 rounds 2"));
                System.Threading.Thread.Sleep(2500);
                for (int i = 0; i < 3; i++) pids.Add(Start("connect 127.0.0.1 7777 lag 50 bot"));
            }
            File.WriteAllText(NetTestPath, $"started {DateTime.Now:HH:mm:ss} backend={backend}\npids {string.Join(",", pids)} backendPid={_backendPid}\n");
            Debug.Log($"[claude-cmd] nettest 시작 (backend={backend}): 서버 + 봇 클라 3 (pids {string.Join(",", pids)}). 5분쯤 뒤 nettest_report");
        }

        static string BackendPidPath => Path.Combine(TestBackendDir, "backend.pid");

        static void ReadBackendPid()
        {
            if (_backendPid > 0 || !File.Exists(BackendPidPath)) return;
            int.TryParse(File.ReadAllText(BackendPidPath).Trim(), out _backendPid);
        }

        static void NetTestKill()
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("KnifeWar"))
                {
                    try { p.Kill(); } catch { }
                }
                ReadBackendPid();
                if (_backendPid > 0)
                {
                    try { var bp = System.Diagnostics.Process.GetProcessById(_backendPid); if (bp.ProcessName.StartsWith("dotnet")) bp.Kill(); } catch { }
                    _backendPid = -1;
                    try { File.Delete(BackendPidPath); } catch { }
                }
            }
            catch { }
        }

        static string HttpGet(string url, string header = null, string value = null)
        {
            try
            {
                using var c = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                if (header != null) c.DefaultRequestHeaders.Add(header, value);
                return c.GetStringAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception e) { return "ERR " + e.Message; }
        }

        /// <summary>로그 요약: 오류·예외·잘못된 패킷은 전부, 접속·라운드·킬·경기 끝은 표시. 그리고 프로세스 정리.</summary>
        static void NetTestReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"report {DateTime.Now:HH:mm:ss}");
            string[] keys = { "launch:", "서버 시작", "접속 완료", "현재 상태 수신", "round ", "live", "RoundEnd", "MatchEnd", "kill ", "remote player", "입장 거부", "버전 불일치", "잘못된 패킷", "Exception", "[Error]", "[Exception]", "[Warning]", "(client) ping", "접속 종료", "연결 끊김", "corrections=" };
            foreach (var f in Directory.GetFiles(ClientDir, "kw_log_*.txt"))
            {
                var lines = File.ReadAllLines(f);
                sb.AppendLine($"==== {Path.GetFileName(f)} ({lines.Length} lines)");
                int shown = 0;
                foreach (var line in lines)
                {
                    bool hit = false;
                    foreach (var k in keys) if (line.Contains(k)) { hit = true; break; }
                    if (!hit) continue;
                    if (line.Contains("[Warning]") && line.Contains("Input Manager")) continue;
                    sb.AppendLine(line.Length > 300 ? line.Substring(0, 300) : line);
                    if (++shown > 400) { sb.AppendLine("... (more)"); break; }
                }
            }
            // 백엔드 흐름이면: 리더보드(경기 수·MMR 반영)와 서버 목록, 백엔드 로그 꼬리 (pid 는 도메인 리로드로 잊을 수 있어 파일에서 다시 읽는다)
            ReadBackendPid();
            if (_backendPid > 0)
            {
                sb.AppendLine("==== backend /leaderboard/kr");
                sb.AppendLine(HttpGet(TestBackendUrl + "/leaderboard/kr"));
                sb.AppendLine("==== backend /admin/servers");
                sb.AppendLine(HttpGet(TestBackendUrl + "/admin/servers", "X-Server-Secret", TestServerSecret));
                var blog = Path.Combine(TestBackendDir, "backend_log.txt");
                if (File.Exists(blog))
                {
                    var bl = File.ReadAllLines(blog);
                    sb.AppendLine($"==== backend_log.txt ({bl.Length} lines, last 60)");
                    for (int i = Math.Max(0, bl.Length - 60); i < bl.Length; i++) sb.AppendLine(bl[i]);
                }
            }
            NetTestKill();
            File.WriteAllText(NetTestPath, sb.ToString());
            Debug.Log($"[claude-cmd] nettest 보고서: {NetTestPath} ({sb.Length} chars), 프로세스 정리됨");
        }

        /// <summary>에디터 빌드 대상을 Windows 일반 게임으로 되돌림.</summary>
        static void PlayerTarget()
        {
            EditorUserBuildSettings.standaloneBuildSubtarget = StandaloneBuildSubtarget.Player;
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
            Debug.Log("[claude-cmd] 빌드 대상: Windows 게임");
        }
    }
}
