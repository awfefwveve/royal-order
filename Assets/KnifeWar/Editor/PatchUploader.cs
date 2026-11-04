using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 패치·설치 파일을 Cloudflare R2(patch.astrelgames.com)에 올린다. 서버 없이 CDN만으로 배포·업데이트.
    ///   royalorder/blobs/{sha256}          파일 내용 (처음 보는 것만 올림, 기록: Builds/Patch/uploaded.txt)
    ///   royalorder/{버전}/manifest.json    파일 목록
    ///   royalorder/RoyalOrderSetup.exe     설치 파일 (Inno Setup이 있으면) / 없으면 RoyalOrder.zip
    ///   royalorder/latest.json             최신 버전 — 맨 마지막에 올려서, 다 올라간 뒤에만 플레이어가 받게 함
    /// 키는 Deploy/secrets.env (커밋 안 됨): CLOUDFLARE_API_TOKEN (R2 쓰기 권한), CLOUDFLARE_ACCOUNT_ID, R2_BUCKET(기본 royalorder-patch)
    /// </summary>
    public static class PatchUploader
    {
        const string Prefix = "royalorder";
        const string PublicRoot = "https://patch.astrelgames.com/" + Prefix;
        static readonly string[] Origins = { "https://astrelgames.com", "https://www.astrelgames.com" };

        public static bool Upload(string ver)
        {
            string patchDir = Path.GetFullPath($"Builds/Patch/{ver}");
            string gameDir = Path.GetFullPath($"Builds/Release/{ver}/game");
            if (!File.Exists(Path.Combine(patchDir, "manifest.json"))) { Debug.LogError($"[deploy] {patchDir}/manifest.json 이 없어요. 먼저 출시 빌드를 하세요."); return false; }

            var env = ReadSecrets();
            env.TryGetValue("CLOUDFLARE_API_TOKEN", out var token);
            env.TryGetValue("CLOUDFLARE_ACCOUNT_ID", out var account);
            string bucket = env.TryGetValue("R2_BUCKET", out var b) && b.Length > 0 ? b : "royalorder-patch";
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(account))
            {
                Debug.LogError("[deploy] Deploy/secrets.env 에 CLOUDFLARE_API_TOKEN, CLOUDFLARE_ACCOUNT_ID 가 필요해요.");
                return false;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            string api = $"https://api.cloudflare.com/client/v4/accounts/{account}/r2/buckets/{bucket}";

            try
            {
                // 1) 파일 내용: 처음 보는 해시만
                string recordPath = Path.GetFullPath("Builds/Patch/uploaded.txt");
                var uploaded = new HashSet<string>(File.Exists(recordPath) ? File.ReadAllLines(recordPath) : Array.Empty<string>());
                var blobs = Directory.GetFiles(Path.Combine(patchDir, "blobs"));
                int i = 0, sent = 0;
                long bytes = 0;
                foreach (var f in blobs)
                {
                    i++;
                    string hash = Path.GetFileName(f);
                    if (uploaded.Contains(hash)) continue;
                    if (Cancel($"파일 올리는 중 {i}/{blobs.Length}", (float)i / blobs.Length)) return false;
                    Put(http, api, $"{Prefix}/blobs/{hash}", f, "application/octet-stream");
                    uploaded.Add(hash);
                    File.AppendAllText(recordPath, hash + "\n");
                    sent++;
                    bytes += new FileInfo(f).Length;
                }
                Debug.Log($"[deploy] 새 파일 {sent}개 ({bytes / (1024 * 1024)}MB) 올림, 나머지 {blobs.Length - sent}개는 이미 있음");

                // 2) 파일 목록
                Put(http, api, $"{Prefix}/{ver}/manifest.json", Path.Combine(patchDir, "manifest.json"), "application/json");

                // 3) 처음 설치 파일
                Cancel("설치 파일 만드는 중", 0.95f);
                string installerUrl = MakeAndUploadInstaller(http, api, ver, gameDir);

                // 4) 최신 버전 (마지막)
                string latest = $"{{\"version\":\"{ver}\",\"installer\":\"{installerUrl}\",\"date\":\"{DateTime.UtcNow:yyyy-MM-dd}\"}}";
                PutBytes(http, api, $"{Prefix}/latest.json", Encoding.UTF8.GetBytes(latest), "application/json", "no-cache, max-age=0");
                TryPurge(http, account, new[] { $"{PublicRoot}/latest.json", $"{PublicRoot}/RoyalOrderSetup.exe", $"{PublicRoot}/RoyalOrder.zip" });

                // 5) 홈페이지에서 latest.json 을 읽을 수 있게 (한 번 해 두면 됨, 실패해도 배포는 끝난 것)
                TrySetCors(http, api);

                Debug.Log($"[deploy] 배포 끝 v{ver} → {PublicRoot}/latest.json\n  설치 파일: {installerUrl}\n  플레이어는 다음에 게임을 켤 때 자동으로 업데이트돼요.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[deploy] 올리기 실패: {e.Message}\n  (403이면 Cloudflare API 토큰에 'R2 편집' 권한이 있는지 확인)");
                return false;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static string MakeAndUploadInstaller(HttpClient http, string api, string ver, string gameDir)
        {
            string iscc = FindIscc();
            if (iscc != null)
            {
                var psi = new ProcessStartInfo(iscc, $"/Q /DMyVersion={ver} \"{Path.GetFullPath("Deploy/installer/KnifeWar.iss")}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    string exe = Path.GetFullPath($"Deploy/installer/Output/RoyalOrderSetup-{ver}.exe");
                    if (p.ExitCode == 0 && File.Exists(exe))
                    {
                        Put(http, api, $"{Prefix}/RoyalOrderSetup-{ver}.exe", exe, "application/octet-stream");
                        Put(http, api, $"{Prefix}/RoyalOrderSetup.exe", exe, "application/octet-stream");
                        return $"{PublicRoot}/RoyalOrderSetup.exe";
                    }
                    Debug.LogWarning($"[deploy] Inno Setup 컴파일 실패 → zip으로 대신 올림\n{err}");
                }
            }
            else Debug.LogWarning("[deploy] Inno Setup이 없어서 zip으로 올려요 (설치 파일을 원하면 https://jrsoftware.org/isinfo.php 무료 설치)");

            string zip = Path.GetFullPath($"Builds/Release/{ver}/RoyalOrder-{ver}.zip");
            if (File.Exists(zip)) File.Delete(zip);
            // 압축은 윈도우 기본 PowerShell로 (에디터 어셈블리 참조 문제 없이)
            var z = new ProcessStartInfo("powershell", $"-NoProfile -Command \"Compress-Archive -Path '{gameDir}\\*' -DestinationPath '{zip}' -Force\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var p = Process.Start(z)) { string err = p.StandardError.ReadToEnd(); p.WaitForExit(); if (!File.Exists(zip)) throw new Exception("zip 만들기 실패: " + err); }
            Put(http, api, $"{Prefix}/RoyalOrder.zip", zip, "application/zip");
            return $"{PublicRoot}/RoyalOrder.zip";
        }

        static string FindIscc()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                @"C:\",
            };
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r) || !Directory.Exists(r)) continue;
                foreach (var d in Directory.GetDirectories(r, "Inno Setup*"))
                {
                    string p = Path.Combine(d, "ISCC.exe");
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        static void Put(HttpClient http, string api, string key, string file, string type)
        {
            Send(http, api, key, () =>
            {
                var c = new StreamContent(File.OpenRead(file));
                c.Headers.ContentType = new MediaTypeHeaderValue(type);
                return c;
            });
        }

        static void PutBytes(HttpClient http, string api, string key, byte[] data, string type, string cacheControl = null)
        {
            Send(http, api, key, () =>
            {
                var c = new ByteArrayContent(data);
                c.Headers.ContentType = new MediaTypeHeaderValue(type);
                return c;
            }, cacheControl);
        }

        /// <summary>CDN에 남은 옛 사본 지우기 (토큰에 Zone 읽기 + 캐시 지우기 권한이 있을 때만, 없으면 조용히 넘어감).</summary>
        static void TryPurge(HttpClient http, string account, string[] urls)
        {
            try
            {
                var z = http.GetAsync("https://api.cloudflare.com/client/v4/zones?name=astrelgames.com").Result;
                string body = z.Content.ReadAsStringAsync().Result;
                int i = body.IndexOf("\"id\":\"", StringComparison.Ordinal);
                if (!z.IsSuccessStatusCode || i < 0) { Debug.Log("[deploy] CDN 캐시 지우기 건너뜀 (토큰에 Zone 권한 없음) — latest.json 은 캐시 금지로 올렸음"); return; }
                string zone = body.Substring(i + 6, 32);
                var sb = new StringBuilder("{\"files\":[");
                for (int k = 0; k < urls.Length; k++) sb.Append(k > 0 ? "," : "").Append('"').Append(urls[k]).Append('"');
                sb.Append("]}");
                var res = http.PostAsync($"https://api.cloudflare.com/client/v4/zones/{zone}/purge_cache", new StringContent(sb.ToString(), Encoding.UTF8, "application/json")).Result;
                Debug.Log(res.IsSuccessStatusCode ? "[deploy] CDN 캐시 지움" : $"[deploy] CDN 캐시 지우기 실패 {(int)res.StatusCode}");
            }
            catch (Exception e) { Debug.LogWarning("[deploy] CDN 캐시 지우기 실패: " + e.Message); }
        }

        static void Send(HttpClient http, string api, string key, Func<HttpContent> make, string cacheControl = null)
        {
            for (int attempt = 1; ; attempt++)
            {
                using var content = make();   // 다시 보낼 때마다 새로 (스트림은 한 번만 읽힘)
                using var req = new HttpRequestMessage(HttpMethod.Put, $"{api}/objects/{Uri.EscapeDataString(key).Replace("%2F", "/")}") { Content = content };
                if (cacheControl != null) req.Headers.TryAddWithoutValidation("Cache-Control", cacheControl);
                var res = http.SendAsync(req).Result;
                if (res.IsSuccessStatusCode) return;
                string body = res.Content.ReadAsStringAsync().Result;
                if (attempt >= 3 || (int)res.StatusCode == 401 || (int)res.StatusCode == 403)
                    throw new Exception($"{key}: HTTP {(int)res.StatusCode} {body}");
            }
        }

        static void TrySetCors(HttpClient http, string api)
        {
            try
            {
                var sb = new StringBuilder("{\"rules\":[{\"allowed\":{\"methods\":[\"GET\",\"HEAD\"],\"origins\":[");
                for (int i = 0; i < Origins.Length; i++) sb.Append(i > 0 ? "," : "").Append('"').Append(Origins[i]).Append('"');
                sb.Append("]},\"maxAgeSeconds\":3600}]}");
                var res = http.PutAsync($"{api}/cors", new StringContent(sb.ToString(), Encoding.UTF8, "application/json")).Result;
                if (!res.IsSuccessStatusCode) Debug.LogWarning($"[deploy] CORS 설정 실패 (홈페이지에 버전 표시만 안 됨): {(int)res.StatusCode}");
            }
            catch (Exception e) { Debug.LogWarning("[deploy] CORS 설정 실패: " + e.Message); }
        }

        static bool Cancel(string msg, float p) => EditorUtility.DisplayCancelableProgressBar("ROYAL ORDER 배포", msg, p);

        static Dictionary<string, string> ReadSecrets()
        {
            var d = new Dictionary<string, string>();
            string path = Path.GetFullPath("Deploy/secrets.env");
            if (!File.Exists(path)) return d;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"');
            }
            return d;
        }
    }
}
