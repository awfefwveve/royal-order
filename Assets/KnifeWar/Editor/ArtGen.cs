using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using KnifeWar.Game;
using UnityEditor;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 아트 자동 생성 파이프라인 (에디터 안에서 실행 — 유니티는 인터넷이 되므로):
    ///   1) 원화: OpenAI 이미지 API (gpt-image) → Assets/KnifeWar/Art/Gen/이름/이름_concept.png
    ///   2) 3D:  NC AI VARCO Image-to-3D API → 이름.glb
    ///   3) 넣기: GlbImporter → 메시·텍스처·재질 → SkinSet 칸에 자동 연결 (몬스터) / 프리팹 (건물·캐릭터)
    /// 키는 Deploy/secrets.env 의 OPENAI_API_KEY, NCAI_API_KEY (파일에만, 코드·로그에 안 남김).
    /// 명령: Logs/claude_artgen.txt 에 "all | monsters | buildings | characters | 이름 [force] [step=image|model|import]"
    ///       "yaw 이름 각도" / "height 이름 미터" 는 tuning.json 에 저장 후 다시 import 만.
    /// 이미 있는 결과(png/glb)는 force 가 없으면 다시 만들지 않는다 (돈 아끼기).
    /// </summary>
    public static class ArtGen
    {
        public const string GenDir = "Assets/KnifeWar/Art/Gen";
        const string TuningPath = GenDir + "/tuning.json";
        const string SkinPath = "Assets/KnifeWar/Resources/KnifeWar/SkinSet.asset";

        enum Kind { Monster, Building, Character }

        sealed class Job
        {
            public string Name; public Kind Kind; public int Index; public float Height; public int Faces; public string Prompt;
            public float Yaw;   // tuning.json 으로 덮어씀
        }

        const string Style = " Style: photorealistic, hyper-realistic cinematic realism (looks like a real object photographed in a studio, not a painting or illustration), AAA console game quality, physically based materials with realistic wear, " +
            "clear readable silhouette, restrained detail (no clutter, no excessive scratches, no excessive glow), " +
            "palette: dark stone #17191D, iron #34383E, royal crimson #641E27, antique gold #A67C38, gold highlight #D9C49A. " +
            "Soft even studio lighting, subtle cool rim light. No text, no logo, no watermark, no UI, no frame.";

        const string MonsterBase = "Summoned monster for a dark fantasy game: {0}. Full body for 3D modeling reference: strict front view, orthographic camera, " +
            "neutral A-pose (arms 45 degrees down away from the body, legs slightly apart, standing straight, symmetric), entire body visible from head to feet with margin, " +
            "isolated single object on a plain transparent background, even shadowless lighting, no ground shadow, no fog. Weapon held vertically close to the body. " +
            "Asymmetric, menacing silhouette clearly different from human knights. " +
            "Hollow animated armor with faint golden rune light leaking from cracks (subtle, small areas only). Real-world human-soldier scale unless stated otherwise.";

        const string CharBase = "Full-body character for 3D modeling reference: {0}. Strict front view, orthographic camera, neutral A-pose " +
            "(arms 45 degrees down, palms facing the body, feet shoulder-width apart, standing straight), entire body visible from head to feet with margin, " +
            "isolated single object on a plain transparent background, even shadowless lighting, no ground shadow, no fog, sharp focus. Weapon NOT in hand. Cape hangs straight down behind the body.";

        const string BuildingBase = "{0}. Isolated single object on a plain transparent background, three-quarter view, even lighting, no ground shadow, entire object visible with margin.";

        static readonly Job[] Jobs =
        {
            new Job { Name = "monster_0_footman", Kind = Kind.Monster, Index = 0, Height = 1.75f, Faces = 5000, Prompt = string.Format(MonsterBase, "a hollow footsoldier, real soldier size (1.75 m), slightly hunched, mismatched rusted armor plates, a long jagged spear, fast and light") },
            new Job { Name = "monster_1_shield", Kind = Kind.Monster, Index = 1, Height = 1.85f, Faces = 5000, Prompt = string.Format(MonsterBase, "a hollow shieldbearer, real soldier size (1.85 m), stocky and wide, massive slab-like armor, one giant tower shield fused to its left arm, slow and heavy") },
            new Job { Name = "monster_2_archer", Kind = Kind.Monster, Index = 2, Height = 1.75f, Faces = 5000, Prompt = string.Format(MonsterBase, "a thin hollow archer, real soldier size (1.75 m), slightly hunched, tattered hood over a cage-like armor torso, a bone longbow held at the side, quiver of golden-tipped arrows") },
            new Job { Name = "monster_3_ogre", Kind = Kind.Monster, Index = 3, Height = 2.6f, Faces = 8000, Prompt = string.Format(MonsterBase, "a giant hollow ogre, 2.6 m tall, bulging plates of armor over a bloated iron body, huge club-like fists, lumbering and brutal") },
            new Job { Name = "monster_4_twinogre", Kind = Kind.Monster, Index = 4, Height = 2.9f, Faces = 8000, Prompt = string.Format(MonsterBase, "a colossal two-headed hollow ogre, 2.9 m tall, two helmeted heads side by side snarling, four thick arms, even heavier armor") },
            new Job { Name = "monster_5_deathknight", Kind = Kind.Monster, Index = 5, Height = 2.1f, Faces = 8000, Prompt = string.Format(MonsterBase, "a tall hollow death knight, 2.1 m, black full plate with a torn cape, a huge greatsword held low, faint purple-black rune light instead of gold") },
            new Job { Name = "monster_6_firegolem", Kind = Kind.Monster, Index = 6, Height = 2.8f, Faces = 8000, Prompt = string.Format(MonsterBase, "a volcanic golem, 2.8 m, body of cracked black basalt with glowing lava seams, molten core visible through the chest, slow heavy stance (no team colors)") },
            new Job { Name = "building_barracks", Kind = Kind.Building, Index = 0, Height = 3.0f, Faces = 6000, Prompt = string.Format(BuildingBase, "a small medieval field barracks for a dark fantasy game, 2 x 2 m footprint under 3 m tall, dark timber and iron, a banner pole, a doorway where soldiers march out") },
            new Job { Name = "building_manawell", Kind = Kind.Building, Index = 1, Height = 1.5f, Faces = 6000, Prompt = string.Format(BuildingBase, "a small stone mana well for a dark fantasy game, 1.5 m across, glowing cool blue water with soft rising motes, iron frame and bucket") },
            new Job { Name = "knight_blue", Kind = Kind.Character, Faces = 20000, Index = 0, Height = 1.83f, Prompt = string.Format(CharBase, "a Royal Order knight, male human, 183 cm, 8 heads tall, athletic build. Fitted plate armor with layered pauldrons, articulated gauntlets and sabatons, a chainmail skirt over leather. Open-face helmet with a narrow nasal guard so the face is visible: short dark hair, trimmed beard, scarred determined face. A short heraldic tabard and a knee-length cape in cold steel blue (#2F4A66), small royal crest (a stylized crown over a sword) on the chest. Belt with a knife sheath on the right hip") },
            new Job { Name = "knight_red", Kind = Kind.Character, Faces = 20000, Index = 1, Height = 1.83f, Prompt = string.Format(CharBase, "a Royal Order knight, male human, 183 cm, 8 heads tall, athletic build. Fitted plate armor with layered pauldrons, articulated gauntlets and sabatons, a chainmail skirt over leather. Open-face helmet with a narrow nasal guard so the face is visible: short dark hair, trimmed beard, scarred determined face. A short heraldic tabard and a knee-length cape in royal crimson (#641E27), small royal crest (a stylized crown over a sword) on the chest, antique gold trim. Belt with a knife sheath on the right hip") },
            new Job { Name = "king_blue", Kind = Kind.Character, Faces = 20000, Index = 2, Height = 1.9f, Prompt = string.Format(CharBase, "the King of the Royal Order, male human, 190 cm, regal and tall, mature (around 50), grey-streaked beard, stern face. Ornate gilded plate cuirass under a heavy floor-length royal mantle in cold steel blue (#2F4A66) with white fur collar, a tall slender gold crown with a single gem. Long ceremonial robe under the armor. Hands empty") },
            new Job { Name = "king_red", Kind = Kind.Character, Faces = 20000, Index = 3, Height = 1.9f, Prompt = string.Format(CharBase, "the King of the Royal Order, male human, 190 cm, regal and tall, mature (around 50), grey-streaked beard, stern face. Ornate gilded plate cuirass under a heavy floor-length royal mantle in royal crimson (#641E27) with white fur collar, a tall slender gold crown with a single gem. Long ceremonial robe under the armor. Hands empty") },
        };

        // ---- 실행 상태 ----
        static readonly Queue<Job> _queue = new Queue<Job>();
        static Job _current;
        static Task<string> _task;
        static bool _force;
        static string _step;   // null = 전부, image / model / import
        static string _views = "front";   // 원화 각도: front 기본, "views=front,side" 처럼 지정 (한 장씩 확인받고 찍기 위해)
        static bool _hooked;
        static double _started;

        [MenuItem("KnifeWar/아트/생성 (명령 파일 Logs/claude_artgen.txt)", priority = 30)]
        public static void RunFromFile()
        {
            string path = Path.Combine(ProjectRoot, "Logs/claude_artgen.txt");
            if (!File.Exists(path)) { Debug.LogError("[artgen] 명령 파일 없음: " + path); return; }
            string args = File.ReadAllText(path).Trim();
            File.Delete(path);
            Run(args);
        }

        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static void Run(string args)
        {
            var parts = args.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) { Debug.LogError("[artgen] 명령 없음"); return; }
            if (parts[0] == "yaw" || parts[0] == "height")
            {
                if (parts.Length < 3 || !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) { Debug.LogError("[artgen] 사용법: yaw 이름 각도 / height 이름 미터"); return; }
                SetTuning(parts[1], parts[0], v);
                Run(parts[1] + " step=import");
                return;
            }
            _force = false; _step = null; _views = "front";
            var sel = new List<Job>();
            foreach (var p in parts)
            {
                if (p == "force") { _force = true; continue; }
                if (p.StartsWith("step=")) { _step = p.Substring(5); continue; }
                if (p.StartsWith("views=")) { _views = p.Substring(6); continue; }
                foreach (var j in Jobs)
                {
                    bool match = p == "all" || j.Name == p
                        || (p == "monsters" && j.Kind == Kind.Monster) || (p == "buildings" && j.Kind == Kind.Building) || (p == "characters" && j.Kind == Kind.Character);
                    if (match && !sel.Contains(j)) sel.Add(j);
                }
            }
            if (sel.Count == 0) { Debug.LogError("[artgen] 맞는 작업 없음: " + args); return; }
            if (_current != null) { Debug.LogWarning("[artgen] 이미 실행 중 — 뒤에 붙임"); }
            foreach (var j in sel) _queue.Enqueue(j);
            Debug.Log($"[artgen] 시작: {sel.Count}개 (force={_force}, step={_step ?? "all"})");
            if (!_hooked) { _hooked = true; EditorApplication.update += Update; }
        }

        static void Update()
        {
            if (_current == null)
            {
                if (_queue.Count == 0) { EditorApplication.update -= Update; _hooked = false; Debug.Log("[artgen] 전부 끝"); return; }
                _current = _queue.Dequeue();
                ApplyTuning(_current);
                _started = EditorApplication.timeSinceStartup;
                if (_lastImageModel == null) { var saved = EditorPrefs.GetString("kw.artgen.model26", ""); if (saved.Length > 0) _lastImageModel = saved; }
                var job = _current; bool force = _force; string step = _step; string views = _views;
                string root = ProjectRoot;
                Debug.Log($"[artgen] ▶ {job.Name}");
                _task = Task.Run(() => NetworkSteps(job, force, step, root, views));
                return;
            }
            if (!_task.IsCompleted)
            {
                return;
            }
            string err = _task.IsFaulted ? Short((_task.Exception?.InnerException ?? _task.Exception)?.ToString()) : _task.Result;
            if (_lastImageModel != null) EditorPrefs.SetString("kw.artgen.model26", _lastImageModel);
            if (!string.IsNullOrEmpty(err)) Debug.LogError($"[artgen] {_current.Name} 실패: {err}");
            else if (_step == null || _step == "import")
            {
                try { ImportStep(_current); }
                catch (Exception e) { Debug.LogError($"[artgen] {_current.Name} 넣기 실패: {e.Message}"); }
            }
            Debug.Log($"[artgen] ■ {_current.Name} ({EditorApplication.timeSinceStartup - _started:F0}초)");
            _current = null; _task = null;
        }

        // ---------------- 네트워크 (백그라운드 스레드) ----------------

        static string NetworkSteps(Job job, bool force, string step, string root, string views)
        {
            string dir = Path.Combine(root, GenDir, job.Name);
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, job.Name + "_front.png");
            string side = Path.Combine(dir, job.Name + "_side.png");
            string back = Path.Combine(dir, job.Name + "_back.png");
            string glb = Path.Combine(dir, job.Name + ".glb");
            var secrets = ReadSecrets(root);

            bool doImage = step == null || step == "image";
            bool wantFront = views.Contains("front"), wantSide = views.Contains("side"), wantBack = views.Contains("back");
            bool doModel = step == null || step == "model";
            if (doImage && wantFront && (force || !File.Exists(png)))
            {
                if (!secrets.TryGetValue("OPENAI_API_KEY", out var okey) || okey.Length < 10) return "OPENAI_API_KEY 없음 (Deploy/secrets.env)";
                // 모델 이름은 자주 바뀜: secrets.env 의 OPENAI_IMAGE_MODEL 이 있으면 그것, 없으면 후보를 차례로
                string[] models = secrets.TryGetValue("OPENAI_IMAGE_MODEL", out var m) && m.Length > 0 ? new[] { m } : new[] { "gpt-image-2.6-sunburst", "gpt-image-2.6-flare", "gpt-image-2.5-sunburst", "gpt-image-2.5-flare" };
                string e = null;
                foreach (var model in models)
                {
                    e = GenerateImage(okey, model, job.Prompt + Style, png);
                    if (e == null) { _lastImageModel = model; UnityEngine.Debug.Log($"[artgen] 원화 모델: {model}"); break; }
                    if (!e.Contains("does not exist")) break;   // 모델이 없을 때만 다음 후보로
                }
                if (e != null) return "원화: " + e;
                if (File.Exists(glb)) File.Delete(glb);   // 원화가 바뀌면 3D 도 다시
                // 옆·뒤는 지우지 않음 (새 정면과 맞지 않으면 따로 다시 찍기)
            }
            if (doImage && File.Exists(png) && (wantSide || wantBack))
            {
                // 옆·뒤 모습 (동작 추적용): 정면 그림을 참조로 같은 디자인 유지
                if (!secrets.TryGetValue("OPENAI_API_KEY", out var okey2) || okey2.Length < 10) return "OPENAI_API_KEY 없음 (Deploy/secrets.env)";
                string model = secrets.TryGetValue("OPENAI_IMAGE_MODEL", out var m2) && m2.Length > 0 ? m2 : _lastImageModel ?? "gpt-image-2.6-sunburst";
                const string viewBase = "The SAME character as the attached image: identical design, proportions, armor, colors and details. {0}, orthographic camera, same neutral A-pose, same scale and height, entire body visible with margin, isolated single object on a plain transparent background, even shadowless lighting, no ground shadow.";
                if (wantSide && (force || !File.Exists(side)))
                {
                    string e2 = EditImage(okey2, model, png, string.Format(viewBase, "Strict right side view (90 degrees)") + Style, side);
                    if (e2 != null) return "옆모습: " + e2;
                }
                if (wantBack && (force || !File.Exists(back)))
                {
                    string e2 = EditImage(okey2, model, png, string.Format(viewBase, "Strict back view (180 degrees)") + Style, back);
                    if (e2 != null) return "뒷모습: " + e2;
                }
            }
            if (doModel && (force || !File.Exists(glb)))
            {
                if (!File.Exists(png)) return "원화 파일이 없어 3D 를 못 만듦";
                if (!secrets.TryGetValue("NCAI_API_KEY", out var nkey) || nkey.Length < 10) return "NCAI_API_KEY 없음 (Deploy/secrets.env)";
                string e = ImageTo3D(nkey, png, glb, job.Faces > 0 ? job.Faces : 8000);
                if (e != null) return "3D: " + e;
            }
            return null;
        }

        static Dictionary<string, string> ReadSecrets(string root)
        {
            var d = new Dictionary<string, string>();
            string p = Path.Combine(root, "Deploy/secrets.env");
            if (!File.Exists(p)) return d;
            foreach (var line in File.ReadAllLines(p))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string v = t.Substring(eq + 1).Trim().Trim('"', '\'');
                d[t.Substring(0, eq).Trim()] = v;
            }
            return d;
        }

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        static string _lastImageModel;

        /// <summary>참조 이미지 + 프롬프트 → 새 이미지 (images/edits). 같은 캐릭터의 다른 각도용.</summary>
        static string EditImage(string key, string model, string refPng, string prompt, string outPng)
        {
            string e = EditImage(key, model, refPng, prompt, outPng, true);
            if (e != null && e.Contains("background")) e = EditImage(key, model, refPng, prompt, outPng, false);   // 투명 배경 미지원 모델
            return e;
        }

        static string EditImage(string key, string model, string refPng, string prompt, string outPng, bool transparent)
        {
            var form = new MultipartFormDataContent();   // req 가 같이 정리함 (두 번 Dispose 하면 Mono 에서 NRE)
            var img = new ByteArrayContent(File.ReadAllBytes(refPng));
            img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(img, "image", Path.GetFileName(refPng));
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(prompt), "prompt");
            form.Add(new StringContent("1024x1024"), "size");
            form.Add(new StringContent("high"), "quality");
            if (transparent) form.Add(new StringContent("transparent"), "background");
            form.Add(new StringContent("png"), "output_format");
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/edits") { Content = form };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var res = Http.SendAsync(req).GetAwaiter().GetResult();
            string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) return $"HTTP {(int)res.StatusCode}: {Short(text)}";
            var j = MiniJson.Parse(text) as Dictionary<string, object>;
            var data = j != null && j.TryGetValue("data", out var dl) ? dl as List<object> : null;
            if (data == null || data.Count == 0) return "응답에 이미지 없음: " + Short(text);
            var first = (Dictionary<string, object>)data[0];
            if (first.TryGetValue("b64_json", out var b64) && b64 is string s) { File.WriteAllBytes(outPng, Convert.FromBase64String(s)); return null; }
            if (first.TryGetValue("url", out var url) && url is string u) { File.WriteAllBytes(outPng, Http.GetByteArrayAsync(u).GetAwaiter().GetResult()); return null; }
            return "응답 형식 모름: " + Short(text);
        }

        static string GenerateImage(string key, string model, string prompt, string outPng)
        {
            string e = GenerateImage(key, model, prompt, outPng, true);
            if (e != null && e.Contains("background")) e = GenerateImage(key, model, prompt, outPng, false);   // 투명 배경 미지원 모델 → 회색 배경
            return e;
        }

        static string GenerateImage(string key, string model, string prompt, string outPng, bool transparent)
        {
            string body = "{\"model\":" + MiniJson.Quote(model) + ",\"prompt\":" + MiniJson.Quote(prompt) +
                          ",\"size\":\"1024x1024\",\"quality\":\"high\"" + (transparent ? ",\"background\":\"transparent\"" : "") + ",\"output_format\":\"png\",\"n\":1}";
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var res = Http.SendAsync(req).GetAwaiter().GetResult();
            string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) return $"HTTP {(int)res.StatusCode}: {Short(text)}";
            var j = MiniJson.Parse(text) as Dictionary<string, object>;
            var data = j != null && j.TryGetValue("data", out var dl) ? dl as List<object> : null;
            if (data == null || data.Count == 0) return "응답에 이미지 없음: " + Short(text);
            var first = (Dictionary<string, object>)data[0];
            if (first.TryGetValue("b64_json", out var b64) && b64 is string s) { File.WriteAllBytes(outPng, Convert.FromBase64String(s)); return null; }
            if (first.TryGetValue("url", out var url) && url is string u)
            {
                var bytes = Http.GetByteArrayAsync(u).GetAwaiter().GetResult();
                File.WriteAllBytes(outPng, bytes); return null;
            }
            return "응답 형식 모름: " + Short(text);
        }

        static string ImageTo3D(string key, string png, string outGlb, int faces)
        {
            string requestId;
            {
                var form = new MultipartFormDataContent();   // req 가 같이 정리함
                var img = new ByteArrayContent(File.ReadAllBytes(png));
                img.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(img, "image", Path.GetFileName(png));
                form.Add(new StringContent("tri"), "target_face_type");
                form.Add(new StringContent(faces.ToString()), "target_face_num");   // 게임용 폴리곤 예산 (몬스터 수백 마리 동시 표시)
                form.Add(new StringContent("true"), "generate_texture");
                form.Add(new StringContent("-1"), "seed");
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://openapi.ai.nc.com/3d/varco/v1/image-to-3d") { Content = form };
                req.Headers.Add("OPENAPI_KEY", key);
                using var res = Http.SendAsync(req).GetAwaiter().GetResult();
                string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!res.IsSuccessStatusCode) return $"요청 HTTP {(int)res.StatusCode}: {Short(text)}";
                var j = MiniJson.Parse(text) as Dictionary<string, object>;
                if (j == null || !j.TryGetValue("requestId", out var rid)) return "requestId 없음: " + Short(text);
                requestId = rid.ToString();
            }
            var deadline = DateTime.UtcNow.AddMinutes(12);
            while (true)
            {
                System.Threading.Thread.Sleep(5000);
                if (DateTime.UtcNow > deadline) return "12분 넘게 처리 중";
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://openapi.ai.nc.com/inference/result/" + requestId);
                req.Headers.Add("OPENAPI_KEY", key);
                using var res = Http.SendAsync(req).GetAwaiter().GetResult();
                string text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var j = MiniJson.Parse(text) as Dictionary<string, object>;
                string status = j != null && j.TryGetValue("status", out var st) ? st?.ToString() : null;
                if (status == "processing" || status == "pending" || status == "queued") continue;
                if (j != null && j.TryGetValue("model_url", out var mu) && mu is string url && url.Length > 0)
                {
                    var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
                    File.WriteAllBytes(outGlb, bytes);
                    return null;
                }
                return $"결과 상태 {status}: {Short(text)}";
            }
        }

        static string Short(string s) => s == null ? "" : (s.Length > 600 ? s.Substring(0, 600) + "…" : s);

        // ---------------- 넣기 (메인 스레드) ----------------

        static void ImportStep(Job job)
        {
            string dir = $"{GenDir}/{job.Name}";
            string glbAbs = Path.Combine(ProjectRoot, dir, job.Name + ".glb");
            if (!File.Exists(glbAbs)) { Debug.LogWarning($"[artgen] {job.Name}: glb 없음, 넣기 건너뜀"); return; }
            AssetDatabase.Refresh();
            var r = GlbImporter.Import(glbAbs, dir, job.Name, job.Height, job.Yaw);
            if (r.Error != null) throw new Exception(r.Error);
            Debug.Log($"[artgen] {job.Name}: 메시 {r.Mesh.vertexCount} 정점 / 원래 키 {r.RawHeight:F2} → {job.Height}m, yaw {job.Yaw}, 텍스처 {(r.Albedo ? "O" : "X")} 노멀 {(r.Normal ? "O" : "X")}");
            // 동작(영상→DINO)용 참조: 3D 모델 그대로 정면·옆 렌더 (원화를 따로 그리지 않음 → 디자인 100% 일치, 공짜)
            RenderView(r.Mesh, r.Material, Path.Combine(ProjectRoot, dir, job.Name + "_render_front.png"), 0f);
            RenderView(r.Mesh, r.Material, Path.Combine(ProjectRoot, dir, job.Name + "_render_side.png"), -90f);
            Debug.Log($"[artgen] {job.Name}: 정면·옆 렌더 저장 ({dir}/{job.Name}_render_front.png, _render_side.png)");

            var skin = AssetDatabase.LoadAssetAtPath<SkinSet>(SkinPath);
            if (skin == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SkinPath));
                skin = ScriptableObject.CreateInstance<SkinSet>();
                AssetDatabase.CreateAsset(skin, SkinPath);
            }
            if (job.Kind == Kind.Monster)
            {
                if (skin.MonsterMesh == null || skin.MonsterMesh.Length < 7) skin.MonsterMesh = new Mesh[7];
                if (skin.MonsterMaterial == null || skin.MonsterMaterial.Length < 7) skin.MonsterMaterial = new Material[7];
                if (skin.MonsterScale == null || skin.MonsterScale.Length < 7) skin.MonsterScale = new[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f };
                skin.MonsterMesh[job.Index] = r.Mesh;
                skin.MonsterMaterial[job.Index] = r.Material;
                skin.MonsterScale[job.Index] = 1f;
            }
            else
            {
                var prefab = GlbImporter.SavePrefab(r, dir, job.Name);
                if (job.Kind == Kind.Building)
                {
                    if (skin.BuildingPrefab == null || skin.BuildingPrefab.Length < 2) skin.BuildingPrefab = new GameObject[2];
                    skin.BuildingPrefab[job.Index] = prefab;
                }
                // 캐릭터는 뼈대를 심은 뒤에 연결 (지금은 프리팹만 만들어 둠)
            }
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
        }

        /// <summary>메시를 투명 배경 1024×1024 로 렌더 (직교 카메라, 부드러운 스튜디오 조명). yaw: 모델을 돌리는 각도 (-90 = 오른쪽 옆모습).</summary>
        static void RenderView(Mesh mesh, Material mat, string outPng, float yaw)
        {
            const int Size = 1024;
            const int Layer = 31;
            var root = new GameObject("ArtGenRender") { hideFlags = HideFlags.HideAndDontSave };
            var go = new GameObject("Model") { hideFlags = HideFlags.HideAndDontSave, layer = Layer };
            go.transform.SetParent(root.transform);
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var b = mesh.bounds;
            float h = b.size.y;

            var lights = new List<Light>();
            void AddLight(Vector3 euler, float intensity, Color c)
            {
                var lg = new GameObject("L") { hideFlags = HideFlags.HideAndDontSave };
                lg.transform.SetParent(root.transform);
                lg.transform.rotation = Quaternion.Euler(euler);
                var l = lg.AddComponent<Light>(); l.type = LightType.Directional; l.intensity = intensity; l.color = c; l.cullingMask = 1 << Layer; l.shadows = LightShadows.None;
                lights.Add(l);
            }
            AddLight(new Vector3(25f, 155f, 0f), 1.25f, new Color(1f, 0.97f, 0.92f));   // 앞 위 키 라이트
            AddLight(new Vector3(10f, -30f, 0f), 0.55f, new Color(0.8f, 0.86f, 1f));    // 뒤 림 라이트
            AddLight(new Vector3(-20f, 200f, 0f), 0.35f, Color.white);                  // 아래 채움

            var cg = new GameObject("C") { hideFlags = HideFlags.HideAndDontSave };
            cg.transform.SetParent(root.transform);
            var cam = cg.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = h * 0.55f;
            cam.transform.position = new Vector3(0f, h * 0.5f, 10f);
            cam.transform.rotation = Quaternion.Euler(0f, 180f, 0f);   // +Z 에서 모델 앞(+Z)을 봄
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.cullingMask = 1 << Layer;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 40f;

            var amb = RenderSettings.ambientLight; var ambMode = RenderSettings.ambientMode;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.32f, 0.33f, 0.36f);
            var rt = RenderTexture.GetTemporary(Size, Size, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            try
            {
                // 불투명 셰이더는 알파를 제대로 안 써서, 검은 배경·흰 배경 두 번 찍어 차이로 알파를 만든다 (차이 매트)
                Color[] Shot(Color bg)
                {
                    cam.backgroundColor = bg;
                    cam.targetTexture = rt;
                    cam.Render();
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    var t = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                    t.ReadPixels(new Rect(0, 0, Size, Size), 0, 0); t.Apply();
                    RenderTexture.active = prev;
                    var px = t.GetPixels();
                    UnityEngine.Object.DestroyImmediate(t);
                    return px;
                }
                var black = Shot(new Color(0f, 0f, 0f, 1f));
                var white = Shot(new Color(1f, 1f, 1f, 1f));
                var outPx = new Color[black.Length];
                for (int i = 0; i < outPx.Length; i++)
                {
                    var bk = black[i]; var wt = white[i];
                    float a = 1f - ((wt.r - bk.r) + (wt.g - bk.g) + (wt.b - bk.b)) / 3f;
                    a = Mathf.Clamp01(a);
                    outPx[i] = a > 0.004f ? new Color(Mathf.Clamp01(bk.r / a), Mathf.Clamp01(bk.g / a), Mathf.Clamp01(bk.b / a), a) : new Color(0f, 0f, 0f, 0f);
                }
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                tex.SetPixels(outPx); tex.Apply();
                File.WriteAllBytes(outPng, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
                RenderSettings.ambientLight = amb; RenderSettings.ambientMode = ambMode;
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ---------------- 보정값 (tuning.json) ----------------

        static Dictionary<string, object> LoadTuning()
        {
            string p = Path.Combine(ProjectRoot, TuningPath);
            if (!File.Exists(p)) return new Dictionary<string, object>();
            return MiniJson.Parse(File.ReadAllText(p)) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        static void ApplyTuning(Job job)
        {
            var t = LoadTuning();
            if (t.TryGetValue(job.Name, out var o) && o is Dictionary<string, object> d)
            {
                if (d.TryGetValue("yaw", out var y)) job.Yaw = Convert.ToSingle(y);
                if (d.TryGetValue("height", out var h)) job.Height = Convert.ToSingle(h);
            }
        }

        static void SetTuning(string name, string key, float value)
        {
            var t = LoadTuning();
            if (!(t.TryGetValue(name, out var o) && o is Dictionary<string, object> d)) { d = new Dictionary<string, object>(); t[name] = d; }
            d[key] = (double)value;
            var sb = new StringBuilder("{\n");
            bool first = true;
            foreach (var kv in t)
            {
                if (!first) sb.Append(",\n"); first = false;
                sb.Append("  ").Append(MiniJson.Quote(kv.Key)).Append(": {");
                bool f2 = true;
                foreach (var e in (Dictionary<string, object>)kv.Value)
                {
                    if (!f2) sb.Append(", "); f2 = false;
                    sb.Append(MiniJson.Quote(e.Key)).Append(": ").Append(Convert.ToDouble(e.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                sb.Append('}');
            }
            sb.Append("\n}\n");
            Directory.CreateDirectory(Path.Combine(ProjectRoot, GenDir));
            File.WriteAllText(Path.Combine(ProjectRoot, TuningPath), sb.ToString());
            Debug.Log($"[artgen] 보정 저장: {name} {key}={value}");
        }
    }
}
