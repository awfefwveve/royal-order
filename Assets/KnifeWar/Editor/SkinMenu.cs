using System.IO;
using KnifeWar.Game;
using UnityEditor;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>외형 교체 ("껍데기 씌우기") 메뉴.</summary>
    public static class SkinMenu
    {
        const string Dir = "Assets/KnifeWar/Resources/KnifeWar";
        const string AssetPath = Dir + "/SkinSet.asset";

        [MenuItem("KnifeWar/외형/스킨 설정 파일 열기 (없으면 만들기)", priority = 10)]
        public static void OpenOrCreate()
        {
            var skin = AssetDatabase.LoadAssetAtPath<SkinSet>(AssetPath);
            if (skin == null)
            {
                Directory.CreateDirectory(Dir);
                skin = ScriptableObject.CreateInstance<SkinSet>();
                AssetDatabase.CreateAsset(skin, AssetPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[KnifeWar] 스킨 설정 파일을 만들었어요: {AssetPath} — 인스펙터에서 모델·소리를 끌어다 놓으세요.");
            }
            Selection.activeObject = skin;
            EditorGUIUtility.PingObject(skin);
        }

        [MenuItem("KnifeWar/외형/스킨 점검", priority = 11)]
        public static void Check()
        {
            var skin = AssetDatabase.LoadAssetAtPath<SkinSet>(AssetPath);
            if (skin == null) { Debug.Log("[KnifeWar] 스킨 설정 파일이 없어요 → 전부 기본 모양으로 실행됩니다."); return; }

            int warnings = 0;
            void Warn(string s) { warnings++; Debug.LogWarning("[KnifeWar] 스킨: " + s); }

            foreach (var (name, prefab) in new[] { ("파랑 기사", skin.Team0Knight), ("파랑 왕", skin.Team0Wizard), ("빨강 기사", skin.Team1Knight), ("빨강 왕", skin.Team1Wizard) })
            {
                if (prefab == null) continue;
                var anim = prefab.GetComponentInChildren<Animator>();
                if (anim == null) { Warn($"{name}: Animator가 없어요 (움직임 없이 서 있는 모양으로 보입니다)"); continue; }
                if (anim.runtimeAnimatorController == null) Warn($"{name}: Animator Controller가 비어 있어요");
                if (skin.KnifeInHand != null && !anim.isHuman) Warn($"{name}: 휴머노이드가 아니라 손에 칼을 못 붙여요 (Rig → Humanoid로 바꾸세요)");
                if (prefab.GetComponentsInChildren<Collider>(true).Length > 0) Debug.Log($"[KnifeWar] 스킨: {name}의 충돌체는 실행 중 자동으로 제거됩니다 (판정은 히트박스만 사용).");
            }
            if (skin.Team0Knight == null && skin.Team1Knight != null || skin.Team0Knight != null && skin.Team1Knight == null)
                Warn("한 팀만 모델이 있어요 — 다른 팀은 캡슐로 보입니다");

            for (int t = 0; t < 3 && skin.MonsterMesh != null && t < skin.MonsterMesh.Length; t++)
                if (skin.MonsterMesh[t] != null && skin.MonsterMesh[t].vertexCount > 3000)
                    Warn($"몬스터 {t}번 메시 정점 {skin.MonsterMesh[t].vertexCount}개 — 수백 마리를 그리므로 3000개 이하 권장");
            foreach (var (name, mat) in new[] { ("파랑 몬스터 재질", skin.MonsterTeam0), ("빨강 몬스터 재질", skin.MonsterTeam1) })
                if (mat != null && !mat.enableInstancing) Warn($"{name}: Enable GPU Instancing을 켜 주세요 (안 켜면 실행 중 사본을 만들어 켭니다)");

            if (skin.UiFont == null) Debug.Log("[KnifeWar] 스킨: 화면 글꼴이 비어 있어요 — 기본 글꼴 사용 (한·중·일 글자가 다 나오는지 확인 필요)");
            Debug.Log(warnings == 0 ? "[KnifeWar] 스킨 점검 완료: 문제 없음" : $"[KnifeWar] 스킨 점검 완료: 경고 {warnings}개");
        }
    }
}
