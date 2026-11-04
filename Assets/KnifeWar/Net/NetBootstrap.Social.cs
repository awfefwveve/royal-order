using System;
using System.Collections;
using KnifeWar.Game;
using UnityEngine;

namespace KnifeWar.Net
{
    [Serializable] public sealed class FriendDto { public string id; public string displayId; public string state; }
    [Serializable] public sealed class InviteDto { public string id; public string from; public string kind; public string code; }
    [Serializable] public sealed class FriendsDto { public FriendDto[] friends; public FriendDto[] incoming; public FriendDto[] outgoing; public InviteDto[] invites; }
    [Serializable] public sealed class SeatDto { public int slot; public string id; public string name; public bool host; public bool me; }
    [Serializable] public sealed class RoomDto { public bool inRoom; public string code; public bool isHost; public bool started; public SeatDto[] seats; }

    /// <summary>
    /// 친구 (이름#태그로 추가, 접속 상태, 파티·방 초대) + 사용자 설정 대전 (사설방: 12칸, 빈 칸은 봇, 방장이 시작).
    /// 메뉴에 있는 동안 /friends 를 6초마다, 방에 있으면 /room 을 2초마다 묻는다.
    /// 방장이 시작하면 평소 매칭과 같은 길(/mm/join → /mm/status)로 접속 정보를 받아 경기에 들어간다.
    /// </summary>
    public sealed partial class NetBootstrap
    {
        FriendsDto _friends;
        RoomDto _room;
        static int _playTab;          // 0 경쟁전, 1 사용자 설정 (경기 다녀와도 유지)
        string _friendInput = "", _roomCodeInput = "", _socialMsg = "";
        float _socialMsgAt = -10f;
        bool _socialBusy, _friendsOpen;
        Coroutine _socialLoop;
        float _roomPollAt, _friendsPollAt;

        bool LoggedIn => _api != null && !string.IsNullOrEmpty(_api.Token) && _profile != null;
        bool InRoom => _room != null && _room.inRoom;
        int FriendRequests => _friends?.incoming?.Length ?? 0;

        void SocialMsg(string s) { _socialMsg = s; _socialMsgAt = FE.Now; }

        /// <summary>메뉴 화면을 그릴 때마다 불러서 루프가 돌고 있게 한다.</summary>
        void EnsureSocialLoop()
        {
            if (_socialLoop == null && LoggedIn && !_started) _socialLoop = StartCoroutine(SocialLoop());
        }

        IEnumerator SocialLoop()
        {
            while (!_started && LoggedIn)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= _friendsPollAt)
                {
                    _friendsPollAt = now + 6f;
                    yield return _api.Get("/friends", (code, text) => { if (code == 200) _friends = JsonUtility.FromJson<FriendsDto>(text); });
                }
                if ((InRoom || _playTab == 1) && now >= _roomPollAt)
                {
                    _roomPollAt = now + 2f;
                    yield return _api.Get("/room", (code, text) => { if (code == 200) _room = JsonUtility.FromJson<RoomDto>(text); });
                    // 방장이 시작함 → 평소 매칭 길로 접속 정보 받기
                    if (InRoom && _room.started && !_searching && !_started)
                        StartCoroutine(Matchmake(_backendUrl.Trim(), _profile.name, "", _lag, false));
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            _socialLoop = null;
        }

        IEnumerator SocialCall(string path, string body, Action<long, string> done)
        {
            if (_socialBusy || !LoggedIn) yield break;
            _socialBusy = true;
            yield return _api.Post(path, body, (code, text) =>
            {
                if (code == 0) SocialMsg(L.T("st_backend_down"));
                else done?.Invoke(code, text ?? "");
            });
            _socialBusy = false;
            _friendsPollAt = 0f;   // 바로 새로고침
            _roomPollAt = 0f;
        }

        static string IdBody(string id) => $"{{\"id\":\"{BackendClient.Escape(id)}\"}}";

        // ------------------------------------------------------------------ 친구 패널 (오른쪽)

