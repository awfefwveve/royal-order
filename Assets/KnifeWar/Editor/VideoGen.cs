using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 동작 영상 생성 (fal 큐 API). 두 시점 시작 그림(정면 | 옆, XSXB start_frame) → 영상 AI → mp4.
    /// 명령: Logs/claude_videogen.txt 에 "이름 동작 [model=max|h3|seedance] [res=768P] [dur=5]"
    ///   시작 그림: AItools/runs/이름/mocap/start_frame/start_frame.png
    ///   프롬프트: AItools/runs/이름/prompts/동작.txt  (사람이 OK 한 문장만 씀)
    ///   결과:     AItools/runs/이름/mocap/videos/동작_N/source.mp4 (+ request.json)
    /// 키: Deploy/secrets.env 의 FAL_KEY (코드·로그에 안 남김). 한 번 실행 = 영상 1개 = 과금 1회.
    /// </summary>
    public static class VideoGen
    {
        static readonly Dictionary<string, string> Models = new Dictionary<string, string>
        {
            { "max", "minimax/h3-max/image-to-video" },
            { "h3", "minimax/h3/image-to-video" },
            { "seedance", "bytedance/seedance-2.5/image-to-video" },
        };

        static Task<string> _task;
        static string _label;

        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        static string AiRoot => Path.GetFullPath(Path.Combine(ProjectRoot, "..", "AItools"));

        [MenuItem("KnifeWar/아트/동작 영상 생성 (Logs/claude_videogen.txt)", priority = 31)]
        public static void RunFromFile()
        {
            string path = Path.Combine(ProjectRoot, "Logs/claude_videogen.txt");
            if (!File.Exists(path)) { Debug.LogError("[videogen] 명령 파일 없음"); return; }
            string args = File.ReadAllText(path).Trim();
            File.Delete(path);
            if (_task != null && !_task.IsCompleted) { Debug.LogError("[videogen] 이미 하나 만드는 중 — 끝난 뒤 다시"); return; }

            var parts = args.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { Debug.LogError("[videogen] 사용법: 이름 동작 [model=max] [res=768P] [dur=5]"); return; }
            string name = parts[0], motion = parts[1], model = "max", res = "768P", dur = "5";
            for (int i = 2; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("model=")) model = parts[i].Substring(6);
                else if (parts[i].StartsWith("res=")) res = parts[i].Substring(4);
                else if (parts[i].StartsWith("dur=")) dur = parts[i].Substring(4);
            }
            if (!Models.TryGetValue(model, out var endpoint)) { Debug.LogError("[videogen] 모르는 모델: " + model); return; }

            string run = Path.Combine(AiRoot, "runs", name);
            string start = Path.Combine(run, "mocap", "start_frame", "start_frame.png");
            string promptFile = Path.Combine(run, "prompts", motion + ".txt");
            if (!File.Exists(start)) { Debug.LogError("[videogen] 시작 그림 없음: " + start); return; }
            if (!File.Exists(promptFile)) { Debug.LogError("[videogen] 프롬프트 없음 (OK 받은 문장을 먼저 저장): " + promptFile); return; }
            string prompt = File.ReadAllText(promptFile).Trim();

            string videos = Path.Combine(run, "mocap", "videos");
            Directory.CreateDirectory(videos);
            int n = 1; while (Directory.Exists(Path.Combine(videos, $"{motion}_{n}"))) n++;
            string outDir = Path.Combine(videos, $"{motion}_{n}");

            string key = ReadSecret("FAL_KEY");
            if (string.IsNullOrEmpty(key)) { Debug.LogError("[videogen] FAL_KEY 없음 (Deploy/secrets.env)"); return; }

            _label = $"{name}/{motion}_{n} ({endpoint}, {res}, {dur}s)";
            Debug.Log("[videogen] 시작: " + _label);
            _task = Task.Run(() => Generate(key, endpoint, start, prompt, res, dur, outDir, model));
            EditorApplication.update += Watch;
        }

        static void Watch()
        {
            if (_task == null || !_task.IsCompleted) return;
            EditorApplication.update -= Watch;
            string err = _task.IsFaulted ? (_task.Exception?.InnerException ?? _task.Exception)?.ToString() : _task.Result;
            if (string.IsNullOrEmpty(err)) Debug.Log("[videogen] 완료: " + _label);
            else Debug.LogError("[videogen] 실패: " + _label + " — " + Short(err));
        }

        static string ReadSecret(string name)
        {
            string p = Path.Combine(ProjectRoot, "Deploy/secrets.env");
            if (!File.Exists(p)) return null;
            foreach (var line in File.ReadAllLines(p))
            {
                var t = line.Trim();
                if (t.StartsWith(name + "=")) return t.Substring(name.Length + 1).Trim().Trim('"', '\'');
            }
            return null;
        }

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        static string Generate(string key, string endpoint, string startPng, string prompt, string res, string dur, string outDir, string model)
        {
            string dataUri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(startPng));
            var sb = new StringBuilder("{");
            sb.Append("\"prompt\":").Append(MiniJson.Quote(prompt));
            sb.Append(",\"image_url\":").Append(MiniJson.Quote(dataUri));
            if (model == "seedance")
            {
                sb.Append(",\"duration\":").Append(MiniJson.Quote(dur));
                sb.Append(",\"resolution\":\"720p\",\"aspect_ratio\":\"16:9\",\"generate_audio\":false");
            }
            else
            {
                sb.Append(",\"duration\":").Append(dur);
                sb.Append(",\"resolution\":").Append(MiniJson.Quote(res));
                sb.Append(",\"prompt_expansion_mode\":\"disabled\"");   // 프롬프트를 그대로 (모델이 고쳐 쓰지 않게)
            }
            sb.Append('}');

            Dictionary<string, object> sub;
            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://queue.fal.run/" + endpoint))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
                req.Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json");
                using var res1 = Http.SendAsync(req).GetAwaiter().GetResult();
                string text = res1.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!res1.IsSuccessStatusCode) return $"제출 HTTP {(int)res1.StatusCode}: {Short(text)}";
                sub = MiniJson.Parse(text) as Dictionary<string, object>;
            }
            if (sub == null || !sub.ContainsKey("request_id")) return "request_id 없음";
            string statusUrl = sub.TryGetValue("status_url", out var su) ? su as string : null;
            string responseUrl = sub.TryGetValue("response_url", out var ru) ? ru as string : null;
            if (statusUrl == null || responseUrl == null) return "status/response 주소 없음";

            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (true)
            {
                System.Threading.Thread.Sleep(5000);
                if (DateTime.UtcNow > deadline) return "20분 넘게 걸림 (fal 대시보드 Request History 확인)";
                using var req = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
                using var r = Http.SendAsync(req).GetAwaiter().GetResult();
                var st = MiniJson.Parse(r.Content.ReadAsStringAsync().GetAwaiter().GetResult()) as Dictionary<string, object>;
                string status = st != null && st.TryGetValue("status", out var s) ? s as string : null;
                if (status == "COMPLETED") break;
                if (status != "IN_QUEUE" && status != "IN_PROGRESS") return "상태 " + status;
            }

            string resultText;
            using (var req = new HttpRequestMessage(HttpMethod.Get, responseUrl))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
                using var r = Http.SendAsync(req).GetAwaiter().GetResult();
                resultText = r.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!r.IsSuccessStatusCode) return $"결과 HTTP {(int)r.StatusCode}: {Short(resultText)}";
            }
            var result = MiniJson.Parse(resultText) as Dictionary<string, object>;
            var video = result != null && result.TryGetValue("video", out var v) ? v as Dictionary<string, object> : null;
            string url = video != null && video.TryGetValue("url", out var u) ? u as string : null;
            if (string.IsNullOrEmpty(url)) return "영상 주소 없음: " + Short(resultText);

            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "source.mp4"), Http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(outDir, "request.json"),
                "{\"endpoint\":" + MiniJson.Quote(endpoint) + ",\"prompt\":" + MiniJson.Quote(prompt) + ",\"result\":" + resultText + "}");
            return null;
        }

        static string Short(string s) => s == null ? "" : (s.Length > 500 ? s.Substring(0, 500) + "…" : s);
    }
}
