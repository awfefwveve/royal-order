using System.Collections;
using System.IO;
using KnifeWar.Core;
using KnifeWar.Game;
using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 포트폴리오용 스크린샷 자동 촬영 (에디터 전용).
    /// Logs/claude_showcase.txt 가 있으면 플레이 시작 때 실행: 메뉴·가입·설정 → 연습전(봇)에서
    /// 1라운드는 왕 시점, 2라운드는 기사 시점으로 장면을 찍어 Logs/showcase/ 에 PNG로 저장하고 플레이를 끈다.
    /// </summary>
    public sealed class ShowcaseCapture : MonoBehaviour
    {
        NetBootstrap _boot;
        MatchController _m;
        string _dir;
        int _n;

        static string LogsDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));

        public static void TryStart(NetBootstrap boot, MatchController m)
        {
            if (!Application.isEditor) return;
            string flag = Path.Combine(LogsDir, "claude_showcase.txt");
            if (!File.Exists(flag)) return;
            try { File.Delete(flag); } catch { }
            var c = boot.gameObject.AddComponent<ShowcaseCapture>();
            c._boot = boot;
            c._m = m;
        }

        IEnumerator Start()
        {
            _dir = Path.Combine(LogsDir, "showcase");
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.GetFiles(_dir)) try { File.Delete(f); } catch { }
            Log("시작");

            // 1. 시작 메뉴 (로그인)
            yield return Wait(2f);
            yield return Shot("menu_login");

            // 2. 가입 화면 (약관 동의)
            _boot.ShowcaseRegister(true);
            yield return Wait(0.6f);
            yield return Shot("register_terms");
            _boot.ShowcaseRegister(false);

            // 3. 설정 창: 조작(키 바꾸기) · 화면
            _boot.ShowcaseSettings = true;
            SettingsWindow.Tab = 0;
            yield return Wait(0.6f);
            yield return Shot("settings_controls");
            SettingsWindow.Tab = 1;
            yield return Wait(0.6f);
            yield return Shot("settings_video");
            SettingsWindow.Tab = 0;
            _boot.ShowcaseSettings = false;

            // 4. 연습전: 사람 칸도 봇이 조작, 1라운드는 내가 왕
            _m.ConfigureRounds(2);
            _m.HumanIsBot = true;
            _boot.ShowcaseStartOffline();
            yield return Wait(0.5f);
            int me = _m.HumanIndex;
            int teamKing = me + 5;   // 같은 팀 원래 왕 칸 (0번 팀: 5번)
            _m.ShowcaseSetClass(me, PlayerClass.King);
            _m.ShowcaseSetClass(teamKing, PlayerClass.Knight);

            yield return Until(() => _m.Round.Phase == RoundPhase.Select, 20f);
            yield return Wait(2.5f);
            yield return Shot("class_select");

            yield return Until(() => _m.Round.Phase == RoundPhase.Live, 30f);
            Time.timeScale = 2f;
            yield return Until(() => _m.Monsters != null && _m.Monsters.AliveInTeam(_m.Human.Team) >= 4, 40f);
            Time.timeScale = 1f;
            yield return Wait(1.5f);
            yield return Shot("king_summon");

            // 전체 모습 (위에서)
            yield return Wait(4f);
            yield return Overview("overview_bridge");
            yield return Wait(2f);
            yield return Shot("king_battle");

            // 점수판
            GameHud.ForceScoreboard = true;
            yield return Wait(0.4f);
            yield return Shot("scoreboard");
            GameHud.ForceScoreboard = false;

            // 2라운드: 나는 기사
            _m.ShowcaseSetClass(me, PlayerClass.Knight);
            _m.ShowcaseSetClass(teamKing, PlayerClass.King);
            Time.timeScale = 3f;
            yield return Until(() => _m.Round.Phase == RoundPhase.Select && _m.Round.RoundNumber >= 2, 240f);
            Time.timeScale = 1f;
            yield return Until(() => _m.Round.Phase == RoundPhase.Live, 30f);
            Time.timeScale = 2f;
            yield return Until(() => NearEnemy(3.2f), 60f);
            Time.timeScale = 1f;
            yield return Shot("knight_duel");
            yield return Until(() => NearEnemy(2.5f), 20f);
            yield return Shot("knight_duel2");

            // 경기 끝 (결과·점수판)
            Time.timeScale = 4f;
            yield return Until(() => _m.Round.Phase == RoundPhase.MatchEnd, 400f);
            Time.timeScale = 1f;
            yield return Wait(1.5f);
            yield return Shot("match_end");

            Log($"끝 ({_n}장)");
            File.WriteAllText(Path.Combine(_dir, "done.txt"), _n.ToString());
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        bool NearEnemy(float meters)
        {
            var h = _m.Human;
            if (h == null || !h.Alive || _m.Round.Phase != RoundPhase.Live) return false;
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                var a = _m.Avatars[p];
                if (a.Team == h.Team || !a.Alive) continue;
                if ((a.transform.position - h.transform.position).sqrMagnitude < meters * meters) return true;
            }
            return false;
        }

        IEnumerator Overview(string name)
        {
            var go = new GameObject("ShowcaseCam");
            var cam = go.AddComponent<Camera>();
            cam.depth = 50;
            cam.fieldOfView = 55f;
            cam.farClipPlane = 600f;
            go.transform.position = new Vector3(0f, 26f, -38f);
            go.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 0f, 2f) - go.transform.position);
            yield return Wait(0.3f);
            yield return Shot(name);
            Destroy(go);
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            _n++;
            string path = Path.Combine(_dir, $"{_n:00}_{name}.bmp");
            int w = Screen.width, h = Screen.height;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            File.WriteAllBytes(path, Bmp(tex.GetPixels32(), w, h));
            Destroy(tex);
            Log($"찍음 {_n:00}_{name} ({Screen.width}x{Screen.height})");
            yield return null;
            yield return null;
        }

        /// <summary>24비트 BMP (이미지 변환 모듈 없이 저장). 픽셀은 아래 줄부터 = BMP 순서와 같다.</summary>
        static byte[] Bmp(Color32[] px, int w, int h)
        {
            int row = (w * 3 + 3) & ~3, size = 54 + row * h;
            var b = new byte[size];
            void I32(int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
            b[0] = (byte)'B'; b[1] = (byte)'M'; I32(2, size); I32(10, 54); I32(14, 40); I32(18, w); I32(22, h);
            b[26] = 1; b[28] = 24; I32(34, row * h);
            for (int y = 0; y < h; y++)
            {
                int o = 54 + y * row, s = y * w;
                for (int x = 0; x < w; x++) { var c = px[s + x]; b[o++] = c.b; b[o++] = c.g; b[o++] = c.r; }
            }
            return b;
        }

        static IEnumerator Wait(float realSeconds)
        {
            yield return new WaitForSecondsRealtime(realSeconds);
        }

        IEnumerator Until(System.Func<bool> cond, float timeoutReal)
        {
            float end = Time.realtimeSinceStartup + timeoutReal;
            while (!cond())
            {
                if (Time.realtimeSinceStartup > end) { Log("시간 초과로 건너뜀"); yield break; }
                yield return null;
            }
        }

        static void Log(string s) => Debug.Log("[showcase] " + s);
    }
}
