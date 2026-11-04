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
            try { lines = File.ReadAllLines(CmdPath); File.Delete(CmdPath); }
            catch { return; }

            foreach (var raw in lines)
            {
                string cmd = raw.Trim().ToLowerInvariant();
                if (cmd.Length == 0) continue;
                Debug.Log($"[claude-cmd] {cmd}");
                try
                {
                    switch (cmd)
                    {
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
