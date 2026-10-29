using System.Collections.Generic;
using System.IO;
using System.Linq;
using KnifeWar.Bench;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>유니티 상단 메뉴 "KnifeWar" — 벤치 씬 생성과 빌드를 버튼 하나로.</summary>
    public static class BenchMenu
    {
        const string Root = "Assets/KnifeWar";
        const string ScenePath = Root + "/Scenes/MonsterBench.unity";

        [MenuItem("KnifeWar/1. 몬스터 벤치 씬 만들기", priority = 1)]
        public static void CreateScene()
        {
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(Root + "/Materials");
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 500f;
            camGo.transform.position = new Vector3(0f, 38f, -62f);
            camGo.transform.LookAt(Vector3.zero);

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.None;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var bridge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bridge.name = "Bridge (120m x 4m)";
            bridge.transform.localScale = new Vector3(120f, 0.5f, 4.6f);
            Object.DestroyImmediate(bridge.GetComponent<Collider>());

            var runner = new GameObject("BenchRunner").AddComponent<BenchRunner>();
            runner.Team0Material = GetOrCreateMaterial("Monster_Team0", new Color(0.25f, 0.45f, 0.95f));
            runner.Team1Material = GetOrCreateMaterial("Monster_Team1", new Color(0.90f, 0.30f, 0.25f));
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            runner.MonsterMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(tmp);

            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuildSettings();
            Selection.activeGameObject = runner.gameObject;
            Debug.Log($"[KnifeWar] 벤치 씬 생성: {ScenePath} — Play를 누르면 측정이 시작됩니다 (BenchRunner에서 모드 선택).");
        }

        [MenuItem("KnifeWar/2. 서버 벤치 빌드 (Windows 헤드리스)", priority = 20)]
        public static void BuildServerWindows() =>
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Server, "Builds/ServerBench/ServerBench.exe");

        [MenuItem("KnifeWar/3. 서버 벤치 빌드 (Linux 헤드리스)", priority = 21)]
        public static void BuildServerLinux() =>
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Builds/ServerBenchLinux/ServerBench.x86_64");

        [MenuItem("KnifeWar/4. 클라 렌더 벤치 빌드 (Windows)", priority = 22)]
        public static void BuildClientWindows() =>
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "Builds/ClientBench/ClientBench.exe");

        [MenuItem("KnifeWar/결과 폴더 열기", priority = 40)]
        public static void OpenResults() => EditorUtility.RevealInFinder(Application.persistentDataPath + "/");

        // ------------------------------------------------------------------

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string path)
        {
            if (!File.Exists(ScenePath)) CreateScene();

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = path,
                target = target,
                targetGroup = BuildTargetGroup.Standalone,
                subtarget = (int)subtarget,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result == BuildResult.Succeeded)
            {
                Debug.Log($"[KnifeWar] 빌드 성공: {path} ({report.summary.totalSize / (1024 * 1024)}MB)");
                EditorUtility.RevealInFinder(path);
            }
            else
            {
                Debug.LogError($"[KnifeWar] 빌드 실패: {report.summary.result}. " +
                               "서버 빌드는 Unity Hub에서 해당 플랫폼의 'Dedicated Server' 모듈 설치가 필요합니다.");
            }
        }

        static Material GetOrCreateMaterial(string name, Color color)
        {
            string path = $"{Root}/Materials/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var mat = new Material(shader) { enableInstancing = true, color = color };
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Any(s => s.path == ScenePath)) return;
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
