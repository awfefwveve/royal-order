using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KnifeWar.Game;
using UnityEngine;
using UnityEngine.Networking;

namespace KnifeWar.Net
{
    /// <summary>
    /// 자체 런처 역할 (자동 업데이트). 스팀 없이 배포하므로 게임이 스스로 최신 버전을 받는다.
    ///
    /// 1) 백엔드 /client/version → 최신 버전, 패치 주소, 공지, 점검 여부
    /// 2) 내 버전(실행 파일 옆 version.txt)과 다르면 {패치주소}/{버전}/manifest.json 을 받는다
    /// 3) 파일별 크기·SHA-256을 비교해 바뀐 파일만 _update 폴더로 받는다 (받은 뒤 해시 재확인)
    /// 4) 게임을 끄고, 작은 PowerShell 스크립트가 파일을 덮어쓴 뒤 게임을 다시 켠다
    ///    (실행 중인 exe는 스스로 덮어쓸 수 없어서. Windows 기본 PowerShell만 사용 — 별도 프로그램 불필요)
    ///
    /// 설치 위치는 관리자 권한이 필요 없는 곳(%LocalAppData%\Programs\...)이어야 한다 → Deploy/installer 참고.
    /// 에디터·데디 서버에서는 동작하지 않는다.
    /// </summary>
    public sealed class GameUpdater
    {
        public enum State { Idle, Checking, Downloading, Applying, UpToDate, Failed, Skipped }

        public State Current { get; private set; } = State.Idle;
        public float Progress { get; private set; }          // 0~1
        public string Message { get; private set; } = "";
        public string Notice { get; private set; } = "";
        public bool Maintenance { get; private set; }
        public string LocalVersion { get; private set; } = "";
        public string LatestVersion { get; private set; } = "";
        public bool Busy => Current == State.Checking || Current == State.Downloading || Current == State.Applying;

        [Serializable] class VersionDto { public string version; public string patchBaseUrl; public string notice; public bool maintenance; }
        [Serializable] class FileEntry { public string p; public long s; public string h; }
        [Serializable] class Manifest { public string version; public FileEntry[] files; }
        [Serializable] class CacheEntry { public string p; public long s; public long t; public string h; }
        [Serializable] class Cache { public List<CacheEntry> items = new List<CacheEntry>(); }

        static string GameDir => Path.GetDirectoryName(Application.dataPath) ?? ".";
        static string UpdateDir => Path.Combine(GameDir, "_update");

        public static string ReadLocalVersion()
        {
            try
            {
                string f = Path.Combine(GameDir, "version.txt");
                return File.Exists(f) ? File.ReadAllText(f).Trim() : "";
            }
            catch { return ""; }
        }

        public IEnumerator Run(string backendUrl)
        {
            LocalVersion = ReadLocalVersion();
            if (Application.isEditor || Application.platform != RuntimePlatform.WindowsPlayer)
            {
                Current = State.Skipped;   // 에디터·다른 OS: 업데이트 안 함 (공지만 받아옴)
                yield return FetchVersion(backendUrl, v => { });
                yield break;
            }

            Current = State.Checking;
            Message = L.T("upd_checking");
            VersionDto info = null;
            yield return FetchVersion(backendUrl, v => info = v);
            if (info == null) { Current = State.Skipped; Message = ""; yield break; }   // 백엔드에 못 닿음 → 연습 모드는 가능
            LatestVersion = info.version ?? "";
            if (string.IsNullOrEmpty(LatestVersion) || LatestVersion == LocalVersion || string.IsNullOrEmpty(info.patchBaseUrl))
            {
                Current = State.UpToDate;
                Message = "";
                yield break;
            }

            // 매니페스트
            string baseUrl = info.patchBaseUrl.TrimEnd('/') + "/" + UnityWebRequest.EscapeURL(LatestVersion);
            Manifest manifest = null;
            using (var req = UnityWebRequest.Get(baseUrl + "/manifest.json"))
            {
                req.timeout = 20;
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success)
                    try { manifest = JsonUtility.FromJson<Manifest>(req.downloadHandler.text); } catch { }
            }
            if (manifest == null || manifest.files == null) { Fail(L.T("upd_failed_manifest")); yield break; }

            // 바뀐 파일 찾기 (해시 캐시로 매번 전체 해시 계산을 피함)
            Message = L.T("upd_scanning");
            var cache = LoadCache();
            var need = new List<FileEntry>();
            long total = 0;
            int scanned = 0;
            foreach (var f in manifest.files)
            {
                if (!SafePath(f.p)) continue;
                string local = Path.Combine(GameDir, f.p);
                if (!File.Exists(local) || new FileInfo(local).Length != f.s || !string.Equals(HashCached(local, f.p, cache), f.h, StringComparison.OrdinalIgnoreCase))
                {
                    need.Add(f);
                    total += f.s;
                }
                if (++scanned % 20 == 0) { Progress = (float)scanned / manifest.files.Length * 0.1f; yield return null; }
            }
            SaveCache(cache);

            if (need.Count == 0)
            {
                WriteVersion(LatestVersion);
                Current = State.UpToDate;
                Message = "";
                yield break;
            }

