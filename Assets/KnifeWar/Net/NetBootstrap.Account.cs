using System;
using System.Collections;
using KnifeWar.Game;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 자체 계정 (라이엇·넥슨식 자체 브랜드): 이메일 + 비밀번호 로그인, 이름#태그, 자동 로그인,
    /// 프로필(랭크·전적), 리더보드, 최근 경기와 신고.
    /// 로그인 정보는 비밀번호가 아니라 서버가 준 세션 토큰만 이 PC에 저장한다 (30일).
    /// </summary>
    public sealed partial class NetBootstrap
    {
        enum AccountMode { Login, Register, ResetRequest, Reset }

        ProfileDto _profile;
        AccountMode _accMode = AccountMode.Login;
        string _email = "", _password = "", _password2 = "", _regName = "", _code = "";
        bool _agreeTerms, _age14;
        internal void ShowcaseRegister(bool on) { _accMode = on ? AccountMode.Register : AccountMode.Login; _email = on ? "player@example.com" : ""; _regName = on ? "Bojung" : ""; _agreeTerms = _age14 = on; }
        [Serializable] sealed class TermsInfo { public string termsVersion; public string termsUrl; public string privacyUrl; }
        TermsInfo _terms;
        bool _termsLoading;

        /// <summary>현재 약관 버전·주소를 백엔드에서 받아 둔다 (가입 화면·재동의 화면에서 필요).</summary>
        IEnumerator LoadTerms()
        {
            if (_terms != null || _termsLoading) yield break;
            _termsLoading = true;
            yield return new BackendClient(_backendUrl.Trim()).Get("/client/version", (code, text) =>
            {
                if (code == 200) try { _terms = JsonUtility.FromJson<TermsInfo>(text); } catch { }
            });
            _termsLoading = false;
        }

        /// <summary>약관·개인정보 동의 체크 + 보기 버튼. 둘 다 체크해야 true.</summary>
        float DrawTermsChecks(float x, float y, float pw, bool withAge)
        {
            if (_terms == null) { if (!_termsLoading) StartCoroutine(LoadTerms()); }
            _agreeTerms = GUI.Toggle(new Rect(x, y, pw - 190, 26), _agreeTerms, " " + L.T("acc_agree_terms"));
            GUI.enabled = _terms != null && !string.IsNullOrEmpty(_terms.termsUrl);
            if (GUI.Button(new Rect(x + pw - 186, y, 90, 26), L.T("acc_view_terms"))) Application.OpenURL(_terms.termsUrl);
            if (GUI.Button(new Rect(x + pw - 92, y, 92, 26), L.T("acc_view_privacy"))) Application.OpenURL(_terms.privacyUrl);
            GUI.enabled = !_accBusy;
            y += 30;
            if (withAge)
            {
                _age14 = GUI.Toggle(new Rect(x, y, pw, 26), _age14, " " + L.T("acc_age14"));
                y += 30;
            }
            return y + 4;
        }

        void DoAgreeTerms()
        {
            if (_terms == null || !_agreeTerms) { _accMsg = L.T("acc_need_terms"); return; }
            StartCoroutine(AccountCall("/auth/terms", $"{{\"version\":\"{BackendClient.Escape(_terms.termsVersion)}\"}}", (code, text) =>
            {
                if (code == 200) { _profile = JsonUtility.FromJson<ProfileDto>(text); _agreeTerms = false; }
                else { _terms = null; _accMsg = L.T("acc_terms_changed"); }   // 그새 버전이 바뀜 → 다시 받기
            }));
        }
        string _accMsg = "";
        bool _accBusy;
        string _lastError = "";

        enum SidePanel { None, Leaderboard, Matches, Verify }
        SidePanel _side;
        LeaderDto[] _board;
        MatchSummaryDto[] _matches;
        string _reportTarget;      // 신고 이유를 고르는 중인 계정
        string _sideMsg = "";

        static readonly string[] ReportReasons = { "cheat", "abuse", "troll", "name", "afk" };

        // ------------------------------------------------------------------
        // 시작: 자동 업데이트 → 자동 로그인
        // ------------------------------------------------------------------

        readonly GameUpdater _updater = new GameUpdater();

        IEnumerator MenuStartup()
        {
            yield return null;
            if (_started) yield break;   // 실행 옵션으로 바로 시작한 경우 (서버·테스트)
            if (!Session.UpdateChecked)
            {
                Session.UpdateChecked = true;
                yield return _updater.Run(_backendUrl.Trim());
                Session.Notice = _updater.Notice;
                Session.Maintenance = _updater.Maintenance;
                if (_updater.Current == GameUpdater.State.Failed) yield break;   // 화면에서 다시 시도/계속 선택
            }
            yield return AutoLogin();
            _startupDone = true;
        }

        /// <summary>업데이트 중이면 화면 전체를 쓰고 true.</summary>
        bool DrawUpdater(float w, float h)
        {
            if (!_updater.Busy && _updater.Current != GameUpdater.State.Failed) return false;
            GUI.Label(new Rect(0, h * 0.25f, w, 60), L.T("title"), _title);
            float bw = Mathf.Min(600f, w - 40f), bx = (w - bw) * 0.5f, by = h * 0.5f;
            GUI.Label(new Rect(bx, by - 40, bw, 32), _updater.Message, _label);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            GUI.DrawTexture(new Rect(bx, by, bw, 14), Texture2D.whiteTexture);
            GUI.color = new Color(0.35f, 0.7f, 1f, 1f);
            GUI.DrawTexture(new Rect(bx, by, bw * Mathf.Clamp01(_updater.Progress), 14), Texture2D.whiteTexture);
            GUI.color = old;
            if (_updater.Current == GameUpdater.State.Failed)
            {
                if (GUI.Button(new Rect(bx, by + 40, bw * 0.48f, 44), L.T("upd_retry"), _button))
                {
                    Session.UpdateChecked = false;
                    StartCoroutine(MenuStartup());
                }
                if (GUI.Button(new Rect(bx + bw * 0.52f, by + 40, bw * 0.48f, 44), L.T("upd_skip"), _button))
                {
                    _updater.SkipFailed();
                    StartCoroutine(AutoLogin());
                }
            }
            return true;
        }

        /// <summary>메뉴 위쪽 공지·점검, 오른쪽 아래 버전.</summary>
        void DrawNoticeAndVersion(float w, float h)
        {
            if (!string.IsNullOrEmpty(Session.Notice))
                GUI.Label(new Rect(20, 14, w - 420, 30), "▶ " + Session.Notice, _small);
            if (Session.Maintenance)
                GUI.Label(new Rect(20, 40, w - 420, 30), L.T("upd_maintenance"), _label);
            string v = GameUpdater.ReadLocalVersion();
            GUI.Label(new Rect(w - 220, h - 30, 210, 24), "v" + (string.IsNullOrEmpty(v) ? "dev" : v), _small);
        }

        // ------------------------------------------------------------------
        // 로그인 흐름
        // ------------------------------------------------------------------

        /// <summary>메뉴를 열 때: 저장된 세션으로 자동 로그인 → 파티·최근 경기 이어서.</summary>
        IEnumerator AutoLogin()
        {
            yield return null;
            if (_started) yield break;
            _email = PlayerPrefs.GetString("kw.email", "");
            string token = !string.IsNullOrEmpty(Session.ApiToken) ? Session.ApiToken : PlayerPrefs.GetString("kw.session", "");
            string backend = !string.IsNullOrEmpty(Session.ApiBackend) ? Session.ApiBackend : PlayerPrefs.GetString("kw.session_backend", "");
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(backend)) yield break;

            _api = new BackendClient(backend) { Token = token };
            bool ok = false;
            yield return _api.Get("/me", (code, text) =>
            {
                if (code == 200) { _profile = JsonUtility.FromJson<ProfileDto>(text); ok = true; }
                else if (code == 401) ForgetSession();   // 만료·비밀번호 변경·제재
            });
            if (!ok) yield break;
            Session.ApiToken = token;
            Session.ApiBackend = backend;
            _backendUrl = backend;

            yield return ResumeParty();
        }

        void SaveSession(string token)
        {
            Session.ApiToken = token;
            Session.ApiBackend = _backendUrl.Trim().TrimEnd('/');
            _api = new BackendClient(Session.ApiBackend) { Token = token };
            PlayerPrefs.SetString("kw.session", token);
            PlayerPrefs.SetString("kw.session_backend", Session.ApiBackend);
            PlayerPrefs.SetString("kw.email", _email.Trim());
            PlayerPrefs.SetString("kw.backend", _backendUrl.Trim());
            ErrorReporter.SetBackend(_backendUrl);
            PlayerPrefs.Save();
        }

        void ForgetSession()
        {
            Session.ApiToken = null;
            PlayerPrefs.DeleteKey("kw.session");
            PlayerPrefs.Save();
            _profile = null;
            _party = null;
            if (_api != null) _api.Token = null;
        }

        IEnumerator AccountCall(string path, string body, Action<long, string> done)
        {
            if (_accBusy) yield break;
            _accBusy = true;
            _accMsg = "";
            var api = new BackendClient(_backendUrl.Trim()) { Token = _api?.Token };
            yield return api.Post(path, body, (code, text) =>
            {
                if (code == 0) _accMsg = L.T("st_backend_down");
                else done(code, text ?? "");
            });
            _accBusy = false;
        }

        void OnLoggedIn(string text)
        {
            var r = JsonUtility.FromJson<LoginResponse>(text);
            SaveSession(r.token);
            _profile = r.account;
            _password = _password2 = _code = "";
            _accMsg = "";
            _accMode = AccountMode.Login;
            if (_profile != null && !_profile.emailVerified) _side = SidePanel.Verify;
            StartCoroutine(ResumeParty());
        }

        void DoLogin()
        {
            StartCoroutine(AccountCall("/auth/login", $"{{\"email\":\"{BackendClient.Escape(_email.Trim())}\",\"password\":\"{BackendClient.Escape(_password)}\"}}", (code, text) =>
            {
                if (code == 200) OnLoggedIn(text);
                else if (code == 503) _accMsg = L.T("upd_maintenance");
                else if (code == 401) _accMsg = L.T("acc_wrong");
                else if (code == 423) _accMsg = L.T("acc_locked");
                else if (code == 403)
                {
                    var b = JsonUtility.FromJson<BanInfo>(text);
                    _accMsg = b.until > 0
                        ? L.T("acc_banned_until", DateTimeOffset.FromUnixTimeSeconds(b.until).ToLocalTime().ToString("yyyy-MM-dd HH:mm"), b.reason)
                        : L.T("acc_banned", b.reason);
                }
                else _accMsg = L.T("st_login_failed", code);
            }));
        }

        void DoRegister()
        {
            if (_password != _password2) { _accMsg = L.T("acc_pw_mismatch"); return; }
            if (_password.Length < 8) { _accMsg = L.T("acc_pw_short"); return; }
            if (!_agreeTerms || _terms == null) { _accMsg = L.T("acc_need_terms"); return; }
            if (!_age14) { _accMsg = L.T("acc_need_age"); return; }
            StartCoroutine(AccountCall("/auth/register",
                $"{{\"email\":\"{BackendClient.Escape(_email.Trim())}\",\"password\":\"{BackendClient.Escape(_password)}\",\"name\":\"{BackendClient.Escape(_regName.Trim())}\",\"agreedTerms\":\"{BackendClient.Escape(_terms.termsVersion)}\",\"age14\":true}}", (code, text) =>
                {
                    if (code == 200) OnLoggedIn(text);
                    else if (code == 409) _accMsg = L.T("acc_email_taken");
                    else if (text.Contains("\"terms\"")) { _terms = null; _accMsg = L.T("acc_terms_changed"); }
                    else if (text.Contains("email")) _accMsg = L.T("acc_bad_email");
                    else if (text.Contains("password")) _accMsg = L.T("acc_pw_short");
                    else _accMsg = L.T("st_login_failed", code);
                }));
        }

        void DoResetRequest()
        {
            StartCoroutine(AccountCall("/auth/reset/request", $"{{\"email\":\"{BackendClient.Escape(_email.Trim())}\"}}", (code, text) =>
            {
                _accMode = AccountMode.Reset;
                _accMsg = L.T("acc_reset_sent");
            }));
        }

        void DoReset()
        {
            if (_password != _password2) { _accMsg = L.T("acc_pw_mismatch"); return; }
            if (_password.Length < 8) { _accMsg = L.T("acc_pw_short"); return; }
            StartCoroutine(AccountCall("/auth/reset",
                $"{{\"email\":\"{BackendClient.Escape(_email.Trim())}\",\"code\":\"{BackendClient.Escape(_code.Trim())}\",\"password\":\"{BackendClient.Escape(_password)}\"}}", (code, text) =>
                {
                    if (code == 200) OnLoggedIn(text);
                    else _accMsg = L.T("acc_bad_code");
                }));
        }

        void Logout()
        {
            if (_party != null) LeaveParty();
            ForgetSession();
            _side = SidePanel.None;
        }

        // ------------------------------------------------------------------
        // 옆 패널: 리더보드 / 최근 경기·신고 / 이메일 인증
        // ------------------------------------------------------------------

        void OpenSide(SidePanel p)
        {
            _side = _side == p ? SidePanel.None : p;
            _sideMsg = "";
            _reportTarget = null;
            if (_api == null || string.IsNullOrEmpty(_api.Token)) return;
            if (_side == SidePanel.Leaderboard)
            {
                string region = string.IsNullOrEmpty(_profile?.homeRegion) ? "kr" : _profile.homeRegion;
                StartCoroutine(_api.Get("/leaderboard/" + region, (code, text) =>
                {
                    if (code == 200) _board = JsonUtility.FromJson<LeaderList>("{\"items\":" + text + "}").items;
                }));
            }
            else if (_side == SidePanel.Matches)
            {
                StartCoroutine(_api.Get("/me/matches", (code, text) =>
                {
                    if (code == 200) _matches = JsonUtility.FromJson<MatchSummaryList>("{\"items\":" + text + "}").items;
                }));
                StartCoroutine(_api.Get("/me", (code, text) => { if (code == 200) _profile = JsonUtility.FromJson<ProfileDto>(text); }));
            }
        }

        void SendReport(string matchId, string accountId, string reason)
        {
            _reportTarget = null;
            StartCoroutine(_api.Post("/report", $"{{\"matchId\":\"{matchId}\",\"accountId\":\"{accountId}\",\"reason\":\"{reason}\"}}", (code, text) =>
            {
                _sideMsg = code == 200 ? L.T("rep_done") : L.T("rep_failed");
            }));
        }

        // ------------------------------------------------------------------
        // 화면
        // ------------------------------------------------------------------

        void DrawMainMenu(float w, float h, float x, float y, float pw)
        {
            DrawNoticeAndVersion(w, h);
            if (_profile == null) y = DrawLogin(x, y, pw);
            else y = DrawProfileAndMatch(x, y, pw);
            y += 16;

            if (GUI.Button(new Rect(x, y, pw * 0.48f, 46), L.T("menu_practice"), _button)) StartOffline();
            if (GUI.Button(new Rect(x + pw * 0.52f, y, pw * 0.48f, 46), L.T("menu_host"), _button)) StartServer(NetRole.Host, ParsePort());
            y += 62;

            GUI.Label(new Rect(x, y, pw, 26), L.T("menu_direct"), _label);
            y += 28;
            _address = GUI.TextField(new Rect(x, y, pw - 130, 36), _address, _field);
            _portText = GUI.TextField(new Rect(x + pw - 120, y, 120, 36), _portText, _field);
            y += 44;
            GUI.Label(new Rect(x, y, pw, 26), L.T("menu_lag", (_lag * 2).ToString("0")), _label);
            y += 26;
            _lag = Mathf.Round(GUI.HorizontalSlider(new Rect(x, y, pw, 20), _lag, 0f, 150f));
            y += 26;
            if (GUI.Button(new Rect(x, y, pw, 46), L.T("menu_connect"), _button))
            {
                PlayerPrefs.SetString("kw.address", _address);
                StartClient(_address.Trim(), ParsePort(), _lag, false, "");
            }

            // 개발용: 백엔드 주소 (출시 빌드에서는 고정 주소)
            GUI.Label(new Rect(10, h - 64, 110, 26), L.T("menu_backend"), _small);
            GUI.enabled = _profile == null;
            _backendUrl = GUI.TextField(new Rect(120, h - 66, 340, 28), _backendUrl);
            GUI.enabled = true;

            if (_profile != null) DrawSide(x - 360f, 80f + h * 0.08f, 330f, h);
        }

        float DrawLogin(float x, float y, float pw)
        {
            GUI.enabled = !_accBusy;
            switch (_accMode)
            {
                case AccountMode.Login:
                    y = Field(x, y, pw, L.T("acc_email"), ref _email, false);
                    y = Field(x, y, pw, L.T("acc_password"), ref _password, true);
                    if (GUI.Button(new Rect(x, y, pw, 52), L.T("acc_login"), _button) || EnterPressed()) DoLogin();
                    y += 60;
                    if (GUI.Button(new Rect(x, y, pw * 0.48f, 38), L.T("acc_register"), _button)) { _accMode = AccountMode.Register; _accMsg = ""; }
                    if (GUI.Button(new Rect(x + pw * 0.52f, y, pw * 0.48f, 38), L.T("acc_forgot"), _button)) { _accMode = AccountMode.ResetRequest; _accMsg = ""; }
                    y += 46;
                    break;

                case AccountMode.Register:
                    y = Field(x, y, pw, L.T("acc_email"), ref _email, false);
                    y = Field(x, y, pw, L.T("acc_name"), ref _regName, false);
                    y = Field(x, y, pw, L.T("acc_password"), ref _password, true);
                    y = Field(x, y, pw, L.T("acc_password2"), ref _password2, true);
                    y = DrawTermsChecks(x, y, pw, true);
                    if (GUI.Button(new Rect(x, y, pw, 52), L.T("acc_register_do"), _button)) DoRegister();
                    y += 60;
                    if (GUI.Button(new Rect(x, y, pw, 34), L.T("acc_back"), _button)) { _accMode = AccountMode.Login; _accMsg = ""; }
                    y += 42;
                    break;

                case AccountMode.ResetRequest:
                    y = Field(x, y, pw, L.T("acc_email"), ref _email, false);
                    if (GUI.Button(new Rect(x, y, pw, 48), L.T("acc_reset_send"), _button)) DoResetRequest();
                    y += 56;
                    if (GUI.Button(new Rect(x, y, pw, 34), L.T("acc_back"), _button)) { _accMode = AccountMode.Login; _accMsg = ""; }
                    y += 42;
                    break;

                case AccountMode.Reset:
                    y = Field(x, y, pw, L.T("acc_code"), ref _code, false);
                    y = Field(x, y, pw, L.T("acc_new_password"), ref _password, true);
                    y = Field(x, y, pw, L.T("acc_password2"), ref _password2, true);
                    if (GUI.Button(new Rect(x, y, pw, 48), L.T("acc_reset_do"), _button)) DoReset();
                    y += 56;
                    if (GUI.Button(new Rect(x, y, pw, 34), L.T("acc_back"), _button)) { _accMode = AccountMode.Login; _accMsg = ""; }
                    y += 42;
                    break;
            }
            GUI.enabled = true;
            if (!string.IsNullOrEmpty(_accMsg))
            {
                GUI.Label(new Rect(x, y, pw, 48), _accMsg, _label);
                y += 50;
            }
            return y;
        }

        float Field(float x, float y, float pw, string label, ref string value, bool password)
        {
            GUI.Label(new Rect(x, y, 130, 30), label, _label);
            var rc = new Rect(x + 130, y, pw - 130, 34);
            value = password ? GUI.PasswordField(rc, value, '●', 128, _field) : GUI.TextField(rc, value, 254, _field);
            return y + 42;
        }

        static bool EnterPressed()
        {
            var e = Event.current;
            return e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter);
        }

        float DrawProfileAndMatch(float x, float y, float pw)
        {
            var p = _profile;
            if (!p.termsOk)
            {
                // 약관이 바뀌었으면 다시 동의해야 매칭 가능
                GUI.Label(new Rect(x, y, pw, 30), p.displayId, _label);
                y += 34;
                GUI.Label(new Rect(x, y, pw, 48), L.T("acc_terms_updated"), _small);
                y += 52;
                GUI.enabled = !_accBusy;
                y = DrawTermsChecks(x, y, pw, false);
                if (GUI.Button(new Rect(x, y, pw, 48), L.T("acc_agree_do"), _button)) DoAgreeTerms();
                y += 56;
                if (GUI.Button(new Rect(x, y, pw, 34), L.T("acc_logout"), _button)) Logout();
                GUI.enabled = true;
                y += 42;
                if (!string.IsNullOrEmpty(_accMsg)) { GUI.Label(new Rect(x, y, pw, 40), _accMsg, _small); y += 44; }
                return y;
            }
            GUI.Label(new Rect(x, y, pw, 30), p.displayId, _label);
            y += 30;
            string rank = L.T("acc_rank", TierName(p.tier), p.mmr, p.wins, p.losses);
            GUI.Label(new Rect(x, y, pw, 26), rank, _small);
            y += 30;
            if (GUI.Button(new Rect(x, y, pw * 0.32f, 32), L.T("menu_board"))) OpenSide(SidePanel.Leaderboard);
            if (GUI.Button(new Rect(x + pw * 0.34f, y, pw * 0.32f, 32), L.T("menu_recent"))) OpenSide(SidePanel.Matches);
            if (GUI.Button(new Rect(x + pw * 0.68f, y, pw * 0.32f, 32), L.T("acc_logout"))) { Logout(); return y + 40; }
            y += 44;

            bool partyMember = _party != null && _party.inParty && !_party.isLeader;
            if (partyMember) GUI.Label(new Rect(x, y, pw, 56), L.T("party_wait_leader"), _label);
            else if (GUI.Button(new Rect(x, y, pw, 56), _party != null && _party.inParty ? L.T("party_match") : L.T("menu_match"), _button))
                StartCoroutine(Matchmake(_backendUrl.Trim(), p.name, "", _lag, false));
            return y + 64;
        }

        public static string TierName(int tier) => tier < 0 ? L.T("tier_placement") : L.T("tier_" + Mathf.Clamp(tier, 0, 6));

        void DrawSide(float sx, float sy, float sw, float h)
        {
            if (_side == SidePanel.None || sx < 10f) return;
            float sh = h - sy - 80f;
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(sx - 8, sy - 8, sw + 16, sh + 16), Texture2D.whiteTexture);
            GUI.color = old;
            float y = sy;

            if (_side == SidePanel.Verify)
            {
                GUI.Label(new Rect(sx, y, sw, 60), L.T("acc_verify_info", _profile.email), _small);
                y += 64;
                _code = GUI.TextField(new Rect(sx, y, sw - 110, 32), _code, 6, _field);
                if (GUI.Button(new Rect(sx + sw - 100, y, 100, 32), L.T("ok")))
                    StartCoroutine(_api.Post("/auth/verify", $"{{\"code\":\"{BackendClient.Escape(_code.Trim())}\"}}", (code, text) =>
                    {
                        if (code == 200) { _profile = JsonUtility.FromJson<ProfileDto>(text); _side = SidePanel.None; _code = ""; }
                        else _sideMsg = L.T("acc_bad_code");
                    }));
                y += 40;
                if (GUI.Button(new Rect(sx, y, sw * 0.48f, 30), L.T("acc_resend")))
                    StartCoroutine(_api.Post("/auth/verify/resend", "{}", (code, text) => _sideMsg = L.T("acc_resent")));
                if (GUI.Button(new Rect(sx + sw * 0.52f, y, sw * 0.48f, 30), L.T("acc_later"))) _side = SidePanel.None;
                y += 38;
            }
            else if (_side == SidePanel.Leaderboard)
            {
                GUI.Label(new Rect(sx, y, sw, 28), L.T("menu_board") + " · " + (_profile.homeRegion ?? "").ToUpperInvariant(), _label);
                y += 32;
                if (_board == null || _board.Length == 0) GUI.Label(new Rect(sx, y, sw, 26), L.T("board_empty"), _small);
                else
                    for (int i = 0; i < _board.Length && i < 20 && y < sy + sh - 24; i++)
                    {
                        var b = _board[i];
                        GUI.Label(new Rect(sx, y, sw, 24), $"{b.rank,2}. {b.displayId}   {TierName(b.tier)} {b.mmr}", _small);
                        y += 24;
                    }
            }
            else if (_side == SidePanel.Matches)
            {
                GUI.Label(new Rect(sx, y, sw, 28), L.T("menu_recent"), _label);
                y += 32;
                if (_matches == null || _matches.Length == 0) GUI.Label(new Rect(sx, y, sw, 26), L.T("recent_empty"), _small);
                else
                {
                    var m = _matches[0];
                    string res = m.winnerTeam == -2 ? L.T("recent_void") : m.winnerTeam < 0 ? L.T("result_draw") : m.winnerTeam == m.myTeam ? L.T("result_win") : L.T("result_lose");
                    string delta = m.mmrChange > 0 ? "+" + m.mmrChange : m.mmrChange.ToString();
                    GUI.Label(new Rect(sx, y, sw, 26), $"{res}  ({delta} MMR)", _label);
                    y += 30;
                    if (m.players != null)
                        foreach (var pl in m.players)
                        {
                            if (y > sy + sh - 30) break;
                            string line = $"{(pl.team == m.myTeam ? "■" : "□")} {pl.name}  {pl.kills}/{pl.deaths}";
                            GUI.Label(new Rect(sx, y, sw - 70, 24), line, _small);
                            if (!pl.me && GUI.Button(new Rect(sx + sw - 64, y, 64, 22), L.T("rep_btn"))) _reportTarget = _reportTarget == pl.id ? null : pl.id;
                            y += 26;
                            if (_reportTarget == pl.id)
                            {
                                float bw = (sw - 8) / ReportReasons.Length;
                                for (int k = 0; k < ReportReasons.Length; k++)
                                    if (GUI.Button(new Rect(sx + k * (bw + 2), y, bw, 26), L.T("rep_" + ReportReasons[k])))
                                        SendReport(m.id, pl.id, ReportReasons[k]);
                                y += 30;
                            }
                        }
                    if (_matches.Length > 1)
                    {
                        y += 6;
                        for (int i = 1; i < _matches.Length && y < sy + sh - 24; i++)
                        {
                            var o = _matches[i];
                            string r2 = o.winnerTeam == -2 ? L.T("recent_void") : o.winnerTeam < 0 ? L.T("result_draw") : o.winnerTeam == o.myTeam ? L.T("result_win") : L.T("result_lose");
                            GUI.Label(new Rect(sx, y, sw, 22), $"{DateTimeOffset.FromUnixTimeSeconds(o.createdAt).ToLocalTime():MM-dd HH:mm}  {r2}  {(o.mmrChange > 0 ? "+" : "")}{o.mmrChange}", _small);
                            y += 22;
                        }
                    }
                }
            }
            if (!string.IsNullOrEmpty(_sideMsg)) GUI.Label(new Rect(sx, sy + sh - 26, sw, 26), _sideMsg, _small);
        }
    }

    [Serializable] public sealed class ProfileDto
    {
        public string id; public string name; public string tag; public string displayId; public string email; public bool emailVerified;
        public string homeRegion; public int mmr; public int tier; public int wins; public int losses; public int matches; public int kills; public int deaths;
        public bool termsOk = true;
    }
    [Serializable] public sealed class BanInfo { public string error; public string reason; public long until; }
    [Serializable] public sealed class LeaderDto { public int rank; public string name; public string displayId; public int mmr; public int tier; public int wins; public int matches; }
    [Serializable] public sealed class LeaderList { public LeaderDto[] items; }
    [Serializable] public sealed class MatchPlayerDto { public string id; public string name; public int team; public int kills; public int deaths; public bool me; }
    [Serializable] public sealed class MatchSummaryDto { public string id; public string region; public long createdAt; public long endedAt; public int winnerTeam; public int myTeam; public int mmrChange; public MatchPlayerDto[] players; }
    [Serializable] public sealed class MatchSummaryList { public MatchSummaryDto[] items; }
}