        void DrawFriendsPanel(float w, float h)
        {
            if (!_friendsOpen || !LoggedIn) return;
            float pw = Mathf.Min(380f, w * 0.32f), px = w - pw - 20, py = TopH + 16, ph = h - TopH - BottomH - 32;
            var box = new Rect(px - 14, py - 10, pw + 28, ph + 20);
            FE.Fill(box, new Color(0.04f, 0.05f, 0.08f, 0.97f));
            FE.Border(box, FE.Line);
            FE.Text(new Rect(px, py, pw - 40, 30), L.T("fr_title"), 20, Color.white, TextAnchor.MiddleLeft, true);
            if (FE.Button(new Rect(px + pw - 32, py, 32, 30), "×", FE.Kind.Ghost, 20)) _friendsOpen = false;
            py += 40;

            // 추가
            _friendInput = GUI.TextField(new Rect(px, py, pw - 92, 32), _friendInput, 40, _field);
            if (string.IsNullOrEmpty(_friendInput)) FE.Text(new Rect(px + 8, py, pw - 100, 32), L.T("fr_hint"), 14, new Color(1, 1, 1, 0.35f));
            GUI.enabled = !_socialBusy && _friendInput.Contains("#");
            if (FE.Button(new Rect(px + pw - 84, py, 84, 32), L.T("fr_add"), FE.Kind.Primary, 15))
            {
                string did = _friendInput.Trim();
                StartCoroutine(SocialCall("/friends/request", $"{{\"displayId\":\"{BackendClient.Escape(did)}\"}}", (code, text) =>
                {
                    if (code == 200) { SocialMsg(text.Contains("\"friends\"") ? L.T("fr_now_friends") : L.T("fr_sent")); _friendInput = ""; }
                    else if (code == 404) SocialMsg(L.T("fr_not_found"));
                    else if (code == 400) SocialMsg(L.T("fr_hint"));
                    else SocialMsg(L.T("fr_failed"));
                }));
            }
            GUI.enabled = true;
            py += 38;
            if (FE.Now - _socialMsgAt < 5f) { FE.Text(new Rect(px, py, pw, 22), _socialMsg, 13, FE.Gold); py += 24; }

            // 받은 요청
            var inc = _friends?.incoming;
            if (inc != null && inc.Length > 0)
            {
                FE.Text(new Rect(px, py, pw, 24), L.T("fr_requests", inc.Length), 14, FE.Gold, TextAnchor.MiddleLeft, true);
                py += 28;
                foreach (var f in inc)
                {
                    FE.Text(new Rect(px, py, pw - 150, 30), f.displayId, 15, Color.white);
                    if (FE.Button(new Rect(px + pw - 144, py, 70, 30), L.T("fr_accept"), FE.Kind.Primary, 13)) StartCoroutine(SocialCall("/friends/accept", IdBody(f.id), null));
                    if (FE.Button(new Rect(px + pw - 70, py, 70, 30), L.T("fr_decline"), FE.Kind.Line, 13)) StartCoroutine(SocialCall("/friends/remove", IdBody(f.id), null));
                    py += 36;
                }
                py += 6;
            }

            // 친구 목록 (접속 중 먼저)
            var list = _friends?.friends ?? new FriendDto[0];
            FE.Text(new Rect(px, py, pw, 24), L.T("fr_list", list.Length), 14, FE.Sub, TextAnchor.MiddleLeft, true);
            py += 28;
            if (list.Length == 0) FE.Text(new Rect(px, py, pw, 44), L.T("fr_empty"), 13, FE.Sub, TextAnchor.UpperLeft);
            var sorted = (FriendDto[])list.Clone();
            Array.Sort(sorted, (a, b) => Rank(a.state).CompareTo(Rank(b.state)));
            string inviteKind = InRoom ? "room" : "party";
            foreach (var f in sorted)
            {
                if (py > box.yMax - 70) break;
                bool on = f.state != "offline";
                Color dot = f.state == "online" ? new Color(0.35f, 0.85f, 0.45f) : f.state == "offline" ? new Color(1, 1, 1, 0.25f) : FE.Gold;
                FE.Fill(new Rect(px, py + 11, 8, 8), dot);
                FE.Text(new Rect(px + 16, py, pw - 120, 18), f.displayId, 15, on ? Color.white : FE.Sub);
                FE.Text(new Rect(px + 16, py + 17, pw - 120, 16), L.T("fr_state_" + f.state), 11, FE.Sub);
                GUI.enabled = on && f.state != "in_match" && !_socialBusy && !_searching;
                if (FE.Button(new Rect(px + pw - 100, py + 2, 66, 30), L.T("fr_invite"), FE.Kind.Line, 13))
                    StartCoroutine(SocialCall("/friends/invite", $"{{\"id\":\"{BackendClient.Escape(f.id)}\",\"kind\":\"{inviteKind}\"}}", (code, text) =>
                    {
                        if (code == 200) { SocialMsg(L.T("fr_invited", f.displayId)); if (inviteKind == "party") StartCoroutine(ResumeParty()); }
                        else SocialMsg(L.T("fr_failed"));
                    }));
                GUI.enabled = !_socialBusy;
                if (FE.Button(new Rect(px + pw - 30, py + 2, 30, 30), "×", FE.Kind.Ghost, 16)) StartCoroutine(SocialCall("/friends/remove", IdBody(f.id), null));
                GUI.enabled = true;
                py += 40;
            }

            // 보낸 요청
            var outg = _friends?.outgoing;
            if (outg != null && outg.Length > 0 && py < box.yMax - 50)
            {
                py += 6;
                FE.Text(new Rect(px, py, pw, 22), L.T("fr_pending"), 13, FE.Sub, TextAnchor.MiddleLeft, true);
                py += 24;
                foreach (var f in outg)
                {
                    if (py > box.yMax - 36) break;
                    FE.Text(new Rect(px, py, pw - 40, 26), f.displayId, 13, FE.Sub);
                    if (FE.Button(new Rect(px + pw - 30, py, 30, 26), "×", FE.Kind.Ghost, 14)) StartCoroutine(SocialCall("/friends/remove", IdBody(f.id), null));
                    py += 28;
                }
            }
        }

