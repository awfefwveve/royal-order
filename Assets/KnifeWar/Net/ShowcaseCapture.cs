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

            // 연습전: 사람 칸도 봇이 조작, 나는 왕 (메뉴 화면은 카메라가 없어 촬영하지 않음)
            GameHud.ShowcaseMode = true;
            _m.HumanIsBot = true;
            _boot.ShowcaseStartOffline();
            yield return Wait(0.5f);
            int me = _m.HumanIndex;
            _m.ShowcaseSetClass(me, PlayerClass.King);
            _m.ShowcaseSetClass(me + 5, PlayerClass.Knight);

            yield return Until(() => _m.Round.Phase == RoundPhase.Select, 20f);
            yield return Wait(2.5f);
            yield return Shot("class_select");

            yield return Until(() => _m.Round.Phase == RoundPhase.Live, 30f);
            Time.timeScale = 2f;
            yield return Until(() => _m.Monsters != null && _m.Monsters.AliveInTeam(_m.Human.Team) >= 4, 40f);
            Time.timeScale = 1f;
            yield return Wait(1f);
            // 왕 뒤에서 적진 쪽으로: 왕 + 앞으로 걸어가는 몬스터
            {
                var k = _m.Human;
                Vector3 fwd = TowardEnemy(k.Team);
                Vector3 kp = k.transform.position;
                yield return CamShot("king_summon", kp - fwd * 3.2f + Vector3.up * 2.4f + Vector3.forward * 0.8f, kp + fwd * 10f + Vector3.up * 0.6f, 70f);
            }

            // 기사끼리 붙은 순간: 우리 기사 어깨 너머로 적을 담는다 (2장)
            for (int shot = 0; shot < 2; shot++)
            {
                Time.timeScale = 2f;
                int a = -1, e = -1;
                float end = Time.realtimeSinceStartup + 40f;
                while (Time.realtimeSinceStartup < end && !FindDuel(_m.Human.Team, 3.5f, out a, out e)) yield return null;
                Time.timeScale = 1f;
                if (a < 0) { Log("근접전 못 찾음"); break; }
                var ap = _m.Avatars[a].transform.position;
                var ep = _m.Avatars[e].transform.position;
                Vector3 d = ep - ap; d.y = 0f; d.Normalize();
                Vector3 side = Vector3.Cross(Vector3.up, d) * (shot == 0 ? 0.7f : -0.7f);
                yield return CamShot(shot == 0 ? "knight_duel" : "knight_duel2", ap - d * 2.4f + side + Vector3.up * 1.9f, ep + Vector3.up * 1.1f, 62f);
                yield return Wait(2.5f);
            }

            // 전투 중인 곳을 비스듬히 위에서
            if (FindDuel(_m.Human.Team, 8f, out int a2, out int e2))
            {
                var mid = (_m.Avatars[a2].transform.position + _m.Avatars[e2].transform.position) * 0.5f;
                yield return CamShot("battle_wide", mid + new Vector3(-6f, 7f, -11f), mid + Vector3.up * 0.8f, 55f);
            }
            yield return Overview("overview_bridge");

            // 점수판
            GameHud.ForceScoreboard = true;
            yield return Wait(0.4f);
            yield return Shot("scoreboard");
            GameHud.ForceScoreboard = false;
            GameHud.ShowcaseMode = false;

            Log($"끝 ({_n}장)");
            File.WriteAllText(Path.Combine(_dir, "done.txt"), _n.ToString());
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        /// <summary>우리 팀 위치에서 적 평균 위치 쪽 (다리 방향 X축).</summary>
        Vector3 TowardEnemy(int team)
        {
            float mine = 0f, theirs = 0f; int nm = 0, nt = 0;
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                var a = _m.Avatars[p];
                if (a.Team == team) { mine += a.transform.position.x; nm++; } else { theirs += a.transform.position.x; nt++; }
            }
            return (theirs / Mathf.Max(1, nt)) >= (mine / Mathf.Max(1, nm)) ? Vector3.right : Vector3.left;
        }

        /// <summary>살아 있는 우리 기사(왕 제외)와 적이 dist 안에 붙어 있으면 그 둘.</summary>
        bool FindDuel(int team, float dist, out int ours, out int enemy)
        {
            ours = enemy = -1;
            float best = dist * dist;
            for (int p = 0; p < MatchController.MaxPlayers; p++)
            {
                var a = _m.Avatars[p];
                if (a.Team != team || !a.Alive || a.Class != PlayerClass.Knight) continue;
                for (int q = 0; q < MatchController.MaxPlayers; q++)
                {
                    var b = _m.Avatars[q];
                    if (b.Team == team || !b.Alive) continue;
                    float d = (a.transform.position - b.transform.position).sqrMagnitude;
                    if (d < best) { best = d; ours = p; enemy = q; }
                }
            }
            return ours >= 0;
        }

        IEnumerator CamShot(string name, Vector3 pos, Vector3 look, float fov)
        {
            var go = new GameObject("ShowcaseCam");
            var cam = go.AddComponent<Camera>();
            cam.depth = 50;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 600f;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(look - pos);
            yield return null;
            yield return Shot(name);
            Destroy(go);
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
