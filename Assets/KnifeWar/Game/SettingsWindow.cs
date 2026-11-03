using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 설정 창 (IMGUI). 시작 메뉴와 경기 중 Esc 메뉴에서 같이 쓴다.
    /// 탭: 조작 / 화면 / 소리. 키 바꾸기는 칸을 누른 뒤 원하는 키(마우스 버튼 포함)를 누르면 된다. Esc = 취소.
    /// </summary>
    public static class SettingsWindow
    {
        static int _tab;
        public static int Tab { get => _tab; set => _tab = value; }
        static int _waiting = -1;    // 키 입력 기다리는 Bind
        static bool _screenDirty;
        static GUIStyle _h, _t, _s;

        /// <summary>그린다. 닫기를 누르면 false.</summary>
        public static bool Draw(float w, float h)
        {
            GameSettings.Load();
            if (_h == null)
            {
                _h = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _t = new GUIStyle(GUI.skin.label) { fontSize = 17 };
                _s = new GUIStyle(GUI.skin.label) { fontSize = 14 };
                _h.normal.textColor = _t.normal.textColor = Color.white;
                _s.normal.textColor = new Color(1, 1, 1, 0.65f);
            }

            float pw = Mathf.Min(620f, w - 40f), ph = Mathf.Min(560f, h - 40f);
            float x = (w - pw) * 0.5f, y = (h - ph) * 0.5f;
            GUI.color = new Color(0, 0, 0, 0.55f); GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = new Color(0.08f, 0.09f, 0.12f, 0.97f); GUI.DrawTexture(new Rect(x, y, pw, ph), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(x, y + 10, pw, 40), L.T("settings"), _h);

            string[] tabs = { L.T("set_controls"), L.T("set_video"), L.T("set_audio") };
            for (int i = 0; i < tabs.Length; i++)
                if (GUI.Toggle(new Rect(x + 30 + i * ((pw - 60) / 3f), y + 58, (pw - 60) / 3f - 6, 34), _tab == i, tabs[i], GUI.skin.button) && _tab != i)
                { _tab = i; _waiting = -1; }

            var area = new Rect(x + 30, y + 108, pw - 60, ph - 180);
            bool changed = false;
            if (_tab == 0) changed = Controls(area);
            else if (_tab == 1) changed = Video(area);
            else changed = Audio(area);
            if (changed) GameSettings.Apply(false);

            bool open = true;
            float by = y + ph - 58;
            if (_tab == 1 && _screenDirty && GUI.Button(new Rect(x + 30, by, 170, 40), L.T("set_apply_screen")))
            {
                GameSettings.Apply(true);
                _screenDirty = false;
            }
            if (_tab == 0 && GUI.Button(new Rect(x + 30, by, 170, 40), L.T("set_reset_keys"))) { GameSettings.ResetKeys(); _waiting = -1; }
            if (GUI.Button(new Rect(x + pw - 200, by, 170, 40), L.T("close")))
            {
                if (_screenDirty) GameSettings.Apply(true);
                _screenDirty = false;
                _waiting = -1;
                GameSettings.Save();
                open = false;
            }
            return open;
        }

        /// <summary>키 바꾸기 대기 중이면 true (Esc 메뉴가 Esc로 닫히지 않게).</summary>
        public static bool Capturing => _waiting >= 0;

        static bool Controls(Rect a)
        {
            bool c = false;
            if (_waiting >= 0)
            {
                // 그리기 전에 먼저 받아야 다른 칸 버튼이 클릭을 가져가지 않는다
                var e = Event.current;
                KeyCode k = KeyCode.None;
                if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None) k = e.keyCode;
                else if (e.type == EventType.MouseDown && e.button <= 4) k = KeyCode.Mouse0 + e.button;
                if (k == KeyCode.Escape) { _waiting = -1; e.Use(); }
                else if (k != KeyCode.None)
                {
                    GameSettings.SetKey((Bind)_waiting, k);
                    _waiting = -1;
                    e.Use();
                    c = true;
                }
            }

            float y = a.y;
            c |= Slider(a.x, ref y, a.width, L.T("sensitivity", GameSettings.Sensitivity), ref GameSettings.Sensitivity, 0.1f, 8f);
            bool inv = GUI.Toggle(new Rect(a.x, y, a.width, 24), GameSettings.InvertY, " " + L.T("set_invert_y"));
            if (inv != GameSettings.InvertY) { GameSettings.InvertY = inv; c = true; }
            y += 34;

            // 키 바꾸기: 두 줄로 나눠 표시
            int n = System.Enum.GetValues(typeof(Bind)).Length, half = (n + 1) / 2;
            float colW = (a.width - 20) / 2f, rowH = 30;
            for (int i = 0; i < n; i++)
            {
                float cx = a.x + (i < half ? 0 : colW + 20), cy = y + (i % half) * rowH;
                GUI.Label(new Rect(cx, cy + 3, colW - 120, 24), L.T("bind_" + ((Bind)i).ToString().ToLowerInvariant()), _t);
                string label = _waiting == i ? L.T("set_press_key") : GameSettings.KeyName(GameSettings.Key((Bind)i));
                if (GUI.Button(new Rect(cx + colW - 116, cy, 116, rowH - 4), label)) _waiting = i;
            }

            return c;
        }

        static bool Video(Rect a)
        {
            bool c = false;
            float y = a.y;
            c |= Slider(a.x, ref y, a.width, L.T("set_fov", GameSettings.Fov), ref GameSettings.Fov, 70f, 110f, 1f);

            GUI.Label(new Rect(a.x, y, 140, 26), L.T("set_display"), _t);
            string[] modes = { L.T("set_borderless"), L.T("set_windowed"), L.T("set_fullscreen") };
            for (int i = 0; i < modes.Length; i++)
                if (GUI.Toggle(new Rect(a.x + 150 + i * ((a.width - 150) / 3f), y, (a.width - 150) / 3f - 4, 28), GameSettings.Display == i, modes[i], GUI.skin.button) && GameSettings.Display != i)
                { GameSettings.Display = i; _screenDirty = true; }
            y += 40;

            var res = Screen.resolutions;
            GUI.Label(new Rect(a.x, y, 140, 26), L.T("set_resolution"), _t);
            int idx = GameSettings.ResolutionIndex;
            string cur = idx >= 0 && idx < res.Length ? $"{res[idx].width} x {res[idx].height} @ {res[idx].refreshRateRatio.value:0}Hz" : L.T("set_native");
            if (GUI.Button(new Rect(a.x + 150, y, 36, 28), "<")) { GameSettings.ResolutionIndex = Mathf.Max(-1, idx - 1); _screenDirty = true; }
            GUI.Label(new Rect(a.x + 196, y + 2, a.width - 250, 26), cur, _t);
            if (GUI.Button(new Rect(a.x + a.width - 36, y, 36, 28), ">")) { GameSettings.ResolutionIndex = Mathf.Min(res.Length - 1, idx + 1); _screenDirty = true; }
            y += 40;

            bool vs = GUI.Toggle(new Rect(a.x, y, a.width, 24), GameSettings.VSync, " " + L.T("set_vsync"));
            if (vs != GameSettings.VSync) { GameSettings.VSync = vs; c = true; }
            y += 34;

            GUI.Label(new Rect(a.x, y, 140, 26), L.T("set_fps_cap"), _t);
            var opts = GameSettings.FpsOptions;
            for (int i = 0; i < opts.Length; i++)
            {
                string t = opts[i] == 0 ? L.T("set_unlimited") : opts[i].ToString();
                if (GUI.Toggle(new Rect(a.x + 150 + i * ((a.width - 150) / opts.Length), y, (a.width - 150) / opts.Length - 4, 28), GameSettings.FpsCap == opts[i], t, GUI.skin.button) && GameSettings.FpsCap != opts[i])
                { GameSettings.FpsCap = opts[i]; c = true; }
            }
            y += 40;
            bool sf = GUI.Toggle(new Rect(a.x, y, a.width, 24), GameSettings.ShowFps, " " + L.T("set_show_fps"));
            if (sf != GameSettings.ShowFps) { GameSettings.ShowFps = sf; c = true; }
            y += 34;

            GUI.Label(new Rect(a.x, y, 140, 26), L.T("language"), _t);
            for (int i = 0; i < L.LangNames.Length; i++)
                if (GUI.Toggle(new Rect(a.x + 150 + i * 80, y, 76, 28), (int)L.Current == i, L.LangNames[i], GUI.skin.button) && (int)L.Current != i)
                    L.Set((Lang)i);
            return c;
        }

        static bool Audio(Rect a)
        {
            bool c = false;
            float y = a.y;
            c |= Slider(a.x, ref y, a.width, L.T("set_vol_master", Mathf.RoundToInt(GameSettings.Master * 100)), ref GameSettings.Master, 0f, 1f);
            c |= Slider(a.x, ref y, a.width, L.T("set_vol_sfx", Mathf.RoundToInt(GameSettings.Sfx * 100)), ref GameSettings.Sfx, 0f, 1f);
            c |= Slider(a.x, ref y, a.width, L.T("set_vol_music", Mathf.RoundToInt(GameSettings.Music * 100)), ref GameSettings.Music, 0f, 1f);
            return c;
        }

        static bool Slider(float x, ref float y, float width, string label, ref float v, float min, float max, float step = 0f)
        {
            GUI.Label(new Rect(x, y, width, 26), label, _t);
            float nv = GUI.HorizontalSlider(new Rect(x, y + 30, width, 20), v, min, max);
            if (step > 0f) nv = Mathf.Round(nv / step) * step;
            y += 60;
            if (Mathf.Approximately(nv, v)) return false;
            v = nv;
            return true;
        }
    }
}
