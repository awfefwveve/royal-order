using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 자체 배포용 빌드·패치 (스팀 없이).
    ///   1) 버전을 하나 올리고 (예: 0.1.3 → 0.1.4) Windows 빌드 → Builds/Release/{버전}/game
    ///   2) 파일별 SHA-256 목록(manifest.json) + 파일 복사본 → Builds/Patch/{버전}
    ///   3) Builds/Patch/{버전} 폴더를 CDN의 {PatchBaseUrl}/{버전}/ 에 올리고, 백엔드 최신 버전을 바꾸면
    ///      모든 플레이어 게임이 다음 실행 때 바뀐 파일만 받아 스스로 업데이트한다.
    /// 처음 설치 파일은 Deploy/installer/KnifeWar.iss (Inno Setup)로 Builds/Release/{버전}/game 을 묶는다.
    /// </summary>
    public static class ReleaseMenu
    {
        const string ScenePath = "Assets/KnifeWar/Scenes/Game.unity";
        static readonly string[] Exclude = { "version.txt", "kw_launch.txt" };

        [MenuItem("KnifeWar/배포/출시 빌드 + 패치 만들기 (버전 +1)", priority = 100)]
        public static void BuildRelease()
        {
            if (!File.Exists(ScenePath)) { Debug.LogError("[KnifeWar] 게임 씬이 없어요."); return; }
            string ver = BumpVersion(PlayerSettings.bundleVersion);
            if (!EditorUtility.DisplayDialog("출시 빌드", $"버전 {PlayerSettings.bundleVersion} → {ver}\n빌드하고 패치 파일을 만들까요?", "만들기", "취소")) return;
            PlayerSettings.bundleVersion = ver;
            AssetDatabase.SaveAssets();

            string gameDir = Path.GetFullPath($"Builds/Release/{ver}/game");
            if (Directory.Exists(gameDir)) Directory.Delete(gameDir, true);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = Path.Combine(gameDir, "KnifeWar.exe"),
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded) { Debug.LogError($"[KnifeWar] 빌드 실패: {report.summary.result}"); return; }

            // 출시에 넣으면 안 되는 것 정리
            foreach (var d in Directory.GetDirectories(gameDir, "*DoNotShip*")) Directory.Delete(d, true);
            foreach (var f in Directory.GetFiles(gameDir, "kw_log_*.txt")) File.Delete(f);
            File.WriteAllText(Path.Combine(gameDir, "version.txt"), ver);

            MakePatch(gameDir, ver);
        }

        [MenuItem("KnifeWar/배포/패치만 다시 만들기 (마지막 출시 빌드)", priority = 101)]
        public static void RemakePatch()
        {
            string ver = PlayerSettings.bundleVersion;
            string gameDir = Path.GetFullPath($"Builds/Release/{ver}/game");
            if (!Directory.Exists(gameDir)) { Debug.LogError($"[KnifeWar] {gameDir} 가 없어요. 먼저 출시 빌드를 하세요."); return; }
            MakePatch(gameDir, ver);
        }

        static void MakePatch(string gameDir, string ver)
        {
            string patchDir = Path.GetFullPath($"Builds/Patch/{ver}");
            if (Directory.Exists(patchDir)) Directory.Delete(patchDir, true);
            string filesDir = Path.Combine(patchDir, "files");
            Directory.CreateDirectory(filesDir);

            var sb = new StringBuilder();
            sb.Append("{\"version\":\"").Append(ver).Append("\",\"files\":[");
            bool first = true;
            long total = 0;
            int count = 0;
            foreach (var full in Directory.GetFiles(gameDir, "*", SearchOption.AllDirectories))
            {
                string rel = full.Substring(gameDir.Length + 1).Replace('\\', '/');
                if (Array.IndexOf(Exclude, rel) >= 0 || rel.StartsWith("_update/")) continue;
                long size = new FileInfo(full).Length;
                string hash = Sha256(full);
                string dest = Path.Combine(filesDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.Copy(full, dest, true);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"p\":\"").Append(rel.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\"s\":").Append(size).Append(",\"h\":\"").Append(hash).Append("\"}");
                total += size;
                count++;
            }
            sb.Append("]}");
            File.WriteAllText(Path.Combine(patchDir, "manifest.json"), sb.ToString());

            Debug.Log($"[KnifeWar] 패치 완료 v{ver}: 파일 {count}개, {total / (1024 * 1024)}MB\n" +
                      $"  1) {patchDir} 폴더를 CDN의 (PatchBaseUrl)/{ver}/ 로 올리기\n" +
                      $"  2) 백엔드 최신 버전을 {ver}로 바꾸기 (POST /admin/client {{\"version\":\"{ver}\"}} 또는 KW_CLIENT_VERSION)\n" +
                      $"  처음 설치 파일: Deploy/installer/KnifeWar.iss 를 Inno Setup으로 컴파일 (Version={ver})");
            EditorUtility.RevealInFinder(patchDir);
        }

        static string BumpVersion(string v)
        {
            var p = (string.IsNullOrEmpty(v) ? "0.1.0" : v).Split('.');
            var nums = new List<int>();
            foreach (var x in p) nums.Add(int.TryParse(x, out int n) ? n : 0);
            while (nums.Count < 3) nums.Add(0);
            nums[nums.Count - 1]++;
            return string.Join(".", nums);
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var h = sha.ComputeHash(fs);
                var sb = new StringBuilder(64);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
