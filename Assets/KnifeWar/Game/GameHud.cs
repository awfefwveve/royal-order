using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>프로토타입 HUD (IMGUI). 정식 UI는 아트 방향이 정해지면 UI Toolkit으로 교체.</summary>
    public sealed class GameHud : MonoBehaviour
    {
        static readonly Color Blue = new Color(0.35f, 0.6f, 1f);
        static readonly Color Red = new Color(1f, 0.38f, 0.32f);

        MatchController _m;
        GUIStyle _big, _mid, _small, _center, _panel;
        Texture2D _white;

        bool _menu, _settings;
        float _fpsT, _fps;

        public void Init(MatchController m) => _m = m;

        void Update()
        {
            if (_m == null) return;
            if (Input.GetKeyDown(KeyCode.Escape) && !SettingsWindow.Capturing)
            {
                if (_settings) { _settings = false; GameSettings.Save(); }
                else SetMenu(!_menu);
            }
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);
        }

        void SetMenu(bool open)
        {
            _menu = open;
            if (!open) _settings = false;
            _m.HumanControls.Blocked = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
        }

        void EnsureStyles()
        {
            if (_big != null) return;
            _white = Texture2D.whiteTexture;
            _big = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _mid = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _center = new GUIStyle(_mid) { alignment = TextAnchor.MiddleCenter };
            _panel = new GUIStyle(GUI.skin.box);
            _big.normal.textColor = _mid.normal.textColor = _small.normal.textColor = _center.normal.textColor = Color.white;
        }

        void OnGUI()
        {
            if (_m == null || _m.Round == null) return;
            SkinSet.ApplyFont();
            EnsureStyles();
            var r = _m.Round;
            var me = _m.Human;
            float w = Screen.width, h = Screen.height;

            // ---- 상단: 점수 / 타이머 ----
            Rect(w * 0.5f - 170, 8, 340, 64, new Color(0, 0, 0, 0.45f));
            Label(new Rect(w * 0.5f - 170, 10, 110, 40), r.Score[0].ToString(), _big, Blue);
            Label(new Rect(w * 0.5f + 60, 10, 110, 40), r.Score[1].ToString(), _big, Red);
            int secs = r.SecondsLeft(_m.ServerTick);
            Label(new Rect(w * 0.5f - 60, 8, 120, 34), $"{secs / 60}:{secs % 60:00}", _big, Color.white);
            Label(new Rect(w * 0.5f - 100, 44, 200, 24), L.T("round_info", r.RoundNumber, r.Config.RoundsToWin), _center, new Color(1, 1, 1, 0.8f));
            Label(new Rect(w * 0.5f - 300, 18, 120, 30), L.T("alive", r.AliveCount(0)), _mid, Blue);
            Label(new Rect(w * 0.5f + 200, 18, 120, 30), L.T("alive", r.AliveCount(1)), _mid, Red);
            Label(new Rect(w * 0.5f - 300, 44, 140, 24), L.T("monsters_n", _m.Monsters.AliveInTeam(0)), _small, Blue);
            Label(new Rect(w * 0.5f + 200, 44, 140, 24), L.T("monsters_n", _m.Monsters.AliveInTeam(1)), _small, Red);

            if (_m.Role == NetRole.Client && NetInfo.Unstable)
                Label(new Rect(w * 0.5f - 150, 76, 300, 26), L.T("st_unstable"), _center, new Color(1f, 0.45f, 0.35f));

            // ---- 단계별 큰 안내 ----
            switch (r.Phase)
            {
                case RoundPhase.Select: DrawSelect(r, w, h); break;
                case RoundPhase.RoundEnd:
                {
                    string t = r.LastRoundWinner < 0 ? L.T("draw") : (r.LastRoundWinner == me.Team ? L.T("round_win") : L.T("round_lose"));
                    Label(new Rect(0, h * 0.28f, w, 60), t, _big, r.LastRoundWinner < 0 ? Color.white : (r.LastRoundWinner == me.Team ? Blue : Red));
                    break;
                }
                case RoundPhase.MatchEnd:
                {
                    Rect(0, h * 0.3f - 20, w, 130, new Color(0, 0, 0, 0.6f));
                    bool win = r.MatchWinner == me.Team;
                    Label(new Rect(0, h * 0.3f, w, 60), win ? L.T("victory") : L.T("defeat"), _big, win ? Blue : Red);
                    Label(new Rect(0, h * 0.3f + 60, w, 30), L.T("press_r"), _center, Color.white);
                    break;
                }
            }

            if (r.Phase == RoundPhase.Live || r.Phase == RoundPhase.RoundEnd) DrawPlayerHud(me, w, h);
            DrawKillFeed(w);

            // 메시지
            if (Time.time < _m.LastSummonMessageUntil)
                Label(new Rect(0, h * 0.62f, w, 30), _m.LastSummonMessage, _center, new Color(1f, 0.9f, 0.5f));

            if ((!_menu && GameSettings.Held(Bind.Scoreboard)) || r.Phase == RoundPhase.MatchEnd) DrawScoreboard(w, h);
            if (GameSettings.ShowFps) Label(new Rect(w - 110, 8, 100, 22), Mathf.RoundToInt(_fps) + " FPS", _small, Color.white);

            // 커서 안내
            if (!_menu && Cursor.lockState != CursorLockMode.Locked && r.Phase != RoundPhase.MatchEnd)
                Label(new Rect(0, h * 0.5f + 60, w, 30), L.T("click_to_start"), _center, Color.white);

            if (_menu && _settings) { if (!SettingsWindow.Draw(w, h)) _settings = false; }
            else if (_menu) DrawMenu(w, h);
        }

        void DrawScoreboard(float w, float h)
        {
            float pw = 640, rowH = 26, x = w * 0.5f - pw * 0.5f, y = h * 0.5f - 200;
            if (_m.Round.Phase == RoundPhase.MatchEnd) y = h * 0.3f + 130;
            Rect(x, y, pw, rowH * 15 + 20, new Color(0, 0, 0, 0.72f));
            Label(new Rect(x + 16, y + 8, 200, rowH), L.T("sb_name"), _mid, Color.white);
            Label(new Rect(x + 230, y + 8, 120, rowH), L.T("sb_class"), _mid, Color.white);
            Label(new Rect(x + 360, y + 8, 80, rowH), L.T("sb_kills"), _mid, Color.white);
            Label(new Rect(x + 430, y + 8, 80, rowH), L.T("sb_deaths"), _mid, Color.white);
            Label(new Rect(x + 520, y + 8, 110, rowH), L.T("sb_damage"), _mid, Color.white);
            float yy = y + 8 + rowH + 6;
            for (int team = 0; team < 2; team++)
            {
                for (int p = 0; p < MatchController.MaxPlayers; p++)
                {
                    var a = _m.Avatars[p];
                    if (a.Team != team) continue;
                    var c = team == 0 ? Blue : Red;
                    if (!a.Alive) c *= 0.6f;
                    c.a = 1f;
                    if (p == _m.HumanIndex) Rect(x + 6, yy, pw - 12, rowH, new Color(1, 1, 1, 0.12f));
                    Label(new Rect(x + 16, yy, 200, rowH), a.DisplayName + (a.Alive ? "" : "  " + L.T("sb_dead")), _small, c);
                    Label(new Rect(x + 230, yy, 120, rowH), a.Class == PlayerClass.Knight ? L.T("knight") : L.T("wizard"), _small, c);
                    Label(new Rect(x + 360, yy, 80, rowH), _m.Kills[p].ToString(), _small, c);
                    Label(new Rect(x + 430, yy, 80, rowH), _m.Deaths[p].ToString(), _small, c);
                    Label(new Rect(x + 520, yy, 110, rowH), _m.DamageDealt[p].ToString(), _small, c);
                    yy += rowH;
                }
                yy += 10;
            }
        }

        void DrawMenu(float w, float h)
        {
            float pw = 420, ph = 350, x = w * 0.5f - pw * 0.5f, y = h * 0.5f - ph * 0.5f;
            Rect(0, 0, w, h, new Color(0, 0, 0, 0.45f));
            Rect(x, y, pw, ph, new Color(0.08f, 0.09f, 0.12f, 0.95f));
            Label(new Rect(x, y + 12, pw, 40), L.T("menu"), _big, Color.white);

            if (GUI.Button(new Rect(x + 30, y + 90, pw - 60, 60), L.T("settings"))) _settings = true;

            if (GUI.Button(new Rect(x + 30, y + 180, 170, 44), L.T("resume"))) SetMenu(false);
            if (_m.Role != NetRole.Client && GUI.Button(new Rect(x + 220, y + 180, 170, 44), L.T("restart")))
            {
                PlayerPrefs.Save();
                UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
            if (GUI.Button(new Rect(x + 30, y + 232, 170, 44), L.T("menu_leave")))
            {
                PlayerPrefs.Save();
                SetMenu(false);
                if (MatchController.LeaveHandler != null) MatchController.LeaveHandler();
                else UnityEngine.SceneManagement.SceneManager.LoadScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
            }
            if (GUI.Button(new Rect(x + 220, y + 232, 170, 44), L.T("menu_quit")))
            {
                PlayerPrefs.Save();
                if (MatchController.LeaveHandler != null) MatchController.LeaveHandler();
                Application.Quit();
            }
            Label(new Rect(x + 30, y + 292, pw - 60, 30), L.T("esc_close"), _small, new Color(1, 1, 1, 0.6f));
        }

        void DrawSelect(RoundRules r, float w, float h)
        {
            var me = _m.Human;
            float pw = 560, ph = 300, x = w * 0.5f - pw * 0.5f, y = h * 0.22f;
            Rect(x, y, pw, ph, new Color(0, 0, 0, 0.65f));
            Label(new Rect(x, y + 10, pw, 40), L.T("class_select", r.SecondsLeft(_m.ServerTick)), _big, Color.white);

            var mine = r.GetPick(me.Index);
            Option(new Rect(x + 30, y + 70, 240, 90), L.T("knight_title"), L.T("knight_desc"), mine == PlayerClass.Knight);
            Option(new Rect(x + 290, y + 70, 240, 90), L.T("wizard_title"), L.T("wizard_desc"), mine == PlayerClass.King);

            // 팀원 선택 현황 (실시간)
            string line = "";
            int knights = 0, wizards = 0;
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                if (_m.Avatars[p].Team != me.Team) continue;
                var c = r.GetPick(p);
                if (c == PlayerClass.Knight) knights++; else wizards++;
                line += $"{_m.Avatars[p].DisplayName}:{(c == PlayerClass.Knight ? L.T("knight") : L.T("wizard"))}  ";
            }
            Label(new Rect(x + 20, y + 180, pw - 40, 30), L.T("team_counts", knights, wizards), _mid, Color.white);
            Label(new Rect(x + 20, y + 212, pw - 40, 60), line, _small, new Color(1, 1, 1, 0.85f));
        }

        void Option(Rect rc, string title, string desc, bool selected)
        {
            Rect(rc.x, rc.y, rc.width, rc.height, selected ? new Color(0.3f, 0.55f, 1f, 0.55f) : new Color(1, 1, 1, 0.08f));
            Label(new Rect(rc.x + 12, rc.y + 12, rc.width - 24, 30), title, _mid, Color.white);
            Label(new Rect(rc.x + 12, rc.y + 48, rc.width - 24, 30), desc, _small, new Color(1, 1, 1, 0.8f));
        }

        void DrawPlayerHud(PlayerAvatar me, float w, float h)
        {
            int hp = _m.Combat.Hp[me.Index];

            // 체력
            Rect(20, h - 70, 260, 50, new Color(0, 0, 0, 0.5f));
            Rect(30, h - 38, 240 * hp / 100f, 10, hp > 30 ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.35f, 0.3f));
            Label(new Rect(30, h - 68, 200, 30), me.Alive ? L.T("hp", hp) : L.T("dead_spectate"), _mid, Color.white);

            // 직업별
            if (me.Class == PlayerClass.King)
            {
                float mana = _m.Wizard.Mana(me.Index);
                Rect(w - 360, h - 110, 340, 90, new Color(0, 0, 0, 0.5f));
                Rect(w - 350, h - 38, 320 * mana / WizardRules.MaxMana, 10, new Color(0.45f, 0.6f, 1f));
                Label(new Rect(w - 350, h - 108, 320, 26), L.T("mana", mana.ToString("0")), _mid, Color.white);
                Label(new Rect(w - 350, h - 80, 330, 40),
                    L.T("wizard_keys", WizardRules.CostOf(0), WizardRules.CostOf(1), WizardRules.CostOf(2), MatchController.BuffCost),
                    _small, Color.white);
            }
            else
            {
                Label(new Rect(w - 300, h - 50, 280, 30), L.T("knight_keys"), _small, new Color(1, 1, 1, 0.8f));
            }

            if (_m.Wizard.TeamBuffs[me.Team].IsActive(_m.ServerTick))
                Label(new Rect(20, h - 100, 300, 26), L.T("buff_active"), _small, new Color(0.6f, 0.8f, 1f));

            // 조준점 + 히트마커
            if (me.Alive)
            {
                float cx = w * 0.5f, cy = h * 0.5f;
                Rect(cx - 1, cy - 1, 2, 2, Color.white);
                Rect(cx - 9, cy - 1, 5, 2, Color.white);
                Rect(cx + 4, cy - 1, 5, 2, Color.white);
                Rect(cx - 1, cy - 9, 2, 5, Color.white);
                Rect(cx - 1, cy + 4, 2, 5, Color.white);
                if (Time.time < _m.HitMarkerUntil)
                {
                    var c = _m.HitMarkerKill ? new Color(1f, 0.3f, 0.3f) : Color.white;
                    Label(new Rect(cx - 20, cy - 20, 40, 40), "X", _big, c);
                }
            }
        }

        void DrawKillFeed(float w)
        {
            float y = 90;
            for (int i = _m.KillFeed.Count - 1; i >= 0; i--)
            {
                var k = _m.KillFeed[i];
                if (Time.time - k.Time > 6f) continue;
                Rect(w - 380, y, 360, 26, new Color(0, 0, 0, 0.45f));
                float x = w - 372;
                if (!string.IsNullOrEmpty(k.Killer))
                {
                    Label(new Rect(x, y + 2, 120, 24), k.Killer, _small, k.KillerTeam == 0 ? Blue : Red);
                    x += 90;
                }
                Label(new Rect(x, y + 2, 90, 24), $"[{k.How}]", _small, Color.white);
                x += 80;
                Label(new Rect(x, y + 2, 140, 24), k.Victim, _small, k.VictimTeam == 0 ? Blue : Red);
                y += 30;
            }
        }

        void Rect(float x, float y, float w, float h, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), _white);
            GUI.color = old;
        }

        static void Label(Rect r, string text, GUIStyle style, Color c)
        {
            var old = style.normal.textColor;
            style.normal.textColor = c;
            GUI.Label(r, text, style);
            style.normal.textColor = old;
        }
    }
}
