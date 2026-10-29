using System.Collections.Generic;
using System.IO;
using System.Linq;
using KnifeWar.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace KnifeWar.EditorTools
{
    /// <summary>외나무다리 게임 씬을 자동으로 만든다. 다시 실행하면 새로 덮어쓴다.</summary>
    public static class GameSceneBuilder
    {
        const string Root = "Assets/KnifeWar";
        const string ScenePath = Root + "/Scenes/Game.unity";
        const string MatDir = Root + "/Materials";

        [MenuItem("KnifeWar/게임 씬 만들기 (외나무다리 6:6)", priority = 0)]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Directory.CreateDirectory(Root + "/Scenes");
            Directory.CreateDirectory(MatDir);
            AssetDatabase.Refresh();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 빛
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.shadows = LightShadows.Soft;
            sun.color = new Color(1f, 0.95f, 0.85f);
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.75f);
            RenderSettings.ambientEquatorColor = new Color(0.4f, 0.42f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.12f, 0.12f, 0.16f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.fogStartDistance = 60f;
            RenderSettings.fogEndDistance = 260f;

            var bridgeMat = Mat("Bridge_Wood", new Color(0.45f, 0.32f, 0.2f));
            var blueStone = Mat("Platform_Blue", new Color(0.42f, 0.47f, 0.6f));
            var redStone = Mat("Platform_Red", new Color(0.6f, 0.43f, 0.42f));
            var abyssMat = Mat("Abyss", new Color(0.1f, 0.13f, 0.2f));
            var pillarMat = Mat("Pillar_Stone", new Color(0.35f, 0.35f, 0.38f));
            var ropeMat = Mat("Rope", new Color(0.6f, 0.5f, 0.32f));

            var world = new GameObject("World").transform;

            // 외나무다리: 길이 120m, 폭 4m, 윗면 y = 0
            Box("Bridge", world, new Vector3(0f, -0.25f, 0f), new Vector3(120f, 0.5f, 4f), bridgeMat, true);
            // 다리 판자 줄무늬 (보기용)
            for (int i = 0; i < 40; i++)
                Box("Plank", world, new Vector3(-58.5f + i * 3f, 0.005f, 0f), new Vector3(0.12f, 0.02f, 4f), pillarMat, false);
            // 난간 밧줄 (충돌 없음 — 떨어질 수 있다)
            Box("RopeL", world, new Vector3(0f, 0.9f, 2.05f), new Vector3(120f, 0.05f, 0.05f), ropeMat, false);
            Box("RopeR", world, new Vector3(0f, 0.9f, -2.05f), new Vector3(120f, 0.05f, 0.05f), ropeMat, false);
            for (int i = 0; i <= 12; i++)
            {
                float x = -60f + i * 10f;
                Box("Post", world, new Vector3(x, 0.45f, 2.05f), new Vector3(0.12f, 0.9f, 0.12f), ropeMat, false);
                Box("Post", world, new Vector3(x, 0.45f, -2.05f), new Vector3(0.12f, 0.9f, 0.12f), ropeMat, false);
            }
            // 다리 기둥
            for (int i = 1; i < 6; i++)
                Box("Pillar", world, new Vector3(-60f + i * 20f, -16f, 0f), new Vector3(2.5f, 31f, 2.5f), pillarMat, false);

            // 양쪽 진영 (스폰 지역)
            Box("Spawn_Blue", world, new Vector3(-66f, -0.5f, 0f), new Vector3(12f, 1f, 12f), blueStone, true);
            Box("Spawn_Red", world, new Vector3(66f, -0.5f, 0f), new Vector3(12f, 1f, 12f), redStone, true);
            Box("Wall_Blue", world, new Vector3(-72.5f, 2f, 0f), new Vector3(1f, 5f, 12f), blueStone, true);
            Box("Wall_Red", world, new Vector3(72.5f, 2f, 0f), new Vector3(1f, 5f, 12f), redStone, true);
            Box("Base_Blue", world, new Vector3(-66f, -16f, 0f), new Vector3(10f, 30f, 10f), pillarMat, false);
            Box("Base_Red", world, new Vector3(66f, -16f, 0f), new Vector3(10f, 30f, 10f), pillarMat, false);

            // 낭떠러지 아래
            Box("Abyss", world, new Vector3(0f, -32f, 0f), new Vector3(600f, 1f, 300f), abyssMat, false);

            // 매치
            var match = new GameObject("Match").AddComponent<MatchController>();
            match.BlueMat = Mat("Team_Blue", new Color(0.25f, 0.45f, 0.95f));
            match.RedMat = Mat("Team_Red", new Color(0.9f, 0.28f, 0.24f));
            match.HatMat = Mat("Wizard_Hat", new Color(0.45f, 0.2f, 0.75f));
            match.KnifeMat = Mat("Knife_Steel", new Color(0.8f, 0.82f, 0.86f), 0.85f);
            match.MonsterBlueMat = Mat("Monster_Blue", new Color(0.35f, 0.6f, 1f), 0.2f, true);
            match.MonsterRedMat = Mat("Monster_Red", new Color(1f, 0.45f, 0.3f), 0.2f, true);

            EditorSceneManager.SaveScene(scene, ScenePath);
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log($"[KnifeWar] 게임 씬 생성 완료: {ScenePath} — Play를 누르면 6:6 (나 + 봇 11) 시작");
        }

        static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Material mat, bool collider)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go;
        }

        static Material Mat(string name, Color color, float smoothness = 0.15f, bool instancing = false)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.color = color;
            mat.SetFloat("_Glossiness", smoothness);
            mat.enableInstancing = instancing || mat.enableInstancing;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }
    }
}
