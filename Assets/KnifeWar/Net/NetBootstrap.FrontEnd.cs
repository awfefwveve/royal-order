using System;
using System.Collections;
using KnifeWar.Game;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 시작부터 다시 대기실까지 화면 흐름 (상용 게임 순서):
    ///   스플래시(ASTREL 로고) → 타이틀(원화 + 로딩 막대, 업데이트·자동 로그인) → 로그인
    ///   → 로비(왼쪽 메뉴·위아래 바) → 플레이(파티 카드·매칭) → 매칭되면 로딩 → 실제 경기
    ///   → 결과(승패·전적·MMR 오르내림·승급/강등) → 다시 플레이 화면
    /// 기존 로그인·파티·매칭 기능(NetBootstrap, .Account)을 그대로 쓰고 모양만 바꾼다.
    /// </summary>
    public sealed partial class NetBootstrap
    {
        enum Scr { Splash, Title, Login, Lobby, Play, Result }

        Scr _scr = Scr.Splash;
        float _scrAt;
        bool _startupDone;
        float _searchStartedAt = -1f;
        float _copiedAt = -10f;
        bool _devOpen;
        bool _resultLoaded;
        MatchSummaryDto _resultMatch;

        static readonly int[] TierFloor = { 0, 900, 1050, 1200, 1350, 1500, 1700 };

        void Go(Scr s)
        {
            if (_scr == s) return;
            _scr = s;
            _scrAt = FE.Now;
            if (s == Scr.Lobby || s == Scr.Play) _side = SidePanel.None;
            if (s == Scr.Result) StartCoroutine(LoadResult());
        }

        float Age => FE.Now - _scrAt;

        /// <summary>메뉴를 처음 열 때 어느 화면부터 보여줄지.</summary>
        void PickFirstScreen()
        {
            _scrAt = FE.Now;
            if (!Session.SplashShown) { _scr = Scr.Splash; return; }
            if (Session.ShowRecentAfterMatch) { _scr = Scr.Title; Session.AfterTitle = Scr.Result.ToString(); return; }
            _scr = Scr.Title;   // 짧게 지나감 (자동 로그인 확인)
        }

        void DrawFrontEnd(float w, float h)
        {
            if (_settingsOpen)
            {
                FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1f, new Color(0.35f, 0.35f, 0.4f));
                if (!SettingsWindow.Draw(w, h)) _settingsOpen = false;
                return;
            }
            switch (_scr)
            {
                case Scr.Splash: DrawSplash(w, h); break;
                case Scr.Title: DrawTitle(w, h); break;
                case Scr.Login: DrawLoginScreen(w, h); break;
                case Scr.Lobby: DrawLobby(w, h); break;
                case Scr.Play: DrawPlay(w, h); break;
                case Scr.Result: DrawResult(w, h); break;
            }
        }

        // ------------------------------------------------------------------ 1. 스플래시

        void DrawSplash(float w, float h)
        {
            FE.Fill(new Rect(0, 0, w, h), Color.black);
            float t = Age;
            const float total = 3.2f;
            float fadeOut = 1f - Mathf.Clamp01((t - (total - 0.6f)) / 0.6f);
            float s = Mathf.Min(w, h) * 0.17f;
            float cx = w * 0.5f, cy = h * 0.44f;

            // 행성: 작게 시작해 커지며 나타남
            float pa = FE.Ease(t / 0.8f) * fadeOut;
            float ps = s * Mathf.Lerp(0.7f, 1f, FE.Ease(t / 0.9f));
            FE.Image(new Rect(cx - ps * 0.5f, cy - ps * 0.5f, ps, ps), FE.Planet, pa);
            // 고리: 조금 늦게, 돌면서 자리 잡음
            float ra = FE.Ease((t - 0.25f) / 0.7f) * fadeOut;
            float rot = Mathf.Lerp(-40f, 0f, FE.Ease((t - 0.25f) / 1.0f));
            FE.Rotated(new Rect(cx - ps * 0.5f, cy - ps * 0.5f, ps, ps), FE.Ring, rot, ra);
            // 이름: 아래에서 떠오름
            float wa = FE.Ease((t - 0.8f) / 0.7f) * fadeOut;
            float ww = s * 2.6f, wh = ww / FE.WordmarkAspect, wy = cy + s * 0.62f + (1f - wa) * 14f;
            FE.Image(new Rect(cx - ww * 0.5f, wy, ww, wh), FE.Wordmark, wa);

            bool skip = Event.current.type == EventType.MouseUp || Event.current.type == EventType.KeyUp;
            if (t > total || (skip && t > 0.4f))
            {
                Session.SplashShown = true;
                Go(Scr.Title);
            }
        }

        // ------------------------------------------------------------------ 2. 타이틀 (원화 + 로딩)

        void DrawTitle(float w, float h)
        {
            float t = Age;
            FE.Fill(new Rect(0, 0, w, h), Color.black);
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, FE.Ease(t / 0.8f), 1.06f - 0.04f * Mathf.Clamp01(t / 8f));
            FE.ShadeBottom(new Rect(0, h * 0.45f, w, h * 0.55f), new Color(0, 0, 0, 0.92f));

            float tw = Mathf.Min(w * 0.62f, 980f), th = tw / FE.TitleAspect;
            float ta = FE.Ease((t - 0.3f) / 0.8f);
            FE.Image(new Rect((w - tw) * 0.5f, h * 0.60f - th * 0.5f + (1f - ta) * 10f, tw, th), FE.Title, ta);
            FE.Text(new Rect(0, h * 0.60f + th * 0.38f, w, 30), "황실기사단", 20, new Color(1, 1, 1, 0.75f * ta), TextAnchor.MiddleCenter);

            // 로딩 막대: 업데이트 받는 중이면 실제 진행률, 아니면 시간으로 채움
            float bw = Mathf.Min(w * 0.5f, 720f), bx = (w - bw) * 0.5f, by = h - 92f;
            float p;
            string msg;
            if (_updater.Current == GameUpdater.State.Downloading || _updater.Current == GameUpdater.State.Applying)
            { p = _updater.Progress; msg = _updater.Message; }
            else if (_updater.Current == GameUpdater.State.Failed)
            { p = 1f; msg = _updater.Message; }
            else
            {
                p = _startupDone ? Mathf.Clamp01(t / 1.6f) : Mathf.Min(0.85f, t / 3f);
                msg = _startupDone ? L.T("fe_ready") : (_updater.Busy ? _updater.Message : L.T("fe_loading"));
            }
            FE.Bar(new Rect(bx, by, bw, 4), p, FE.Gold);
            FE.Text(new Rect(bx, by + 10, bw, 26), msg ?? "", 15, FE.Sub, TextAnchor.MiddleCenter);
            string v = GameUpdater.ReadLocalVersion();
            FE.Text(new Rect(w - 220, h - 30, 204, 22), "v" + (string.IsNullOrEmpty(v) ? "dev" : v), 13, FE.Sub, TextAnchor.MiddleRight);

            if (_updater.Current == GameUpdater.State.Failed)
            {
                if (FE.Button(new Rect(w * 0.5f - 210, by - 70, 200, 44), L.T("upd_retry")))
                {
                    Session.UpdateChecked = false;
                    _startupDone = false;
                    StartCoroutine(MenuStartup());
                }
                if (FE.Button(new Rect(w * 0.5f + 10, by - 70, 200, 44), L.T("upd_skip")))
                {
                    _updater.SkipFailed();
                    StartCoroutine(FinishStartup());
                }
                return;
            }

            if (_startupDone && t > 1.6f)
            {
                string after = Session.AfterTitle;
                Session.AfterTitle = null;
                if (after == Scr.Result.ToString()) Go(Scr.Result);
                else if (_profile == null) Go(Scr.Login);
                else Go(after == Scr.Play.ToString() ? Scr.Play : Scr.Lobby);
            }
        }

        IEnumerator FinishStartup()
        {
            yield return AutoLogin();
            _startupDone = true;
        }

        // ------------------------------------------------------------------ 3. 로그인

        void DrawLoginScreen(float w, float h)
        {
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1.02f, new Color(0.55f, 0.55f, 0.6f));
            FE.ShadeLeft(new Rect(0, 0, w * 0.6f, h), new Color(0, 0, 0, 0.85f));
            if (_profile != null) { Go(Scr.Lobby); return; }

            float pw = Mathf.Min(480f, w - 48f), x = Mathf.Max(24f, w * 0.08f), y = h * 0.12f;
            float tw = pw, th = tw / FE.TitleAspect;
            FE.Image(new Rect(x, y, tw, th), FE.Title);
            y += th + 12;
            y = DrawLogin(x, y, pw);
            y += 10;
            if (FE.Button(new Rect(x, y, pw, 44), L.T("fe_offline_play"), FE.Kind.Ghost, 16)) StartOffline();
            y += 52;
            DrawTopRight(w);
            if (Debug.isDebugBuild) DrawDevTools(w, h, x, y, pw);
        }

        /// <summary>개발 빌드에서만: 직접 접속·호스트·백엔드 주소.</summary>
        void DrawDevTools(float w, float h, float x, float y, float pw)
        {
            if (FE.Button(new Rect(x, h - 44, 160, 32), L.T("fe_dev"), FE.Kind.Ghost, 13)) _devOpen = !_devOpen;
            if (!_devOpen) return;
            float dy = Mathf.Max(y, h - 300f);
            FE.Fill(new Rect(x - 12, dy - 12, pw + 24, 250), FE.Panel);
            if (GUI.Button(new Rect(x, dy, pw * 0.48f, 34), L.T("menu_host"), _button)) StartServer(NetRole.Host, ParsePort());
            dy += 42;
            _address = GUI.TextField(new Rect(x, dy, pw - 130, 32), _address, _field);
            _portText = GUI.TextField(new Rect(x + pw - 120, dy, 120, 32), _portText, _field);
            dy += 38;
            GUI.Label(new Rect(x, dy, pw, 24), L.T("menu_lag", (_lag * 2).ToString("0")), _small);
            dy += 24;
            _lag = Mathf.Round(GUI.HorizontalSlider(new Rect(x, dy, pw, 20), _lag, 0f, 150f));
            dy += 24;
            if (GUI.Button(new Rect(x, dy, pw, 34), L.T("menu_connect"), _button))
            {
                PlayerPrefs.SetString("kw.address", _address);
                StartClient(_address.Trim(), ParsePort(), _lag, false, "");
            }
            dy += 42;
            GUI.Label(new Rect(x, dy, 110, 26), L.T("menu_backend"), _small);
            GUI.enabled = _profile == null;
            _backendUrl = GUI.TextField(new Rect(x + 110, dy, pw - 110, 28), _backendUrl);
            GUI.enabled = true;
        }

        void DrawTopRight(float w)
        {
            for (int i = 0; i < L.LangNames.Length; i++)
            {
                bool cur = (int)L.Current == i;
                var r = new Rect(w - (L.LangNames.Length - i) * 74 - 16, 16, 70, 30);
                if (cur) FE.Fill(r, new Color(1, 1, 1, 0.14f));
                if (FE.Button(r, L.LangNames[i], FE.Kind.Ghost, 14) && !cur) L.Set((Lang)i);
            }
        }

        // ------------------------------------------------------------------ 공통: 위·아래 바

        const float TopH = 64f, BottomH = 56f;

        void DrawTopBar(float w)
        {
            FE.Fill(new Rect(0, 0, w, TopH), new Color(0, 0, 0, 0.78f));
            FE.Fill(new Rect(0, TopH - 1, w, 1), FE.Line);
            float s = 36f;
            FE.Image(new Rect(18, (TopH - s) * 0.5f, s, s), FE.Planet);
            FE.Image(new Rect(18, (TopH - s) * 0.5f, s, s), FE.Ring);
            FE.Image(new Rect(64, (TopH - 30) * 0.5f, 120, 30), FE.Title);

            if (_profile != null)
            {
                string tier = TierName(_profile.tier);
                FE.Text(new Rect(w - 520, 8, 500, 26), _profile.displayId, 18, Color.white, TextAnchor.MiddleRight, true);
                FE.Text(new Rect(w - 520, 32, 500, 22), $"{tier} · MMR {_profile.mmr} · {_profile.wins}W {_profile.losses}L", 14, FE.Gold, TextAnchor.MiddleRight);
            }
            if (!string.IsNullOrEmpty(Session.Notice))
                FE.Text(new Rect(220, 0, w - 760, TopH), "▶ " + Session.Notice, 14, FE.Sub, TextAnchor.MiddleLeft);
            if (Session.Maintenance)
                FE.Text(new Rect(220, 0, w - 760, TopH), L.T("upd_maintenance"), 15, FE.Red, TextAnchor.MiddleLeft, true);
        }

        void DrawBottomBar(float w, float h, bool back)
        {
            float y = h - BottomH;
            FE.Fill(new Rect(0, y, w, BottomH), new Color(0, 0, 0, 0.82f));
            FE.Fill(new Rect(0, y, w, 1), FE.Line);
            float x = 16;
            if (back)
            {
                if (FE.Button(new Rect(x, y + 8, 56, 40), "<", FE.Kind.Ghost, 26)) Go(Scr.Lobby);
                x += 72;
            }
            if (FE.Button(new Rect(x, y + 10, 130, 36), L.T("menu_recent"), FE.Kind.Ghost, 15)) OpenSide(SidePanel.Matches);
            x += 136;
            if (FE.Button(new Rect(x, y + 10, 130, 36), L.T("menu_board"), FE.Kind.Ghost, 15)) OpenSide(SidePanel.Leaderboard);
            x += 136;
            if (FE.Button(new Rect(x, y + 10, 130, 36), L.T("settings"), FE.Kind.Ghost, 15)) _settingsOpen = true;
            x += 136;
            if (_profile != null && !_profile.emailVerified && FE.Button(new Rect(x, y + 10, 150, 36), L.T("fe_verify"), FE.Kind.Ghost, 15)) OpenSide(SidePanel.Verify);
            if (FE.Button(new Rect(w - 300, y + 10, 130, 36), L.T("acc_logout"), FE.Kind.Ghost, 14)) { Logout(); Go(Scr.Login); }
            string v = GameUpdater.ReadLocalVersion();
            FE.Text(new Rect(w - 160, y, 144, BottomH), "v" + (string.IsNullOrEmpty(v) ? "dev" : v), 13, FE.Sub, TextAnchor.MiddleRight);
        }

        /// <summary>최근 경기·랭킹·인증 패널 (오른쪽).</summary>
        void DrawSidePanelRight(float w, float h)
        {
            if (_side == SidePanel.None || _profile == null) return;
            float sw = Mathf.Min(420f, w * 0.34f), sx = w - sw - 24, sy = TopH + 24;
            FE.Fill(new Rect(sx - 16, sy - 16, sw + 32, h - sy - BottomH - 16), FE.Panel);
            DrawSide(sx, sy, sw, h - BottomH);
        }

        // ------------------------------------------------------------------ 4. 로비

        static readonly string[] LobbyItems = { "fe_play", "fe_practice", "menu_recent", "menu_board", "settings", "fe_quit" };

        void DrawLobby(float w, float h)
        {
            if (_profile == null) { Go(Scr.Login); return; }
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1.03f + 0.01f * Mathf.Sin(FE.Now * 0.15f));
            FE.ShadeLeft(new Rect(0, 0, w * 0.55f, h), new Color(0, 0, 0, 0.88f));
            DrawTopBar(w);

            if (!_profile.termsOk)
            {
                // 약관이 바뀌었으면 다시 동의해야 매칭 가능
                float pw = Mathf.Min(520f, w - 48f), px = (w - pw) * 0.5f, py = h * 0.22f;
                FE.Fill(new Rect(px - 24, py - 24, pw + 48, 360), FE.Panel);
                DrawProfileAndMatch(px, py, pw);
                DrawBottomBar(w, h, false);
                return;
            }

            float x = Mathf.Max(32f, w * 0.05f), y = TopH + Mathf.Max(40f, h * 0.12f);
            float accentW = 6f;
            FE.Fill(new Rect(x - 18, y - 6, accentW, 6 * 56 + 40), new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, 0.6f));
            for (int i = 0; i < LobbyItems.Length; i++)
            {
                bool big = i == 0;
                float ih = big ? 84f : 56f;
                var r = new Rect(x, y, 420, ih);
                bool hover = FE.Hover(r);
                float off = hover ? 10f : 0f;
                Color c = big ? (hover ? Color.white : FE.Red) : (hover ? FE.Gold : Color.white);
                FE.Text(new Rect(x + off, y, 20, ih), "•", big ? 22 : 18, c, TextAnchor.MiddleLeft);
                FE.Text(new Rect(x + 28 + off, y, 400, ih), L.T(LobbyItems[i]), big ? 56 : 32, c, TextAnchor.MiddleLeft, true);
                if (FE.Clicked(r)) OnLobbyItem(i);
                y += ih + (big ? 8 : 0);
            }
            if (_party != null && _party.inParty)
                FE.Text(new Rect(x, y + 20, 420, 26), L.T("fe_in_party", _party.members?.Length ?? 1), 16, FE.Gold);

            DrawSidePanelRight(w, h);
            DrawBottomBar(w, h, false);
        }

        void OnLobbyItem(int i)
        {
            switch (LobbyItems[i])
            {
                case "fe_play": Go(Scr.Play); break;
                case "fe_practice": StartOffline(); break;
                case "menu_recent": OpenSide(SidePanel.Matches); break;
                case "menu_board": OpenSide(SidePanel.Leaderboard); break;
                case "settings": _settingsOpen = true; break;
                case "fe_quit":
                    PlayerPrefs.Save();
                    Application.Quit();
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#endif
                    break;
            }
        }

        // ------------------------------------------------------------------ 5. 플레이 (파티·매칭)

        void DrawPlay(float w, float h)
        {
            if (_profile == null) { Go(Scr.Login); return; }
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1.1f, new Color(0.18f, 0.24f, 0.38f));
            FE.Fill(new Rect(0, 0, w, h), new Color(0.03f, 0.06f, 0.12f, 0.55f));
            DrawTopBar(w);

            // 모드 탭
            float tabY = TopH + 18, tabW = 180;
            string[] tabs = { L.T("fe_ranked"), L.T("fe_practice_bots") };
            float tx = (w - tabs.Length * tabW) * 0.5f;
            for (int i = 0; i < tabs.Length; i++)
            {
                var r = new Rect(tx + i * tabW, tabY, tabW, 36);
                bool sel = i == 0;
                FE.Text(r, tabs[i], 17, sel ? FE.Gold : (FE.Hover(r) ? Color.white : FE.Sub), TextAnchor.MiddleCenter, sel);
                if (sel) FE.Fill(new Rect(r.x + 30, r.yMax - 3, r.width - 60, 3), FE.Gold);
                if (i == 1 && FE.Clicked(r)) StartOffline();
            }
            FE.Fill(new Rect(w * 0.15f, tabY + 40, w * 0.7f, 1), FE.Line);

            // 파티 코드 줄
            float cy = tabY + 58;
            bool inParty = _party != null && _party.inParty;
            if (inParty)
            {
                FE.Text(new Rect(0, cy, w * 0.5f - 8, 34), L.T("fe_party_code", _party.code), 18, Color.white, TextAnchor.MiddleRight, true);
                bool copied = FE.Now - _copiedAt < 1.5f;
                if (FE.Button(new Rect(w * 0.5f + 8, cy, 110, 34), copied ? L.T("fe_copied") : L.T("fe_copy"), FE.Kind.Line, 15))
                { GUIUtility.systemCopyBuffer = _party.code; _copiedAt = FE.Now; }
            }
            else
            {
                GUI.enabled = !_partyBusy && !_searching;
                float rw = 520, rx = (w - rw) * 0.5f;
                if (FE.Button(new Rect(rx, cy, 170, 34), L.T("fe_create_party"), FE.Kind.Line, 15)) StartCoroutine(PartyAction("/party/create", "{}"));
                _partyCodeInput = GUI.TextField(new Rect(rx + 186, cy + 2, 190, 30), _partyCodeInput.ToUpperInvariant(), 8, _field);
                if (FE.Button(new Rect(rx + 386, cy, 134, 34), L.T("fe_join_code"), FE.Kind.Line, 15) && _partyCodeInput.Trim().Length > 0)
                    StartCoroutine(PartyAction("/party/join", $"{{\"code\":\"{BackendClient.Escape(_partyCodeInput.Trim())}\"}}"));
                GUI.enabled = true;
            }

            // 카드 6장
            const int slots = 6;
            float gap = 16f, cw = Mathf.Min(176f, (w - 120f - gap * (slots - 1)) / slots), ch = Mathf.Min(cw * 1.9f, h - TopH - BottomH - 300f);
            float cx0 = (w - (cw * slots + gap * (slots - 1))) * 0.5f, cardY = cy + 56;
            var members = inParty ? _party.members : null;
            for (int i = 0; i < slots; i++)
            {
                var r = new Rect(cx0 + i * (cw + gap), cardY, cw, ch);
                PartyMemberDto m = null;
                if (inParty) { if (members != null && i < members.Length) m = members[i]; }
                else if (i == 0) m = new PartyMemberDto { id = _profile.id, name = _profile.displayId, leader = true, me = true };
                DrawCard(r, m, inParty);
            }

            // 상태 글
            float sy = cardY + ch + 18;
            if (!string.IsNullOrEmpty(_searchText) && !_searching)
                FE.Text(new Rect(w * 0.15f, sy, w * 0.7f, 44), _searchText, 15, FE.Sub, TextAnchor.UpperCenter);
            else FE.Text(new Rect(w * 0.15f, sy, w * 0.7f, 44), L.T("fe_mode_desc"), 14, FE.Sub, TextAnchor.UpperCenter);

            // 아래: 연습 / 매칭 / 파티 나가기
            float by = h - BottomH - 86, bw = 360;
            var main = new Rect((w - bw) * 0.5f, by, bw, 60);
            bool member = inParty && !_party.isLeader;
            if (_searching)
            {
                if (_searchStartedAt < 0f) _searchStartedAt = FE.Now;
                int sec = (int)(FE.Now - _searchStartedAt);
                float pulse = 0.75f + 0.25f * Mathf.Sin(FE.Now * 4f);
                FE.Fill(main, new Color(0.05f, 0.08f, 0.14f, 0.95f));
                FE.Border(main, new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, pulse), 2f);
                FE.Text(new Rect(main.x, main.y + 4, main.width, 34), L.T("fe_waiting", sec / 60, sec % 60), 22, Color.white, TextAnchor.MiddleCenter, true);
                FE.Text(new Rect(main.x, main.y + 34, main.width, 20), member ? L.T("fe_wait_leader") : L.T("fe_cancel_hint"), 12, FE.Sub, TextAnchor.MiddleCenter);
                if (!member && FE.Clicked(main)) { CancelSearch(); _searchStartedAt = -1f; }
            }
            else
            {
                _searchStartedAt = -1f;
                GUI.enabled = !member && !Session.Maintenance;
                if (FE.Button(main, member ? L.T("fe_wait_leader") : L.T("fe_find"), FE.Kind.Primary, 22))
                {
                    _searchText = "";
                    StartCoroutine(Matchmake(_backendUrl.Trim(), _profile.name, "", _lag, false));
                }
                GUI.enabled = true;
            }
            GUI.enabled = !_searching;
            if (FE.Button(new Rect(main.x - 216, by + 10, 200, 40), L.T("fe_practice"), FE.Kind.Line, 16)) StartOffline();
            if (inParty && FE.Button(new Rect(main.xMax + 16, by + 10, 200, 40), L.T("fe_leave_party"), FE.Kind.Line, 16)) LeaveParty();
            GUI.enabled = true;

            DrawSidePanelRight(w, h);
            DrawBottomBar(w, h, true);
        }

        void DrawCard(Rect r, PartyMemberDto m, bool inParty)
        {
            bool hover = FE.Hover(r);
            if (m == null)
            {
                FE.Fill(r, new Color(1, 1, 1, hover ? 0.07f : 0.03f));
                FE.Border(r, new Color(1, 1, 1, hover ? 0.35f : 0.15f));
                FE.Text(new Rect(r.x, r.y, r.width, r.height - 30), "+", 54, new Color(1, 1, 1, hover ? 0.9f : 0.45f), TextAnchor.MiddleCenter);
                FE.Text(new Rect(r.x, r.yMax - 60, r.width, 30), L.T("fe_invite"), 14, FE.Sub, TextAnchor.MiddleCenter);
                if (FE.Clicked(r) && !_searching && !_partyBusy)
                {
                    if (!inParty) StartCoroutine(PartyAction("/party/create", "{}"));
                    else { GUIUtility.systemCopyBuffer = _party.code; _copiedAt = FE.Now; }
                }
                return;
            }
            Color team = m.me ? FE.Gold : FE.Blue;
            FE.Fill(r, new Color(0.06f, 0.09f, 0.16f, 0.95f));
            FE.Fill(new Rect(r.x, r.y, r.width, 6), team);
            FE.Border(r, m.me ? FE.Gold : new Color(1, 1, 1, 0.22f), m.me ? 2f : 1f);
            // 얼굴 자리: 이름 첫 글자 (캐릭터 초상화가 생기면 여기에)
            float d = r.width * 0.56f;
            var face = new Rect(r.center.x - d * 0.5f, r.y + r.height * 0.18f, d, d);
            FE.Fill(face, new Color(team.r, team.g, team.b, 0.18f));
            FE.Border(face, new Color(team.r, team.g, team.b, 0.7f), 2f);
            string initial = string.IsNullOrEmpty(m.name) ? "?" : m.name.Substring(0, 1).ToUpperInvariant();
            FE.Text(face, initial, (int)(d * 0.5f), Color.white, TextAnchor.MiddleCenter, true);
            if (m.leader) FE.Text(new Rect(r.x, r.y + 12, r.width, 22), "★ " + L.T("fe_leader"), 13, FE.Gold, TextAnchor.MiddleCenter, true);

            var nameBand = new Rect(r.x, r.yMax - r.height * 0.3f, r.width, 34);
            FE.Fill(nameBand, m.me ? new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, 0.85f) : new Color(1, 1, 1, 0.1f));
            FE.Text(nameBand, m.name, 15, m.me ? FE.Ink : Color.white, TextAnchor.MiddleCenter, true, !m.me);
            string sub = m.me && _profile != null ? $"{TierName(_profile.tier)} · {_profile.mmr}" : L.T("fe_ready_tag");
            FE.Text(new Rect(r.x, nameBand.yMax + 6, r.width, 22), sub, 13, FE.Sub, TextAnchor.MiddleCenter);
            if (m.me) FE.Text(new Rect(r.x, nameBand.yMax + 28, r.width, 20), L.T("fe_me"), 12, FE.Gold, TextAnchor.MiddleCenter);
            else if (inParty && _party.isLeader && !_searching && FE.Button(new Rect(r.xMax - 30, r.y + 10, 22, 22), "×", FE.Kind.Ghost, 16))
                StartCoroutine(_api.Post("/party/kick", $"{{\"accountId\":\"{m.id}\"}}", null));
        }

        // ------------------------------------------------------------------ 6. 매칭 후 로딩 (경기 화면이 뜰 때까지)

        void DrawMatchLoading(float w, float h)
        {
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1.08f + 0.02f * Mathf.Sin(FE.Now * 0.2f));
            FE.ShadeBottom(new Rect(0, h * 0.35f, w, h * 0.65f), new Color(0, 0, 0, 0.95f));
            float x = Mathf.Max(32f, w * 0.06f), y = h - 230;
            FE.Text(new Rect(x, y, w, 30), "ROYAL ORDER · 6 vs 6", 16, FE.Gold, TextAnchor.MiddleLeft, true);
            FE.Text(new Rect(x, y + 30, w, 64), L.T("fe_map"), 52, Color.white, TextAnchor.MiddleLeft, true);
            int tip = (int)(FE.Now / 6f) % 3 + 1;
            FE.Text(new Rect(x, y + 104, w * 0.6f, 50), "TIP  " + L.T("fe_tip" + tip), 16, FE.Sub, TextAnchor.UpperLeft);
            FE.Bar(new Rect(x, h - 56, w - x * 2, 4), -1f, FE.Gold);
            FE.Text(new Rect(x, h - 48, w - x * 2, 26), NetInfo.Status ?? "", 14, FE.Sub, TextAnchor.MiddleRight);
        }

        // ------------------------------------------------------------------ 7. 결과

        IEnumerator LoadResult()
        {
            _resultLoaded = false;
            _resultMatch = null;
            Session.ShowRecentAfterMatch = false;
            if (_api != null && !string.IsNullOrEmpty(_api.Token))
            {
                yield return _api.Get("/me/matches", (code, text) =>
                {
                    if (code != 200) return;
                    var list = JsonUtility.FromJson<MatchSummaryList>("{\"items\":" + text + "}").items;
                    if (list != null && list.Length > 0) _resultMatch = list[0];
                });
                yield return _api.Get("/me", (code, text) => { if (code == 200) _profile = JsonUtility.FromJson<ProfileDto>(text); });
            }
            _resultLoaded = true;
            _scrAt = FE.Now;   // 연출은 받은 뒤부터
        }

        void DrawResult(float w, float h)
        {
            FE.Cover(new Rect(0, 0, w, h), FE.KeyArt, FE.KeyArtAspect, 1f, 1.12f, new Color(0.3f, 0.3f, 0.36f));
            FE.Fill(new Rect(0, 0, w, h), new Color(0, 0, 0, 0.55f));
            if (!_resultLoaded) { FE.Text(new Rect(0, 0, w, h), L.T("fe_result_loading"), 20, FE.Sub, TextAnchor.MiddleCenter); return; }

            float t = Age;
            var m = _resultMatch;
            int outcome = m != null ? (m.winnerTeam == -2 ? -2 : m.winnerTeam < 0 ? -1 : m.winnerTeam == m.myTeam ? 1 : 0) : Session.LastOutcome;
            string label = outcome == 1 ? L.T("result_win") : outcome == 0 ? L.T("result_lose") : outcome == -2 ? L.T("recent_void") : L.T("result_draw");
            Color lc = outcome == 1 ? FE.Gold : outcome == 0 ? FE.Red : Color.white;

            // 승패 글자: 크게 시작해 자리 잡음
            float k = FE.Ease(t / 0.45f);
            int size = (int)Mathf.Lerp(150f, 96f, k);
            FE.Text(new Rect(0, h * 0.08f, w, 130), label, size, new Color(lc.r, lc.g, lc.b, k), TextAnchor.MiddleCenter, true);
            FE.Fill(new Rect(w * 0.5f - 220 * k, h * 0.08f + 128, 440 * k, 2), new Color(lc.r, lc.g, lc.b, 0.8f));

            float colW = Mathf.Min(420f, w * 0.3f), top = h * 0.08f + 160;
            // 양 팀 전적
            if (m != null && m.players != null)
            {
                float a = FE.Ease((t - 0.4f) / 0.5f);
                for (int side = 0; side < 2; side++)
                {
                    bool mine = side == 0;
                    float x = mine ? w * 0.5f - colW - 40 : w * 0.5f + 40;
                    FE.Text(new Rect(x, top, colW, 28), mine ? L.T("fe_my_team") : L.T("fe_enemy_team"), 16, new Color(1, 1, 1, a), TextAnchor.MiddleLeft, true);
                    FE.Text(new Rect(x, top, colW, 28), "K / D", 14, new Color(1, 1, 1, 0.6f * a), TextAnchor.MiddleRight);
                    float y = top + 32;
                    foreach (var p in m.players)
                    {
                        if ((p.team == m.myTeam) != mine) continue;
                        var row = new Rect(x, y, colW, 30);
                        FE.Fill(row, p.me ? new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, 0.22f * a) : new Color(1, 1, 1, 0.05f * a));
                        FE.Text(new Rect(x + 10, y, colW - 100, 30), p.name, 15, new Color(1, 1, 1, a), TextAnchor.MiddleLeft, p.me);
                        FE.Text(new Rect(x, y, colW - 10, 30), $"{p.kills} / {p.deaths}", 15, new Color(1, 1, 1, a), TextAnchor.MiddleRight);
                        y += 34;
                    }
                }
            }
            else FE.Text(new Rect(0, top, w, 40), L.T("fe_no_record"), 16, FE.Sub, TextAnchor.MiddleCenter);

            // MMR · 티어 (숫자가 올라가거나 내려가는 연출, 이어서 승급/강등)
            if (m != null && _profile != null && outcome != -2)
            {
                float py = h - BottomH - 210, pw = Mathf.Min(560f, w - 80f), px = (w - pw) * 0.5f;
                int now = _profile.mmr, before = now - m.mmrChange;
                float c = FE.Ease((t - 0.9f) / 1.2f);
                int shown = Mathf.RoundToInt(Mathf.Lerp(before, now, c));
                int tierNow = _profile.tier, tierBefore = Session.PrevTier >= -1 && Session.PrevMmr == before ? Session.PrevTier : TierOf(before, tierNow);
                int tierShown = c < 1f ? TierOf(shown, tierBefore < 0 ? -1 : 0) : tierNow;
                FE.Fill(new Rect(px - 20, py - 16, pw + 40, 140), FE.Panel);
                FE.Text(new Rect(px, py, pw, 30), TierName(tierShown), 22, FE.Gold, TextAnchor.MiddleLeft, true);
                string delta = (m.mmrChange >= 0 ? "+" : "") + m.mmrChange;
                FE.Text(new Rect(px, py, pw, 30), $"MMR {shown}   ({delta})", 20, m.mmrChange >= 0 ? new Color(0.5f, 1f, 0.6f) : FE.Red, TextAnchor.MiddleRight, true);
                float lo, hi;
                TierRange(Mathf.Max(0, tierShown), out lo, out hi);
                float prog = tierShown < 0 ? Mathf.Clamp01(_profile.matches / 5f) : Mathf.InverseLerp(lo, hi, shown);
                FE.Bar(new Rect(px, py + 46, pw, 10), prog, FE.Gold);
                FE.Text(new Rect(px, py + 62, pw, 24), tierShown < 0 ? L.T("fe_placement", Mathf.Min(5, _profile.matches)) : $"{lo:0}  —  {hi:0}", 13, FE.Sub, TextAnchor.MiddleCenter);

                if (c >= 1f && tierBefore >= 0 && tierNow != tierBefore)
                {
                    bool up = tierNow > tierBefore;
                    float pa = FE.Ease((t - 2.2f) / 0.4f);
                    float glow = 0.6f + 0.4f * Mathf.Sin(FE.Now * 5f);
                    FE.Text(new Rect(0, py - 74, w, 56), L.T(up ? "fe_promoted" : "fe_demoted", TierName(tierNow)),
                        (int)Mathf.Lerp(64f, 40f, pa), up ? new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, pa * glow + 0.2f) : new Color(FE.Red.r, FE.Red.g, FE.Red.b, pa), TextAnchor.MiddleCenter, true);
                }
            }

            if (t > 1.2f && FE.Button(new Rect((w - 300) * 0.5f, h - BottomH - 50, 300, 52), L.T("fe_continue"), FE.Kind.Primary, 20)) Go(Scr.Play);
        }

        static int TierOf(int mmr, int fallbackPlacement)
        {
            if (fallbackPlacement < 0) return -1;
            int t = 0;
            for (int i = 0; i < TierFloor.Length; i++) if (mmr >= TierFloor[i]) t = i;
            return t;
        }

        static void TierRange(int tier, out float lo, out float hi)
        {
            tier = Mathf.Clamp(tier, 0, TierFloor.Length - 1);
            lo = TierFloor[tier];
            hi = tier + 1 < TierFloor.Length ? TierFloor[tier + 1] : lo + 300;
        }
    }
}