        static int Rank(string state) => state == "online" ? 0 : state == "searching" ? 1 : state == "in_match" ? 2 : 3;

        // ------------------------------------------------------------------ 초대 알림 (위쪽 가운데)

        void DrawInviteBanner(float w)
        {
            var inv = _friends?.invites;
            if (inv == null || inv.Length == 0 || _searching) return;
            var i = inv[inv.Length - 1];
            float bw = Mathf.Min(560f, w - 40f), bx = (w - bw) * 0.5f, by = TopH + 10;
            var r = new Rect(bx, by, bw, 52);
            FE.Fill(r, new Color(0.06f, 0.08f, 0.14f, 0.97f));
            FE.Border(r, FE.Gold, 2f);
            string what = i.kind == "room" ? L.T("fe_custom") : L.T("party");
            FE.Text(new Rect(bx + 16, by, bw - 200, 52), L.T("fr_invite_msg", i.from, what), 15, Color.white, TextAnchor.MiddleLeft, true);
            GUI.enabled = !_socialBusy && !_partyBusy;
            if (FE.Button(new Rect(bx + bw - 176, by + 10, 80, 32), L.T("fr_join"), FE.Kind.Primary, 14)) AcceptInvite(i);
            if (FE.Button(new Rect(bx + bw - 88, by + 10, 76, 32), L.T("fr_decline"), FE.Kind.Line, 14))
                StartCoroutine(SocialCall("/friends/invite/dismiss", IdBody(i.id), (c, t) => _friends.invites = Array.FindAll(_friends.invites, x => x.id != i.id)));
            GUI.enabled = true;
        }

        void AcceptInvite(InviteDto i)
        {
            if (_friends?.invites != null) _friends.invites = Array.FindAll(_friends.invites, x => x.id != i.id);
            StartCoroutine(_api.Post("/friends/invite/dismiss", IdBody(i.id), null));
            if (i.kind == "room") { _playTab = 1; Go(Scr.Play); StartCoroutine(RoomJoin(i.code)); }
            else { _playTab = 0; Go(Scr.Play); StartCoroutine(PartyAction("/party/join", $"{{\"code\":\"{BackendClient.Escape(i.code)}\"}}")); }
        }