            // 받기
            Current = State.Downloading;
            string staging = Path.Combine(UpdateDir, "files");
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); Directory.CreateDirectory(staging); }
            catch (Exception e) { Fail(e.Message); yield break; }

            long done = 0;
            foreach (var f in need)
            {
                string dest = Path.Combine(staging, f.p);
                Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? staging);
                bool ok = false;
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    using (var req = new UnityWebRequest(baseUrl + "/files/" + EscapePath(f.p), "GET"))
                    {
                        req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                        req.timeout = 600;
                        var op = req.SendWebRequest();
                        while (!op.isDone)
                        {
                            Progress = 0.1f + 0.85f * (float)((done + (long)(req.downloadProgress * f.s)) / (double)Math.Max(1, total));
                            Message = L.T("upd_downloading", LatestVersion, (int)(Progress * 100f), FormatSize(total));
                            yield return null;
                        }
                        ok = req.result == UnityWebRequest.Result.Success && File.Exists(dest)
                             && string.Equals(Sha256(dest), f.h, StringComparison.OrdinalIgnoreCase);
                    }
                    if (!ok) yield return new WaitForSecondsRealtime(1.5f);
                }
                if (!ok) { Fail(L.T("upd_failed_file", f.p)); yield break; }
                done += f.s;
            }

            // 적용: 게임을 끄고 스크립트가 덮어쓴 뒤 다시 켠다
            Current = State.Applying;
            Progress = 1f;
            Message = L.T("upd_applying");
            yield return new WaitForSecondsRealtime(0.5f);
            try
            {
                File.WriteAllText(Path.Combine(staging, "version.txt"), LatestVersion);
                string script = Path.Combine(UpdateDir, "apply.ps1");
                File.WriteAllText(script, ApplyScript, new UTF8Encoding(true));
                // 실행 파일 이름 = "<이름>_Data" 폴더 이름에서 _Data를 뺀 것 (IL2CPP에서도 안전)
                string dataName = Path.GetFileName(Application.dataPath);
                string exeName = dataName.EndsWith("_Data") ? dataName.Substring(0, dataName.Length - 5) + ".exe" : "KnifeWar.exe";
                string exe = Path.Combine(GameDir, exeName);
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -ProcId {System.Diagnostics.Process.GetCurrentProcess().Id} -Game \"{GameDir}\" -Src \"{staging}\" -Exe \"{exe}\"")
                { UseShellExecute = false, CreateNoWindow = true };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception e) { Fail(e.Message); yield break; }
            PlayerPrefs.Save();
            Application.Quit();
        }

        /// <summary>업데이트 실패 후 "그냥 계속" (연습 모드 등은 가능, 온라인은 서버가 버전으로 거부할 수 있음).</summary>
        public void SkipFailed()
        {
            if (Current == State.Failed) { Current = State.Skipped; Message = ""; }
        }

        IEnumerator FetchVersion(string backendUrl, Action<VersionDto> done)
        {
            if (string.IsNullOrEmpty(backendUrl)) { done(null); yield break; }
            using (var req = UnityWebRequest.Get(backendUrl.TrimEnd('/') + "/client/version"))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                VersionDto v = null;
                if (req.result == UnityWebRequest.Result.Success)
                    try { v = JsonUtility.FromJson<VersionDto>(req.downloadHandler.text); } catch { }
                if (v != null)
                {
                    Notice = v.notice ?? "";
                    Maintenance = v.maintenance;
                }
                done(v);
            }
        }

        void Fail(string why)
        {
            Current = State.Failed;
            Message = L.T("upd_failed", why);
            Debug.LogWarning("[KnifeWar] 업데이트 실패: " + why);
        }

        // 덮어쓰기 스크립트: 게임 종료를 기다림 → 복사 (잠긴 파일은 몇 번 재시도) → 정리 → 다시 실행
        const string ApplyScript = @"param([int]$ProcId, [string]$Game, [string]$Src, [string]$Exe)
$ErrorActionPreference = 'Stop'
try { Wait-Process -Id $ProcId -Timeout 60 -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Milliseconds 400
$ok = $false
for ($i = 0; $i -lt 10 -and -not $ok; $i++) {
  try { Copy-Item -Path (Join-Path $Src '*') -Destination $Game -Recurse -Force; $ok = $true }
  catch { Start-Sleep -Seconds 1 }
}
if ($ok) { Remove-Item -Path $Src -Recurse -Force -ErrorAction SilentlyContinue }
Start-Process -FilePath $Exe -WorkingDirectory $Game
";

        static bool SafePath(string p) =>
            !string.IsNullOrEmpty(p) && !p.Contains("..") && !Path.IsPathRooted(p) && !p.StartsWith("_update", StringComparison.OrdinalIgnoreCase);

        static string EscapePath(string p)
        {
            var parts = p.Replace('\\', '/').Split('/');
            for (int i = 0; i < parts.Length; i++) parts[i] = Uri.EscapeDataString(parts[i]);
            return string.Join("/", parts);
        }

        static string FormatSize(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.0} GB" : $"{b / (double)(1 << 20):0} MB";

        static void WriteVersion(string v)
        {
            try { File.WriteAllText(Path.Combine(GameDir, "version.txt"), v); } catch { }
        }

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (var x in h) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        static string HashCached(string local, string rel, Cache cache)
        {
            var fi = new FileInfo(local);
            long t = fi.LastWriteTimeUtc.Ticks;
            var e = cache.items.Find(x => x.p == rel);
            if (e != null && e.s == fi.Length && e.t == t) return e.h;
            string h = Sha256(local);
            if (e == null) cache.items.Add(new CacheEntry { p = rel, s = fi.Length, t = t, h = h });
            else { e.s = fi.Length; e.t = t; e.h = h; }
            return h;
        }

        static Cache LoadCache()
        {
            try
            {
                string f = Path.Combine(UpdateDir, "hashcache.json");
                if (File.Exists(f)) return JsonUtility.FromJson<Cache>(File.ReadAllText(f)) ?? new Cache();
            }
            catch { }
            return new Cache();
        }

        static void SaveCache(Cache c)
        {
            try
            {
                Directory.CreateDirectory(UpdateDir);
                File.WriteAllText(Path.Combine(UpdateDir, "hashcache.json"), JsonUtility.ToJson(c));
            }
            catch { }
        }
    }
}
