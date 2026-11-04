using System;
using System.Threading;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 홈페이지 PLAY NOW 연동 (Windows 일반 게임 빌드만):
    /// - royalorder:// 주소를 이 게임 실행 파일로 연결 (HKCU, 관리자 권한 불필요). 설치 파일도 등록하지만,
    ///   zip으로 받았거나 폴더를 옮겨도 게임을 한 번 켜면 현재 위치로 다시 등록된다.
    /// - 한 번에 하나만 실행: 이미 켜져 있으면 새로 켜진 쪽은 바로 꺼진다 (버튼을 여러 번 눌러도 안전).
    ///   설치 파일도 같은 이름(ROYALORDER_RUNNING)으로 게임이 켜져 있는지 확인한다.
    /// 웹에서 들어온 실행(royalorder:...)은 -kw 같은 실행 옵션을 전부 무시한다 (주소로 옵션을 넣는 공격 방지).
    /// </summary>
    public static class AppInstance
    {
        public const string Scheme = "royalorder";
        public const string MutexName = "ROYALORDER_RUNNING";
        static Mutex _mutex;

        /// <summary>웹 주소로 실행됐는지 (그러면 실행 옵션을 믿지 않는다).</summary>
        public static bool FromWeb { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (a.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) FromWeb = true;

            if (Application.isEditor || Application.platform != RuntimePlatform.WindowsPlayer || Application.isBatchMode) return;
            bool testLaunch = !FromWeb && Array.IndexOf(Environment.GetCommandLineArgs(), "-kw") >= 0;
            if (testLaunch) return;   // 여러 클라 띄우는 시험은 막지 않음

            // ASTREL 런처가 깔려 있는데 런처 밖에서 켰으면(바탕화면 아이콘·옛 주소) 런처로 넘김
            // → 런처가 패치 확인 + ASTREL 계정으로 로그인시켜 다시 켠다 (라이엇·배틀넷과 같음)
            bool fromLauncher = Array.IndexOf(Environment.GetCommandLineArgs(), "-launcher") >= 0;
            if (!fromLauncher && Reg(@"query HKCU\Software\Classes\astrel\shell\open\command /ve"))
            {
                Debug.Log("[KnifeWar] 런처로 실행 → ASTREL 런처로 넘김");
                Application.OpenURL("astrel://play/royalorder");
                Application.Quit();
                return;
            }

            try
            {
                _mutex = new Mutex(true, MutexName, out bool created);
                if (!created)
                {
                    Debug.Log("[KnifeWar] 이미 실행 중 → 이 창은 닫음");
                    Application.Quit();
                    return;
                }
            }
            catch (Exception e) { Debug.LogWarning("[KnifeWar] 실행 중 확인 실패: " + e.Message); }

            RegisterScheme();
            Application.quitting += () => { try { _mutex?.ReleaseMutex(); } catch { } };
        }

        /// <summary>royalorder:// → 지금 실행 파일. 마지막으로 등록한 경로와 같으면 건너뜀.
        /// (.NET Standard 2.1 에는 레지스트리 API가 없어서 윈도우 기본 reg.exe 사용)</summary>
        static void RegisterScheme()
        {
            try
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string mark = System.IO.Path.Combine(Application.persistentDataPath, "scheme.txt");
                if (System.IO.File.Exists(mark) && System.IO.File.ReadAllText(mark) == exe) return;
                string key = @"HKCU\Software\Classes\" + Scheme;
                bool ok = Reg($"add \"{key}\" /ve /d \"URL:ROYAL ORDER\" /f")
                       && Reg($"add \"{key}\" /v \"URL Protocol\" /d \"\" /f")
                       && Reg($"add \"{key}\\DefaultIcon\" /ve /d \"\\\"{exe}\\\",0\" /f")
                       && Reg($"add \"{key}\\shell\\open\\command\" /ve /d \"\\\"{exe}\\\" \\\"%1\\\"\" /f");
                if (ok) System.IO.File.WriteAllText(mark, exe);
            }
            catch (Exception e) { Debug.LogWarning("[KnifeWar] royalorder:// 등록 실패: " + e.Message); }
        }

        static bool Reg(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("reg.exe", args) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit(5000);
                return p.HasExited && p.ExitCode == 0;
            }
        }
    }
}
