using System.Collections.Generic;
using System.IO;
using System.Linq;
using KnifeWar.Core;
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
            var castleMat = Mat("Castle_Stone", new Color(0.5f, 0.5f, 0.52f));
            var floorMat = Mat("Castle_Floor", new Color(0.38f, 0.36f, 0.34f));
            var tableMat = Mat("Table_Wood", new Color(0.32f, 0.2f, 0.12f));
            var cardMat = Mat("Card_Paper", new Color(0.9f, 0.85f, 0.65f));
            var carpetBlue = Mat("Carpet_Blue", new Color(0.18f, 0.25f, 0.5f));
            var carpetRed = Mat("Carpet_Red", new Color(0.5f, 0.14f, 0.16f));

            var world = new GameObject("World").transform;

            // 다리: 길이 120m, 폭 12m (MatchController.BridgeHalfWidth), 윗면 y = 0
            float bw = MatchController.BridgeHalfWidth * 2f, edge = MatchController.BridgeHalfWidth + 0.05f;
            float bl = MapLayout.BridgeHalf * 2f;
            Box("Bridge", world, new Vector3(0f, -0.25f, 0f), new Vector3(bl, 0.5f, bw), bridgeMat, true);
            // 다리 판자 줄무늬 (보기용)
            for (int i = 0; i < 40; i++)
                Box("Plank", world, new Vector3(-58.5f + i * 3f, 0.005f, 0f), new Vector3(0.12f, 0.02f, bw), pillarMat, false);
            // 난간 밧줄 (충돌 없음 — 떨어질 수 있다)
            Box("RopeL", world, new Vector3(0f, 0.9f, edge), new Vector3(bl, 0.05f, 0.05f), ropeMat, false);
            Box("RopeR", world, new Vector3(0f, 0.9f, -edge), new Vector3(bl, 0.05f, 0.05f), ropeMat, false);
            for (int i = 0; i <= 12; i++)
            {
                float x = -60f + i * 10f;
                Box("Post", world, new Vector3(x, 0.45f, edge), new Vector3(0.12f, 0.9f, 0.12f), ropeMat, false);
                Box("Post", world, new Vector3(x, 0.45f, -edge), new Vector3(0.12f, 0.9f, 0.12f), ropeMat, false);
            }
            // 다리 기둥
            for (int i = 1; i < 6; i++)
                Box("Pillar", world, new Vector3(-60f + i * 20f, -16f, 0f), new Vector3(2.5f, 31f, bw * 0.6f), pillarMat, false);

            // 양쪽 성 (2층짜리 작은 성: 1층 홀 → 경사로 → 2층 왕의 방). 치수는 MapLayout.
            BuildCastle(world, 0, castleMat, floorMat, blueStone, tableMat, cardMat, carpetBlue, pillarMat);
            BuildCastle(world, 1, castleMat, floorMat, redStone, tableMat, cardMat, carpetRed, pillarMat);

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

        /// <summary>
        /// 한 팀의 성. side = 팀0 -1(왼쪽) / 팀1 +1(오른쪽). 성문은 다리 끝(GateX), 안쪽으로 홀 → 경사로 → 2층.
        /// 2층(Floor2Y)에 왕의 테이블과 흩어진 카드. 소환수는 1층 홀(성문 안 2m)에서 나타나 성문으로 나간다.
        /// </summary>
        static void BuildCastle(Transform world, int team, Material stone, Material floor, Material teamStone, Material table, Material card, Material carpet, Material dark)
        {
            float side = team == 0 ? -1f : 1f;
            string tn = team == 0 ? "Blue" : "Red";
            var root = new GameObject("Castle_" + tn).transform;
            root.SetParent(world, false);
            float hw = MapLayout.CastleHalfWidth;                 // 폭 절반 (z)
            float gate = MapLayout.GateX, back = MapLayout.BackX + 1.5f, f2 = MapLayout.Floor2Y;
            float rampS = MapLayout.RampStartX, rampE = MapLayout.RampEndX;
            float wallT = 1f, wallH = 9f;
            float X(float ax) => side * ax;   // |x| → 월드 x

            // 1층 바닥 (성문 ~ 경사로 시작) + 그 아래 기단
            float hallLen = rampS - gate + 1f;
            Box("Floor1", root, new Vector3(X(gate + hallLen * 0.5f - 0.5f), -0.25f, 0f), new Vector3(hallLen, 0.5f, hw * 2f), floor, true);
            Box("Carpet1", root, new Vector3(X(gate + hallLen * 0.5f - 0.5f), 0.01f, 0f), new Vector3(hallLen - 0.5f, 0.02f, 3f), carpet, false);
            Box("Foundation", root, new Vector3(X((gate + back) * 0.5f), -16f, 0f), new Vector3(back - gate, 31f, hw * 2f), dark, false);

            // 경사로 (회전한 상자, 걸어 올라갈 수 있음): 1층 끝에서 2층까지
            float rampLen = rampE - rampS;
            float slope = Mathf.Atan2(f2, rampLen) * Mathf.Rad2Deg;
            float hyp = Mathf.Sqrt(rampLen * rampLen + f2 * f2);
            var ramp = Box("Ramp", root, new Vector3(X((rampS + rampE) * 0.5f), f2 * 0.5f - 0.25f, 0f), new Vector3(hyp + 0.6f, 0.5f, hw * 2f), floor, true);
            ramp.transform.rotation = Quaternion.Euler(0f, 0f, side * slope);   // 성 안쪽(|x| 큰 쪽)이 높다
            Box("RampCarpet", root, new Vector3(X((rampS + rampE) * 0.5f), f2 * 0.5f + 0.01f, 0f), new Vector3(hyp, 0.02f, 3f), carpet, false).transform.rotation = ramp.transform.rotation;
            // 경사로 아래 계단식 돌 채움 (밑이 비어 보이지 않게, 충돌 없음)
            for (int k = 1; k <= 3; k++)
            {
                float from = rampS + rampLen * k * 0.25f, top = f2 * k * 0.25f - 0.1f;
                Box("RampFill", root, new Vector3(X((from + rampE) * 0.5f), top * 0.5f - 0.25f, 0f), new Vector3(rampE - from, top + 0.5f, hw * 2f), stone, false);
            }

            // 2층 바닥 (경사로 끝 ~ 뒷벽) + 아래 공간은 돌로 채움
            float f2Len = back - rampE;
            Box("Floor2", root, new Vector3(X(rampE + f2Len * 0.5f), f2 - 0.25f, 0f), new Vector3(f2Len, 0.5f, hw * 2f), floor, true);
            Box("Floor2Base", root, new Vector3(X(rampE + f2Len * 0.5f), f2 * 0.5f - 0.5f, 0f), new Vector3(f2Len, f2 - 1f, hw * 2f), stone, false);
            Box("Carpet2", root, new Vector3(X(rampE + f2Len * 0.5f), f2 + 0.01f, 0f), new Vector3(f2Len - 0.5f, 0.02f, 3f), carpet, false);

            // 벽: 양옆(1층 홀 + 2층 모두), 뒷벽, 앞면(성문 양옆 탑)
            float len = back - gate + wallT;
            Box("WallL", root, new Vector3(X(gate + len * 0.5f - wallT * 0.5f), wallH * 0.5f, hw + wallT * 0.5f), new Vector3(len, wallH, wallT), stone, true);
            Box("WallR", root, new Vector3(X(gate + len * 0.5f - wallT * 0.5f), wallH * 0.5f, -(hw + wallT * 0.5f)), new Vector3(len, wallH, wallT), stone, true);
            Box("WallBack", root, new Vector3(X(back + wallT * 0.5f), wallH * 0.5f, 0f), new Vector3(wallT, wallH, hw * 2f + wallT * 2f), stone, true);
            // 앞면: 성문(다리 폭 12m)은 뚫려 있고 양옆은 벽, 위는 문 위 아치 벽
            float sideW = hw - MatchController.BridgeHalfWidth;
            Box("FrontL", root, new Vector3(X(gate - wallT * 0.5f), wallH * 0.5f, MatchController.BridgeHalfWidth + sideW * 0.5f), new Vector3(wallT, wallH, sideW), teamStone, true);
            Box("FrontR", root, new Vector3(X(gate - wallT * 0.5f), wallH * 0.5f, -(MatchController.BridgeHalfWidth + sideW * 0.5f)), new Vector3(wallT, wallH, sideW), teamStone, true);
            Box("GateTop", root, new Vector3(X(gate - wallT * 0.5f), 7f, 0f), new Vector3(wallT, 4f, MatchController.BridgeHalfWidth * 2f), teamStone, true);
            // 탑 4개 (모서리)
            foreach (float z in new[] { hw + 0.5f, -(hw + 0.5f) })
            {
                Box("TowerFront", root, new Vector3(X(gate - 0.5f), 5.5f, z), new Vector3(2.5f, 11f, 2.5f), teamStone, true);
                Box("TowerBack", root, new Vector3(X(back + 0.5f), 5.5f, z), new Vector3(2.5f, 11f, 2.5f), teamStone, true);
            }
            // 2층 천장(지붕)과 1층 홀 지붕은 뚫린 하늘 — 2층 뒤쪽에만 지붕
            Box("Roof2", root, new Vector3(X(rampE + f2Len * 0.5f), wallH + 0.25f, 0f), new Vector3(f2Len, 0.5f, hw * 2f + wallT * 2f), stone, false);
            // 2층 난간 (경사로 쪽 가장자리 양옆만, 가운데는 길)
            foreach (float z in new[] { hw - 1.5f, -(hw - 1.5f) })
                Box("Rail2", root, new Vector3(X(rampE), f2 + 0.5f, z), new Vector3(0.2f, 1f, 3f), dark, true);

            // 왕의 테이블 + 흩어진 카드 (2층 가운데 뒤쪽)
            float tx = MapLayout.TableX;
            Box("Table", root, new Vector3(X(tx), f2 + 0.78f, 0f), new Vector3(1.6f, 0.08f, 2.6f), table, true);
            foreach (float dz in new[] { -1.1f, 1.1f })
                foreach (float dx in new[] { -0.6f, 0.6f })
                    Box("TableLeg", root, new Vector3(X(tx + dx), f2 + 0.37f, dz), new Vector3(0.1f, 0.74f, 0.1f), table, false);
            var rnd = new System.Random(17 + team);
            for (int i = 0; i < 9; i++)
            {
                float dx = (float)(rnd.NextDouble() * 1.2 - 0.6), dz = (float)(rnd.NextDouble() * 2.0 - 1.0);
                var c = Box("Card", root, new Vector3(X(tx + dx), f2 + 0.825f + i * 0.004f, dz), new Vector3(0.18f, 0.005f, 0.26f), card, false);
                c.transform.rotation = Quaternion.Euler(0f, (float)(rnd.NextDouble() * 360.0), 0f);
            }
            Box("Deck", root, new Vector3(X(tx - 0.5f), f2 + 0.86f, -1.0f), new Vector3(0.2f, 0.08f, 0.28f), card, false);
            // 왕좌 (뒤쪽)
            Box("Throne", root, new Vector3(X(back - 0.8f), f2 + 0.5f, 0f), new Vector3(0.9f, 1f, 1.1f), table, true);
            Box("ThroneBack", root, new Vector3(X(back - 0.4f), f2 + 1.4f, 0f), new Vector3(0.15f, 1.8f, 1.1f), table, true);
            // 횃불 (벽)
            foreach (float z in new[] { hw - 0.3f, -(hw - 0.3f) })
            {
                var torch = new GameObject("Torch");
                torch.transform.SetParent(root, false);
                torch.transform.position = new Vector3(X(rampE + 1f), f2 + 2.4f, z);
                var l = torch.AddComponent<Light>();
                l.type = LightType.Point; l.range = 9f; l.intensity = 1.6f; l.color = new Color(1f, 0.75f, 0.45f);
            }
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