        // ------------------------------------------------------------------ 사용자 설정 대전

        IEnumerator RoomCreate()
        {
            if (_socialBusy) yield break;
            yield return EnsurePings(_lag);
            yield return SocialCall("/room/create", BackendClient.PingsJson(Session.Pings ?? new System.Collections.Generic.Dictionary<string, int>()), (code, text) =>
            {
                if (code == 200) { _room = JsonUtility.FromJson<RoomDto>(text); _party = null; }
                else SocialMsg(code == 503 ? L.T("upd_maintenance") : L.T("fr_failed"));
            });
        }

        IEnumerator RoomJoin(string code)
        {
            if (_socialBusy || string.IsNullOrWhiteSpace(code)) yield break;
            yield return EnsurePings(_lag);
            string pings = BackendClient.PingsJson(Session.Pings ?? new System.Collections.Generic.Dictionary<string, int>());
            yield return SocialCall("/room/join", $"{{\"code\":\"{BackendClient.Escape(code.Trim().ToUpperInvariant())}\"," + pings.Substring(1), (c, text) =>
            {
                if (c == 200) { _room = JsonUtility.FromJson<RoomDto>(text); _party = null; _roomCodeInput = ""; }
                else if (c == 404) SocialMsg(L.T("party_not_found"));
                else if (c == 409) SocialMsg(text.Contains("full") ? L.T("party_full") : L.T("room_started"));
                else SocialMsg(L.T("fr_failed"));
            });
        }

