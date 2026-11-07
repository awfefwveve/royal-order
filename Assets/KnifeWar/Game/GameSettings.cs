using System;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>키 바꾸기 대상.</summary>
    public enum Bind
    {
        Forward, Back, Left, Right, Jump, Crouch, Walk,
        Slash, Stab, Slot1, Slot2, Slot3, Slot4, Scoreboard,
    }

    /// <summary>
    /// 플레이어 설정 (PlayerPrefs 저장). 조작·화면·소리·언어.
    /// 처음 켜면 Load(), 바꾸면 Apply() + Save(). 서버(헤드리스)에서는 화면·소리를 건드리지 않는다.
    /// </summary>
    public static class GameSettings
    {
        // 조작
        public static float Sensitivity = 2.0f;
        public static bool InvertY;
        // 화면
        public static float Fov = 90f;
        public static int Display;            // 0 전체화면(창 없음) 1 창 모드 2 전용 전체화면
        public static int ResolutionIndex = -1;  // -1 = 모니터 기본
        public static bool VSync;
        public static int FpsCap = 240;      // 0 = 제한 없음
        public static bool ShowFps;
        // 소리 (0~1)
        public static float Master = 0.8f, Sfx = 1f, Music = 0.5f;

        public static event Action Changed;

        static readonly KeyCode[] Defaults =
        {
            KeyCode.W, KeyCode.S, KeyCode.A, KeyCode.D, KeyCode.Space, KeyCode.LeftControl, KeyCode.LeftShift,
            KeyCode.Mouse0, KeyCode.Mouse1, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Tab,
        };
        static readonly KeyCode[] Keys = (KeyCode[])Defaults.Clone();
        public static readonly int[] FpsOptions = { 60, 120, 144, 165, 240, 0 };

        public static KeyCode Key(Bind b) => Keys[(int)b];
        public static bool Held(Bind b) => Input.GetKey(Keys[(int)b]);
        public static bool Down(Bind b) => Input.GetKeyDown(Keys[(int)b]);

        public static void SetKey(Bind b, KeyCode k)
        {
            // 이미 다른 동작에 쓰던 키면 서로 바꾼다
            for (int i = 0; i < Keys.Length; i++)
                if (i != (int)b && Keys[i] == k) Keys[i] = Keys[(int)b];
            Keys[(int)b] = k;
        }

        public static void ResetKeys() => Array.Copy(Defaults, Keys, Keys.Length);

        static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            Sensitivity = PlayerPrefs.GetFloat("kw.sensitivity", Sensitivity);
            InvertY = PlayerPrefs.GetInt("kw.invertY", 0) == 1;
            Fov = PlayerPrefs.GetFloat("kw.fov", Fov);
            Display = PlayerPrefs.GetInt("kw.display", Display);
            ResolutionIndex = PlayerPrefs.GetInt("kw.res", ResolutionIndex);
            VSync = PlayerPrefs.GetInt("kw.vsync", 0) == 1;
            FpsCap = PlayerPrefs.GetInt("kw.fps", FpsCap);
            ShowFps = PlayerPrefs.GetInt("kw.showfps", 0) == 1;
            Master = PlayerPrefs.GetFloat("kw.vol.master", Master);
            Sfx = PlayerPrefs.GetFloat("kw.vol.sfx", Sfx);
            Music = PlayerPrefs.GetFloat("kw.vol.music", Music);
            for (int i = 0; i < Keys.Length; i++)
                Keys[i] = (KeyCode)PlayerPrefs.GetInt("kw.key." + (Bind)i, (int)Defaults[i]);
            Apply(screen: true);
        }

        public static void Save()
        {
            PlayerPrefs.SetFloat("kw.sensitivity", Sensitivity);
            PlayerPrefs.SetInt("kw.invertY", InvertY ? 1 : 0);
            PlayerPrefs.SetFloat("kw.fov", Fov);
            PlayerPrefs.SetInt("kw.display", Display);
            PlayerPrefs.SetInt("kw.res", ResolutionIndex);
            PlayerPrefs.SetInt("kw.vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("kw.fps", FpsCap);
            PlayerPrefs.SetInt("kw.showfps", ShowFps ? 1 : 0);
            PlayerPrefs.SetFloat("kw.vol.master", Master);
            PlayerPrefs.SetFloat("kw.vol.sfx", Sfx);
            PlayerPrefs.SetFloat("kw.vol.music", Music);
            for (int i = 0; i < Keys.Length; i++) PlayerPrefs.SetInt("kw.key." + (Bind)i, (int)Keys[i]);
            PlayerPrefs.Save();
        }

        static bool Headless => Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>바뀐 값 반영. screen=true일 때만 해상도·창 모드를 바꾼다 (화면이 깜빡이므로).</summary>
        public static void Apply(bool screen)
        {
            if (Headless) return;
            AudioListener.volume = Master;
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            Application.targetFrameRate = VSync || FpsCap <= 0 ? -1 : FpsCap;
            if (screen)
            {
                var mode = Display == 1 ? FullScreenMode.Windowed : Display == 2 ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.FullScreenWindow;
                var res = Screen.resolutions;
                if (ResolutionIndex >= 0 && ResolutionIndex < res.Length)
                    Screen.SetResolution(res[ResolutionIndex].width, res[ResolutionIndex].height, mode, res[ResolutionIndex].refreshRateRatio);
                else
                    Screen.fullScreenMode = mode;
            }
            Changed?.Invoke();
        }

        public static string KeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return L.T("key_lmb");
                case KeyCode.Mouse1: return L.T("key_rmb");
                case KeyCode.Mouse2: return L.T("key_mmb");
                case KeyCode.LeftControl: return "L-Ctrl";
                case KeyCode.LeftShift: return "L-Shift";
                case KeyCode.LeftAlt: return "L-Alt";
                default:
                    var s = k.ToString();
                    return s.StartsWith("Alpha") ? s.Substring(5) : s;
            }
        }
    }
}
