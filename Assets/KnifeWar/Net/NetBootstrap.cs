using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KnifeWar.Game;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KnifeWar.Net
{
    /// <summary>
    /// 게임 시작 방식 결정.
    /// 명령줄 -kw "..." 또는 실행 파일 옆 kw_launch.txt:
    ///   offline
    ///   host [포트]
    ///   server [포트] [backend URL secret 값 region kr address 공개주소]   ← 데디 서버
    ///   connect 주소 [포트] [lag ms] [bot]                                 ← 직접 접속
    ///   match [backend URL] [name 이름] [id 접미사] [lag ms] [bot]          ← 온라인 매칭 (자동)
    ///   공통 옵션: rounds N (N선승, 테스트용 짧은 경기), tick 64|128 (서버 틱. 클라는 접속한 서버를 자동으로 따름)
    /// 데디 서버 빌드는 아무 인자 없으면 server로 시작. 그 외에는 시작 메뉴.
    ///
    /// 끊김 처리 (발로란트식):
    ///   플레이 중 끊김 → 60초 동안 자동 재접속 (같은 입장 토큰 → 같은 칸으로 복귀, 그동안 봇이 대신 뜀)
    ///   게임이 꺼졌다 다시 켜도 "온라인 매칭"을 누르면 진행 중인 경기로 돌아감 (백엔드가 기억)
    ///   경기 종료·거부(버전 다름, 끝난 경기)·나가기 → 메뉴로, 이유 표시
    /// </summary>
    public sealed partial class NetBootstrap : MonoBehaviour
    {
        public const string DefaultBackend = "http://127.0.0.1:8080";

        MatchController _m;
        bool _started;
        string _address = "127.0.0.1";
        string _portText = NetProtocol.DefaultPort.ToString();
        string _backendUrl = DefaultBackend;
        string _playerName = "Player";
        float _lag;
        GUIStyle _title, _label, _button, _field, _small;

        // 매칭 상태
        bool _searching;
        string _searchText = "";
        BackendClient _api;

        /// <summary>씬을 다시 불러도 유지되는 클라 접속 정보 (재접속용).</summary>
        static class Session
        {
            public static bool Has;
            public static string Address;
            public static ushort Port;
            public static string Token;
            public static float Lag;
            public static bool Bot;
            public static float ReconnectDeadline = -1f;
            public static bool PendingConnect;
            public static string MenuMessage;
            public static bool LaunchUsed;
            public static string ApiToken, ApiBackend;     // 로그인 유지 (씬을 다시 불러도)
            public static Dictionary<string, int> Pings;
            public static float PingsAt;
            public static string PingText = "";
            public static bool ShowRecentAfterMatch;
            public static bool UpdateChecked;
            public static string Notice = "";
            public static bool Maintenance;

            public static void Clear()
            {
                Has = false;
                PendingConnect = false;
                ReconnectDeadline = -1f;
                Token = null;
            }
        }

        const float ReconnectWindowSec = 60f;     // 플레이 중 끊기면 이 시간 동안 재접속 시도
        const float MatchJoinWindowSec = 45f;     // 매칭 직후 서버가 아직 준비 전이면 이 시간 동안 재시도

        NetClient _client;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            SceneManager.sceneLoaded += (s, _) => Attach();
            Attach();
        }

        static void Attach()
        {
            var m = FindAnyObjectByType<MatchController>();
            if (m == null || m.GetComponent<NetBootstrap>() != null) return;
            m.gameObject.AddComponent<NetBootstrap>();
        }

        void Start()
        {
            _m = GetComponent<MatchController>();
            _address = PlayerPrefs.GetString("kw.address", _address);
            _backendUrl = PlayerPrefs.GetString("kw.backend", _backendUrl);
            _playerName = PlayerPrefs.GetString("kw.name", Environment.UserName ?? "Player");
            if (!Application.isEditor) BuildLog.Install();
            MatchController.LeaveHandler = null;
            NetInfo.Reset();

            // 재접속 대기 중이면 바로 다시 접속
            if (Session.PendingConnect && Session.Has)
            {
                Session.PendingConnect = false;
                _started = true;
                StartCoroutine(ReconnectAfter(1.5f));
                return;
            }
            if (!string.IsNullOrEmpty(Session.MenuMessage))
            {
                _searchText = Session.MenuMessage;
                Session.MenuMessage = null;
            }
            StartCoroutine(MenuStartup());

            string launch = ReadLaunchSpec();
            bool headless = Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
#if UNITY_SERVER
            headless = true;
#endif
            if (string.IsNullOrWhiteSpace(launch) && headless) launch = "server";
            // 서버는 매 경기 씬을 다시 불러도 계속 서버로. 클라 자동 실행(테스트)은 처음 한 번만.
            bool isServer = !string.IsNullOrWhiteSpace(launch) && launch.TrimStart().StartsWith("server", StringComparison.OrdinalIgnoreCase);
            if (!isServer && Session.LaunchUsed) launch = null;
            if (!string.IsNullOrWhiteSpace(launch))
            {
                Session.LaunchUsed = true;
                Launch(launch);
            }
        }

        void Update()
        {
            if (_client == null) return;
            if (_client.Connected)
            {
                Session.ReconnectDeadline = -1f;   // 접속 성공 → 다음 끊김 때 다시 60초
                return;
            }
            if (Session.ReconnectDeadline > 0f && _m.Role == NetRole.None)
            {
                int left = Mathf.Max(0, Mathf.CeilToInt(Session.ReconnectDeadline - Time.realtimeSinceStartup));
                NetInfo.Status = L.T("st_reconnecting", left);
            }
        }

        IEnumerator ReconnectAfter(float delay)
        {
            int left = Mathf.Max(0, Mathf.CeilToInt(Session.ReconnectDeadline - Time.realtimeSinceStartup));
            NetInfo.Status = L.T("st_reconnecting", left);
            yield return new WaitForSecondsRealtime(delay);
            StartClient(Session.Address, Session.Port, Session.Lag, Session.Bot, Session.Token);
        }

        /// <summary>접속이 끝났을 때: 재접속 / 메뉴로 (이유 표시).</summary>
        void OnClientEnded(NetClient client, ClientEnd why)
        {
            if (client != _client) return;
            float now = Time.realtimeSinceStartup;
            switch (why)
            {
                case ClientEnd.Left:
                    Session.Clear();
                    BackToMenu(null);
                    return;

                case ClientEnd.MatchOver:
                {
                    Session.Clear();
                    var me = _m.Human;
                    int winner = _m.Round.MatchWinner;
                    string result = winner < 0 || me == null ? L.T("result_draw") : (winner == me.Team ? L.T("result_win") : L.T("result_lose"));
                    Session.ShowRecentAfterMatch = true;
                    BackToMenu(L.T("st_match_over", result));
                    return;
                }

                case ClientEnd.Rejected:
                {
                    Session.Clear();
                    string msg;
                    switch (client.LastReject)
                    {
                        case RejectReason.Version: msg = L.T("st_rej_version"); break;
                        case RejectReason.BadToken: msg = L.T("st_rej_over"); break;
                        case RejectReason.Full: msg = L.T("st_full"); break;
                        default: msg = L.T("st_connect_failed"); break;
                    }
                    BackToMenu(msg);
                    return;
                }

                default:   // Lost, ConnectFailed, NotReady → 정해진 시간 안이면 재시도
                {
                    bool matchmade = !string.IsNullOrEmpty(Session.Token);
                    if (Session.ReconnectDeadline < 0f)
                    {
                        float window = why == ClientEnd.Lost ? ReconnectWindowSec : (matchmade ? MatchJoinWindowSec : 0f);
                        Session.ReconnectDeadline = now + window;
                    }
                    if (now < Session.ReconnectDeadline)
                    {
                        Session.PendingConnect = true;
                        ReloadScene();
                    }
                    else
                    {
                        bool wasPlaying = why == ClientEnd.Lost || _m.Role != NetRole.None;
                        Session.Clear();
                        BackToMenu(wasPlaying || matchmade ? L.T("st_reconnect_failed") : L.T("st_connect_failed"));
                    }
                    return;
                }
            }
        }

        void BackToMenu(string message)
        {
            Session.MenuMessage = message;
            ReloadScene();
        }

        static void ReloadScene()
        {
            PlayerPrefs.Save();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        string ReadLaunchSpec()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-kw") return args[i + 1];
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "-server") return "server";
            if (Application.isEditor) return null;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "kw_launch.txt");
                if (File.Exists(path)) return File.ReadAllText(path).Trim();
            }
            catch { }
            return null;
        }

        void Launch(string spec)
        {
            Debug.Log($"[KnifeWar] launch: {spec}");
            var t = spec.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            string mode = t.Length > 0 ? t[0].ToLowerInvariant() : "offline";
            ushort port = NetProtocol.DefaultPort;
            string address = "127.0.0.1", backend = null, secret = null, region = "kr", publicAddr = "127.0.0.1", name = null, idSuffix = "";
            float lag = 0f;
            bool bot = false;
            int rounds = 0, tick = 0;
            for (int i = 1; i < t.Length; i++)
            {
                string k = t[i].ToLowerInvariant();
                string next = i + 1 < t.Length ? t[i + 1] : null;
                if (k == "lag" && next != null) { float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out lag); i++; }
                else if (k == "bot") bot = true;
                else if (k == "backend" && next != null) { backend = next; i++; }
                else if (k == "secret" && next != null) { secret = next; i++; }
                else if (k == "region" && next != null) { region = next; i++; }
                else if (k == "address" && next != null) { publicAddr = next; i++; }
                else if (k == "name" && next != null) { name = next; i++; }
                else if (k == "id" && next != null) { idSuffix = next; i++; }
                else if (k == "rounds" && next != null) { int.TryParse(next, out rounds); i++; }
                else if (k == "tick" && next != null) { int.TryParse(next, out tick); i++; }
                else if (ushort.TryParse(k, out var p)) port = p;
                else if (mode == "connect") address = t[i];
            }

            if (tick > 0 && !_m.SetTickRate(tick)) Debug.LogWarning($"[KnifeWar] 지원하지 않는 틱 {tick} (64 또는 128)");
            if (rounds > 0) _m.ConfigureRounds(rounds);
            switch (mode)
            {
                case "server":
                    if (backend != null) StartBackendServer(port, backend, secret, region, publicAddr);
                    else StartServer(NetRole.Server, port);
                    break;
                case "host": StartServer(NetRole.Host, port); break;
                case "connect": StartClient(address, port, lag, bot, ""); break;
                case "match":
                    StartCoroutine(Matchmake(backend ?? DefaultBackend, name ?? _playerName, idSuffix, lag, bot, allowDev: true));
                    break;
                default: StartOffline(); break;
            }
        }

        void StartOffline()
        {
            _started = true;
            _m.Begin(NetRole.Offline, 0);
        }

        void StartServer(NetRole role, ushort port)
        {
            _started = true;
            if (role == NetRole.Server) Application.targetFrameRate = MatchController.TickRate;   // 서버는 틱 수만큼만 돌린다 (CPU 절약 = 서버비 절약)
            _m.Begin(role, role == NetRole.Host ? 0 : -1);
            var server = gameObject.AddComponent<NetServer>();
            server.StartServer(_m, port);
        }

        /// <summary>데디 서버 + 백엔드: 경기 배정을 받은 뒤에 경기를 시작한다.</summary>
        void StartBackendServer(ushort port, string backend, string secret, string region, string publicAddr)
        {
            _started = true;
            Application.targetFrameRate = MatchController.TickRate;   // 서버는 틱 수만큼만 돌린다 (CPU 절약 = 서버비 절약)
            var server = gameObject.AddComponent<NetServer>();
            if (!server.StartServer(_m, port)) return;
            gameObject.AddComponent<GameServerAgent>().Run(_m, server, backend, secret, region, publicAddr, port);
        }

        void StartClient(string address, ushort port, float lag, bool bot, string joinToken)
        {
            _started = true;
            Session.Has = true;
            Session.Address = address;
            Session.Port = port;
            Session.Lag = lag;
            Session.Bot = bot;
            Session.Token = joinToken;
            _client = gameObject.AddComponent<NetClient>();
            _client.Ended += OnClientEnded;
            var c = _client;
            MatchController.LeaveHandler = () => c.Leave();
            c.Connect(_m, address, port, lag, bot, joinToken);
        }

        // ------------------------------------------------------------------
        // 온라인 매칭: 로그인 → 리전 핑 → 대기열 → 배정된 서버로 접속
        // ------------------------------------------------------------------

        /// <summary>로그인 (스팀 연동 전에는 기기 id). 이미 로그인돼 있으면 건너뜀. 성공하면 _api.Token이 채워진다.</summary>
        IEnumerator EnsureLogin(string backendUrl, string name, string idSuffix, bool allowDev = false)
        {
            backendUrl = backendUrl.TrimEnd('/');
            if (_api == null || _api.BaseUrl != backendUrl) _api = new BackendClient(backendUrl);
            if (string.IsNullOrEmpty(_api.Token) && Session.ApiBackend == backendUrl && !string.IsNullOrEmpty(Session.ApiToken))
                _api.Token = Session.ApiToken;
            if (!string.IsNullOrEmpty(_api.Token)) yield break;
            if (!allowDev)
            {
                _searchText = L.T("acc_need_login");   // 자체 계정으로 로그인해야 온라인 이용 가능
                yield break;
            }

            string deviceId = SystemInfo.deviceUniqueIdentifier + (string.IsNullOrEmpty(idSuffix) ? "" : "-" + idSuffix);
            _searchText = L.T("st_logging_in");
            yield return _api.Post("/auth/dev", $"{{\"deviceId\":\"{BackendClient.Escape(deviceId)}\",\"name\":\"{BackendClient.Escape(name)}\"}}", (code, text) =>
            {
                if (code == 200)
                {
                    _api.Token = JsonUtility.FromJson<LoginResponse>(text).token;
                    Session.ApiToken = _api.Token;
                    Session.ApiBackend = backendUrl;
                    _searchText = "";
                }
                else _searchText = code == 0 ? L.T("st_backend_down") : L.T("st_login_failed", code);
            });
        }

        /// <summary>리전별 핑 (5분 동안 재사용).</summary>
        IEnumerator EnsurePings(float lag)
        {
            if (Session.Pings != null && Time.realtimeSinceStartup - Session.PingsAt < 300f) yield break;
            _searchText = L.T("st_pinging");
            RegionDto[] regions = null;
            yield return _api.Get("/regions", (code, text) =>
            {
                if (code == 200) regions = JsonUtility.FromJson<RegionList>("{\"items\":" + text + "}").items;
            });
            var pings = new Dictionary<string, int>();
            if (regions != null)
                foreach (var r in regions)
                {
                    if (string.IsNullOrEmpty(r.pingUrl)) continue;
                    int ms = -1;
                    yield return BackendClient.MeasurePing(r.pingUrl, v => ms = v);
                    if (ms >= 0) pings[r.id] = Math.Max(1, ms + (int)(lag * 2));
                }
            Session.Pings = pings;
            Session.PingsAt = Time.realtimeSinceStartup;
            string text2 = "";
            foreach (var kv in pings) text2 += $"{kv.Key} {kv.Value}ms  ";
            Session.PingText = text2;
            Debug.Log($"[KnifeWar] 리전 핑: {text2}");
        }

        /// <summary>온라인 매칭: 로그인 → 핑 → 대기열 (파티면 파티 전체, 파티장만) → 매칭되면 접속.</summary>
        IEnumerator Matchmake(string backendUrl, string name, string idSuffix, float lag, bool bot, bool allowDev = false)
        {
            _searching = true;
            yield return EnsureLogin(backendUrl, name, idSuffix, allowDev);
            if (string.IsNullOrEmpty(_api.Token)) { _searching = false; yield break; }
            yield return EnsurePings(lag);

            bool ok = false;
            long errCode = 0;
            yield return _api.Post("/mm/join", BackendClient.PingsJson(Session.Pings), (code, text) => { ok = code == 200; errCode = code; _lastError = text ?? ""; });
            if (!ok)
            {
                _searchText = errCode == 403 ? (_lastError.Contains("verify_email") ? L.T("acc_verify_needed") : L.T("party_leader_only")) : L.T("st_queue_failed");
                _searching = false;
                yield break;
            }
            yield return WaitForMatch(lag, bot);
        }

        /// <summary>매칭될 때까지 상태 확인 (파티원은 파티장이 시작한 검색을 이걸로 기다린다).</summary>
        IEnumerator WaitForMatch(float lag, bool bot)
        {
            _searching = true;
            MatchDto match = null;
            bool stopped = false;
            while (_searching && match == null && !stopped)
            {
                yield return _api.Get("/mm/status", (code, text) =>
                {
                    if (code != 200) return;
                    var s = JsonUtility.FromJson<StatusResponse>(text);
                    if (s.state == "matched" && s.match != null && !string.IsNullOrEmpty(s.match.address))
                    {
                        match = s.match;
                        if (s.rejoin) _searchText = L.T("st_rejoining");
                    }
                    else if (s.state == "searching")
                    {
                        string extra = s.status == "waiting_server" ? L.T("st_waiting_server") : "";
                        if (s.party) extra += L.T("party_tag");
                        _searchText = L.T("st_searching", s.waited / 60, s.waited % 60, extra) + "\n" + Session.PingText;
                    }
                    else
                    {
                        stopped = true;   // 취소됨 (파티원이 취소했거나 파티가 바뀜)
                        _searchText = L.T("st_search_cancelled");
                    }
                });
                if (match == null && !stopped) yield return new WaitForSecondsRealtime(1f);
            }
            _searching = false;
            if (match == null) yield break;

            Debug.Log($"[KnifeWar] 매칭 완료 match={match.matchId} region={match.region} server={match.address}:{match.port} slot={match.slot}");
            StartClient(match.address, (ushort)match.port, lag, bot, match.joinToken);
        }

        void CancelSearch()
        {
            _searching = false;
            if (_api != null && !string.IsNullOrEmpty(_api.Token)) StartCoroutine(_api.Post("/mm/leave", "{}", null));
            _searchText = "";
        }

        // ------------------------------------------------------------------
        // 파티 (친구와 같이 매칭)
        // ------------------------------------------------------------------

        PartyDto _party;
        string _partyCodeInput = "";
        bool _partyBusy;
        Coroutine _partyLoop;

        IEnumerator PartyAction(string path, string body)
        {
            if (_partyBusy) yield break;
            _partyBusy = true;
            PlayerPrefs.SetString("kw.name", _playerName);
            PlayerPrefs.SetString("kw.backend", _backendUrl);
            yield return EnsureLogin(_backendUrl.Trim(), _playerName.Trim(), "");
            if (string.IsNullOrEmpty(_api?.Token)) { _partyBusy = false; yield break; }
            yield return EnsurePings(_lag);
            _searchText = "";
            yield return _api.Post(path, body, (code, text) =>
            {
                if (code == 200) _party = JsonUtility.FromJson<PartyDto>(text);
                else if (code == 404) _searchText = L.T("party_not_found");
                else if (code == 409) _searchText = text.Contains("full") ? L.T("party_full") : L.T("party_busy");
                else _searchText = code == 0 ? L.T("st_backend_down") : L.T("st_login_failed", code);
            });
            if (_party != null && _party.inParty) yield return _api.Post("/party/pings", BackendClient.PingsJson(Session.Pings), null);
            _partyBusy = false;
            StartPartyLoop();
        }

        /// <summary>경기 끝나고 메뉴로 돌아오면 파티를 이어서 보여준다.</summary>
        IEnumerator ResumeParty()
        {
            yield return null;
            if (_started) yield break;
            _api = new BackendClient(Session.ApiBackend) { Token = Session.ApiToken };
            yield return _api.Get("/party", (code, text) =>
            {
                if (code == 200) _party = JsonUtility.FromJson<PartyDto>(text);
            });
            StartPartyLoop();
        }

        void StartPartyLoop()
        {
            if (_partyLoop == null && _party != null && _party.inParty) _partyLoop = StartCoroutine(PartyLoop());
        }

        /// <summary>파티 상태를 주기적으로 확인. 파티원은 파티장이 검색을 시작하면 따라서 기다린다.</summary>
        IEnumerator PartyLoop()
        {
            while (!_started && _party != null && _party.inParty)
            {
                yield return _api.Get("/party", (code, text) =>
                {
                    if (code == 200) _party = JsonUtility.FromJson<PartyDto>(text);
                    else if (code == 401) _party = null;
                });
                if (_party == null || !_party.inParty) break;
                if (!_party.isLeader && !_searching && (_party.searchState == "searching" || _party.searchState == "matched"))
                    StartCoroutine(WaitForMatch(_lag, false));
                yield return new WaitForSecondsRealtime(1.5f);
            }
            _partyLoop = null;
        }

        void LeaveParty()
        {
            if (_api != null && !string.IsNullOrEmpty(_api.Token)) StartCoroutine(_api.Post("/party/leave", "{}", null));
            _party = null;
        }

        // ------------------------------------------------------------------
        // 시작 메뉴
        // ------------------------------------------------------------------

        void OnGUI()
        {
            SkinSet.ApplyFont();
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                _label = new GUIStyle(GUI.skin.label) { fontSize = 18 };
                _button = new GUIStyle(GUI.skin.button) { fontSize = 20 };
                _field = new GUIStyle(GUI.skin.textField) { fontSize = 20 };
                _small = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                _small.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
                _title.normal.textColor = _label.normal.textColor = Color.white;
            }

            float w = Screen.width, h = Screen.height;
            if (_started)
            {
                string s = NetInfo.Status;
                if (NetInfo.PingMs >= 0f) s += "  ·  " + L.T("st_ping", NetInfo.PingMs.ToString("0"));
                if (_m.Role != NetRole.None) s += $"  ·  {MatchController.TickRate} tick";
                if (!string.IsNullOrEmpty(s)) GUI.Label(new Rect(10, h - 28, 900, 24), s, _label);
                if (_m.Role == NetRole.None) Dim(w, h, NetInfo.Status);
                return;
            }

            Dim(w, h, null);
            if (DrawUpdater(w, h)) return;
            for (int i = 0; i < L.LangNames.Length; i++)
            {
                bool cur = (int)L.Current == i;
                if (GUI.Toggle(new Rect(w - 4 * 92 - 10 + i * 92, 10, 88, 30), cur, L.LangNames[i], GUI.skin.button) && !cur) L.Set((Lang)i);
            }
            float pw = 480, x = w * 0.5f - pw * 0.5f, y = h * 0.08f;
            GUI.Label(new Rect(0, y, w, 60), L.T("title"), _title);
            y += 80;
            DrawParty(x + pw + 30, y, Mathf.Min(320f, w - (x + pw + 40)));

            if (_searching || !string.IsNullOrEmpty(_searchText))
            {
                GUI.Label(new Rect(x, y, pw, 90), _searchText, _label);
                y += 96;
                if (_searching && GUI.Button(new Rect(x, y, pw, 48), L.T("cancel"), _button)) CancelSearch();
                else if (!_searching && GUI.Button(new Rect(x, y, pw, 48), L.T("ok"), _button)) _searchText = "";
                return;
            }

            DrawMainMenu(w, h, x, y, pw);
        }

        void DrawParty(float px, float py, float pw)
        {
            if (pw < 200f || _profile == null) return;
            GUI.Label(new Rect(px, py, pw, 30), L.T("party"), _label);
            py += 34;
            if (_party == null || !_party.inParty)
            {
                GUI.enabled = !_partyBusy && !_searching;
                if (GUI.Button(new Rect(px, py, pw, 42), L.T("party_create"), _button))
                    StartCoroutine(PartyAction("/party/create", "{}"));
                py += 52;
                _partyCodeInput = GUI.TextField(new Rect(px, py, pw - 110, 38), _partyCodeInput.ToUpperInvariant(), 8, _field);
                if (GUI.Button(new Rect(px + pw - 100, py, 100, 38), L.T("party_join"), _button) && _partyCodeInput.Trim().Length > 0)
                    StartCoroutine(PartyAction("/party/join", $"{{\"code\":\"{BackendClient.Escape(_partyCodeInput.Trim())}\"}}"));
                GUI.enabled = true;
                return;
            }

            GUI.Label(new Rect(px, py, pw, 30), L.T("party_code", _party.code), _label);
            py += 34;
            if (_party.members != null)
                foreach (var m in _party.members)
                {
                    string tag = (m.leader ? "★ " : "   ") + m.name + (m.me ? " (" + L.T("me") + ")" : "");
                    GUI.Label(new Rect(px, py, pw - 50, 28), tag, _label);
                    if (_party.isLeader && !m.me && !_searching && GUI.Button(new Rect(px + pw - 44, py, 44, 26), "X"))
                        StartCoroutine(_api.Post("/party/kick", $"{{\"accountId\":\"{m.id}\"}}", null));
                    py += 30;
                }
            py += 8;
            GUI.enabled = !_searching;
            if (GUI.Button(new Rect(px, py, pw, 40), L.T("party_leave"), _button)) LeaveParty();
            GUI.enabled = true;
        }

        void Dim(float w, float h, string text)
        {
            var old = GUI.color;
            GUI.color = new Color(0.05f, 0.06f, 0.09f, 0.94f);
            GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
            GUI.color = old;
            if (!string.IsNullOrEmpty(text)) GUI.Label(new Rect(0, h * 0.4f, w, 60), text, _title);
        }

        ushort ParsePort() => ushort.TryParse(_portText, out var p) ? p : NetProtocol.DefaultPort;
    }

    /// <summary>빌드 실행 시 로그를 실행 파일 옆 kw_log_(프로세스번호).txt에 남긴다 (문제 확인용).</summary>
    static class BuildLog
    {
        static string _path;
        static readonly object Lock = new object();

        public static void Install()
        {
            if (_path != null) return;
            try
            {
                _path = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", $"kw_log_{System.Diagnostics.Process.GetCurrentProcess().Id}.txt");
                File.WriteAllText(_path, $"{DateTime.Now:HH:mm:ss} start\n");
                Application.logMessageReceivedThreaded += (msg, stack, type) =>
                {
                    lock (Lock)
                    {
                        try
                        {
                            string extra = type == LogType.Exception || type == LogType.Error ? "\n    " + (stack ?? "").Split('\n')[0] : "";
                            File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss} [{type}] {msg}{extra}\n");
                        }
                        catch { }
                    }
                };
            }
            catch { _path = null; }
        }
    }
}