        void DrawCustom(float w, float h, float top)
        {
            float cx = w * 0.5f;
            if (!InRoom)
            {
                float rw = 560, rx = cx - rw * 0.5f, ry = top + 30;
                FE.Text(new Rect(rx, ry, rw, 60), L.T("room_desc"), 15, FE.Sub, TextAnchor.UpperCenter);
                ry += 80;
                GUI.enabled = !_socialBusy && !_searching;
                if (FE.Button(new Rect(rx + 80, ry, rw - 160, 60), L.T("room_create"), FE.Kind.Primary, 22)) StartCoroutine(RoomCreate());
                ry += 84;
                _roomCodeInput = GUI.TextField(new Rect(rx + 80, ry + 2, rw - 330, 34), _roomCodeInput.ToUpperInvariant(), 8, _field);
                if (FE.Button(new Rect(rx + rw - 240, ry, 160, 38), L.T("fe_join_code"), FE.Kind.Line, 15)) StartCoroutine(RoomJoin(_roomCodeInput));
                GUI.enabled = true;
                if (FE.Now - _socialMsgAt < 5f) FE.Text(new Rect(rx, ry + 50, rw, 24), _socialMsg, 14, FE.Gold, TextAnchor.MiddleCenter);
                return;
            }

            // 방 코드
            float y = top + 6;
            FE.Text(new Rect(0, y, cx - 8, 34), L.T("room_code", _room.code), 18, Color.white, TextAnchor.MiddleRight, true);
            bool copied = FE.Now - _copiedAt < 1.5f;
            if (FE.Button(new Rect(cx + 8, y, 110, 34), copied ? L.T("fe_copied") : L.T("fe_copy"), FE.Kind.Line, 15)) { GUIUtility.systemCopyBuffer = _room.code; _copiedAt = FE.Now; }
            if (FE.Button(new Rect(cx + 126, y, 130, 34), L.T("room_invite"), FE.Kind.Line, 15)) _friendsOpen = true;
            y += 50;

            // 두 팀 칸
            float colW = Mathf.Min(360f, (w - 120f) * 0.5f), gap = 40f, rowH = 44f;
            float x0 = cx - colW - gap * 0.5f, x1 = cx + gap * 0.5f;
            FE.Text(new Rect(x0, y, colW, 28), L.T("room_team_a"), 16, FE.Blue, TextAnchor.MiddleLeft, true);
            FE.Text(new Rect(x1, y, colW, 28), L.T("room_team_b"), 16, FE.Red, TextAnchor.MiddleLeft, true);
            y += 32;
            for (int i = 0; i < 12; i++)
            {
                var s = _room.seats != null && i < _room.seats.Length ? _room.seats[i] : null;
                float sx = i < 6 ? x0 : x1, sy = y + (i % 6) * (rowH + 6);
                var r = new Rect(sx, sy, colW, rowH);
                bool human = s != null && !string.IsNullOrEmpty(s.id);
                bool hover = FE.Hover(r);
                FE.Fill(r, human ? (s.me ? new Color(FE.Gold.r, FE.Gold.g, FE.Gold.b, 0.22f) : new Color(0.08f, 0.11f, 0.18f, 0.95f)) : new Color(1, 1, 1, hover ? 0.07f : 0.03f));
                FE.Border(r, human && s.me ? FE.Gold : new Color(1, 1, 1, hover ? 0.3f : 0.12f));
                string king = i % 6 == 5 ? "  ♔" : "";
                if (human)
                {
                    FE.Text(new Rect(sx + 14, sy, colW - 60, rowH), (s.host ? "★ " : "") + s.name + king, 16, Color.white, TextAnchor.MiddleLeft, true);
                    if (_room.isHost && !s.me && !_room.started && FE.Button(new Rect(sx + colW - 38, sy + 7, 30, 30), "×", FE.Kind.Ghost, 16))
                        StartCoroutine(SocialCall("/room/kick", IdBody(s.id), null));
                }
                else
                {
                    FE.Text(new Rect(sx + 14, sy, colW - 28, rowH), L.T("room_bot") + king, 15, FE.Sub, TextAnchor.MiddleLeft);
                    if (hover && !_room.started) FE.Text(new Rect(sx, sy, colW - 14, rowH), L.T("room_move"), 12, FE.Gold, TextAnchor.MiddleRight);
                    if (FE.Clicked(r) && !_socialBusy && !_room.started) StartCoroutine(SocialCall("/room/move", $"{{\"slot\":{i}}}", (c, t) => { if (c == 200) _room = JsonUtility.FromJson<RoomDto>(t); }));
                }
            }
            y += 6 * (rowH + 6) + 8;
            if (FE.Now - _socialMsgAt < 5f) FE.Text(new Rect(0, y, w, 24), _socialMsg, 14, FE.Gold, TextAnchor.MiddleCenter);
        }

        /// <summary>아래 큰 버튼 줄 (사용자 설정 탭).</summary>
        void DrawCustomButtons(Rect main, float by)
        {
            if (!InRoom) return;
            if (_searching || _room.started)
            {
                FE.Fill(main, new Color(0.05f, 0.08f, 0.14f, 0.95f));
                FE.Border(main, FE.Gold, 2f);
                FE.Text(main, L.T("room_starting"), 20, Color.white, TextAnchor.MiddleCenter, true);
                return;
            }
            GUI.enabled = _room.isHost && !_socialBusy && !Session.Maintenance;
            if (FE.Button(main, _room.isHost ? L.T("room_start") : L.T("room_wait_host"), FE.Kind.Primary, 22))
                StartCoroutine(SocialCall("/room/start", "{}", (c, t) =>
                {
                    if (c == 200) { _room = JsonUtility.FromJson<RoomDto>(t); StartCoroutine(Matchmake(_backendUrl.Trim(), _profile.name, "", _lag, false)); }
                    else if (t.Contains("no_server")) SocialMsg(L.T("room_no_server"));
                    else if (t.Contains("busy")) SocialMsg(L.T("room_busy"));
                    else SocialMsg(L.T("fr_failed"));
                }));
            GUI.enabled = !_socialBusy;
            if (FE.Button(new Rect(main.xMax + 16, by + 10, 200, 40), L.T("room_leave"), FE.Kind.Line, 16))
                StartCoroutine(SocialCall("/room/leave", "{}", (c, t) => _room = null));
            GUI.enabled = true;
        }
    }
}
