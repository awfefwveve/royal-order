using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 외형 교체 설정 ("껍데기"). 메뉴 KnifeWar/외형/스킨 설정 파일 만들기 → 생긴 파일에 모델·소리를 끌어다 놓으면 끝.
    /// 파일 위치: Assets/KnifeWar/Resources/KnifeWar/SkinSet.asset (코드가 자동으로 찾는다, 씬 연결 불필요)
    ///
    /// 비어 있는 칸은 지금의 기본 모양(캡슐·합성음)을 그대로 쓴다. 하나씩 바꿔도 된다.
    /// 판정(히트박스)은 외형과 완전히 분리돼 있다 — 모델을 바꿔도 맞는 범위·데미지는 그대로.
    /// 데디 서버는 외형을 아예 불러오지 않는다 (서버 비용·메모리 영향 없음).
    /// </summary>
    [CreateAssetMenu(menuName = "KnifeWar/Skin Set", fileName = "SkinSet")]
    public sealed class SkinSet : ScriptableObject
    {
        [Header("캐릭터 (발이 원점, 앞이 +Z인 프리팹. Animator 있으면 아래 파라미터를 자동으로 움직임)")]
        [Tooltip("파랑 팀 기사")] public GameObject Team0Knight;
        [Tooltip("파랑 팀 왕 (비우면 기사 모델 + 모자)")] public GameObject Team0Wizard;
        [Tooltip("빨강 팀 기사")] public GameObject Team1Knight;
        [Tooltip("빨강 팀 왕 (비우면 기사 모델 + 모자)")] public GameObject Team1Wizard;
        [Tooltip("모델 키를 판정 키(1.8m)에 자동으로 맞춤 — 보이는 몸과 맞는 범위가 어긋나지 않게")]
        public bool FitModelHeight = true;

        [Header("Animator 파라미터 이름 (모델의 Animator에 있는 것만 사용, 없으면 무시)")]
        public string SpeedParam = "Speed";        // float, 수평 이동 속도 m/s
        public string CrouchParam = "Crouch";      // float 0~1
        public string GroundedParam = "Grounded";  // bool
        public string DeadParam = "Dead";          // bool
        public string SlashTrigger = "Slash";      // trigger
        public string StabTrigger = "Stab";        // trigger
        public string SummonTrigger = "Summon";    // trigger

        [Header("칼")]
        [Tooltip("1인칭 화면에 보이는 칼 (눈 기준 위치에 붙음). 비우면 기본 막대")]
        public GameObject KnifeView;
        [Tooltip("3인칭 모델 손에 들리는 칼 (휴머노이드 모델이면 오른손 뼈에 붙음)")]
        public GameObject KnifeInHand;

        [Header("몬스터 (GPU 인스턴싱으로 수백 마리를 그림 — 뼈 애니메이션 없는 메시만, 발이 원점)")]
        [Tooltip("0 졸병, 1 방패병, 2 궁수, 3 오우거, 4 트윈헤드 오우거, 5 데스나이트, 6 화염 골렘")] public Mesh[] MonsterMesh = new Mesh[7];
        [Tooltip("종류별 크기 배율")] public float[] MonsterScale = { 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        public Material MonsterTeam0;
        public Material MonsterTeam1;
        [Tooltip("종류별 텍스처 재질 (아트 생성 결과). 있으면 팀 재질 대신 이걸 쓰고 팀 색은 살짝 물들임")] public Material[] MonsterMaterial = new Material[7];

        [Header("건물 (0 병영, 1 마나 우물 — 발이 원점인 프리팹. 비우면 기본 상자)")]
        public GameObject[] BuildingPrefab = new GameObject[2];

        [Header("맵 (꾸미기만. 실제 발판·벽 충돌은 그대로 두어 판정이 바뀌지 않음)")]
        public GameObject Environment;
        [Tooltip("기본 회색 다리·기둥 모양을 숨김 (충돌은 유지)")]
        public bool HideDefaultWorld;

        [Header("화면 글꼴 (한·중·일 글자가 다 들어간 글꼴 권장, 예: Noto Sans CJK)")]
        public Font UiFont;

        [Header("소리 (비우면 기본 합성음)")]
        public AudioClip Swing;
        public AudioClip Stab;
        public AudioClip HitBody;
        public AudioClip HitHead;
        public AudioClip Kill;
        public AudioClip Hurt;
        public AudioClip Death;
        public AudioClip Summon;
        public AudioClip MonsterHit;
        public AudioClip SelectBeep;
        public AudioClip RoundStart;
        public AudioClip Win;
        public AudioClip Lose;
        public AudioClip Music;

        static SkinSet _cached;
        static bool _loaded;

        /// <summary>설정 파일 (없으면 null = 전부 기본 모양).</summary>
        public static SkinSet Current
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _cached = Resources.Load<SkinSet>("KnifeWar/SkinSet");
                }
                return _cached;
            }
        }

        /// <summary>OnGUI 맨 앞에서 부른다: 설정한 글꼴을 화면 전체에 적용.</summary>
        public static void ApplyFont()
        {
            var k = Current;
            if (k != null && k.UiFont != null && GUI.skin.font != k.UiFont) GUI.skin.font = k.UiFont;
        }

        public GameObject CharacterPrefab(byte team, PlayerClass cls)
        {
            if (cls == PlayerClass.King)
            {
                var w = team == 0 ? Team0Wizard : Team1Wizard;
                if (w != null) return w;
            }
            return team == 0 ? Team0Knight : Team1Knight;
        }

        public bool HasWizardModel(byte team) => (team == 0 ? Team0Wizard : Team1Wizard) != null;
    }
}
