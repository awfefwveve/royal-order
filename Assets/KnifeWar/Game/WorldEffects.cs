using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 클라이언트 전용 맵 연출.
    /// - 볼케이노: 어느 팀이든 켜져 있으면 하늘·안개·빛이 화산 지형처럼 붉게 바뀌고, 낭떠러지 아래가 용암색이 된다 (서서히).
    /// - 건물: 팀 건물 개수(병영·마나 우물)만큼 성 1층 홀 양옆에 임시 모형을 세운다.
    /// 상태는 WizardRules(미러)에서 읽기만 한다.
    /// </summary>
    public sealed class WorldEffects : MonoBehaviour
    {
        MatchController _m;
        Light _sun;
        Material _abyss;
        float _volcano;   // 0 = 평소, 1 = 화산

        Color _fog0, _sky0, _eq0, _ground0, _sun0, _abyss0;
        static readonly Color FogV = new Color(0.45f, 0.14f, 0.06f);
        static readonly Color SkyV = new Color(0.5f, 0.18f, 0.1f);
        static readonly Color EqV = new Color(0.4f, 0.16f, 0.1f);
        static readonly Color GroundV = new Color(0.35f, 0.08f, 0.02f);
        static readonly Color SunV = new Color(1f, 0.55f, 0.3f);
        static readonly Color AbyssV = new Color(1f, 0.35f, 0.05f);

        readonly GameObject[][] _buildings = { new GameObject[8], new GameObject[8] };   // [team][index] (병영 0~3, 우물 4~5)
        Material _wood, _stone, _water;

        public void Init(MatchController m)
        {
            _m = m;
            var sunGo = GameObject.Find("Sun");
            _sun = sunGo != null ? sunGo.GetComponent<Light>() : null;
            var abyssGo = GameObject.Find("Abyss");
            var ar = abyssGo != null ? abyssGo.GetComponent<Renderer>() : null;
            if (ar != null) { _abyss = ar.material; _abyss0 = _abyss.color; }   // 인스턴스 (원본 에셋은 안 건드림)
            _fog0 = RenderSettings.fogColor;
            _sky0 = RenderSettings.ambientSkyColor;
            _eq0 = RenderSettings.ambientEquatorColor;
            _ground0 = RenderSettings.ambientGroundColor;
            _sun0 = _sun != null ? _sun.color : Color.white;
            _wood = ProceduralMaterials.Leather;
            _stone = ProceduralMaterials.DarkSteel;
            _water = new Material(ProceduralMaterials.Blade) { color = new Color(0.35f, 0.55f, 1f) };
            if (_water.HasProperty("_EmissionColor")) { _water.EnableKeyword("_EMISSION"); _water.SetColor("_EmissionColor", new Color(0.2f, 0.4f, 1f)); }
        }

        void Update()
        {
            if (_m == null || _m.Wizard == null) return;
            bool on = _m.Wizard.VolcanoVisible(_m.ServerTick);
            float target = on ? 1f : 0f;
            float next = Mathf.MoveTowards(_volcano, target, Time.deltaTime * (on ? 0.5f : 0.25f));
            if (!Mathf.Approximately(next, _volcano))
            {
                _volcano = next;
                float t = Mathf.SmoothStep(0f, 1f, _volcano);
                RenderSettings.fogColor = Color.Lerp(_fog0, FogV, t);
                RenderSettings.ambientSkyColor = Color.Lerp(_sky0, SkyV, t);
                RenderSettings.ambientEquatorColor = Color.Lerp(_eq0, EqV, t);
                RenderSettings.ambientGroundColor = Color.Lerp(_ground0, GroundV, t);
                if (_sun != null) _sun.color = Color.Lerp(_sun0, SunV, t);
                if (_abyss != null)
                {
                    _abyss.color = Color.Lerp(_abyss0, AbyssV, t);
                    if (_abyss.HasProperty("_EmissionColor"))
                    {
                        if (t > 0.01f) _abyss.EnableKeyword("_EMISSION"); else _abyss.DisableKeyword("_EMISSION");
                        _abyss.SetColor("_EmissionColor", Color.Lerp(Color.black, new Color(1f, 0.3f, 0.05f), t));
                    }
                }
            }

            for (int team = 0; team < 2; team++)
            {
                var b = _m.Wizard.Buildings[team];
                SyncBuildings(team, BuildingType.Barracks, b.Count(BuildingType.Barracks), 0, 3);
                SyncBuildings(team, BuildingType.ManaWell, b.Count(BuildingType.ManaWell), 4, 2);
            }
        }

        void SyncBuildings(int team, int type, int count, int first, int max)
        {
            var arr = _buildings[team];
            for (int i = 0; i < max; i++)
            {
                bool want = i < count;
                var go = arr[first + i];
                if (want && go == null) arr[first + i] = Build(team, type, i);
                else if (!want && go != null) { Destroy(go); arr[first + i] = null; }
            }
        }

        /// <summary>성 1층 홀 양옆 띠 (다리 폭 밖 z ±7.4, 경사로 전 x 60.9~63.9)에 병영(+z)/우물(-z) 모형.</summary>
        GameObject Build(int team, int type, int index)
        {
            float side = team == 0 ? -1f : 1f;
            float z = (type == BuildingType.Barracks ? 1f : -1f) * (MapLayout.CastleHalfWidth - 1.6f);
            float x = side * (type == BuildingType.Barracks ? MapLayout.GateX + 0.9f + index * 1.5f : MapLayout.GateX + 1.2f + index * 1.9f);
            var root = new GameObject(type == BuildingType.Barracks ? "Barracks" : "ManaWell");
            root.transform.position = new Vector3(x, 0f, z);
            var skin = SkinSet.Current;
            var prefab = skin != null && skin.BuildingPrefab != null && type < skin.BuildingPrefab.Length ? skin.BuildingPrefab[type] : null;
            if (prefab != null)
            {
                var inst = Instantiate(prefab, root.transform);
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.Euler(0f, side > 0 ? 180f : 0f, 0f);   // 문이 다리 쪽을 보게
                inst.transform.localScale = Vector3.one * 0.55f;   // 홀 띠가 좁아서 작게
            }
            else if (type == BuildingType.Barracks)
            {
                ProceduralParts.Box("Walls", root.transform, new Vector3(0f, 0.55f, 0f), new Vector3(1.3f, 1.1f, 1.3f), _wood);
                var roof = ProceduralParts.Box("Roof", root.transform, new Vector3(0f, 1.3f, 0f), new Vector3(1.4f, 0.4f, 1.4f), ProceduralMaterials.Cloth(team));
                roof.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
                ProceduralParts.Box("Flag", root.transform, new Vector3(0f, 2.1f, 0f), new Vector3(0.05f, 1.2f, 0.05f), _stone);
                ProceduralParts.Box("Banner", root.transform, new Vector3(0.22f, 2.45f, 0f), new Vector3(0.45f, 0.28f, 0.02f), ProceduralMaterials.Cloth(team));
            }
            else
            {
                ProceduralParts.Prim(PrimitiveType.Cylinder, "Rim", root.transform, new Vector3(0f, 0.35f, 0f), new Vector3(1.2f, 0.35f, 1.2f), Quaternion.identity, _stone);
                ProceduralParts.Prim(PrimitiveType.Cylinder, "Water", root.transform, new Vector3(0f, 0.6f, 0f), new Vector3(0.95f, 0.05f, 0.95f), Quaternion.identity, _water);
                ProceduralParts.Box("PostA", root.transform, new Vector3(-0.6f, 1.2f, 0f), new Vector3(0.1f, 1.4f, 0.1f), _wood);
                ProceduralParts.Box("PostB", root.transform, new Vector3(0.6f, 1.2f, 0f), new Vector3(0.1f, 1.4f, 0.1f), _wood);
                ProceduralParts.Box("Beam", root.transform, new Vector3(0f, 1.95f, 0f), new Vector3(1.5f, 0.12f, 0.3f), _wood);
            }
            return root;
        }

        void OnDestroy()
        {
            RenderSettings.fogColor = _fog0;
            RenderSettings.ambientSkyColor = _sky0;
            RenderSettings.ambientEquatorColor = _eq0;
            RenderSettings.ambientGroundColor = _ground0;
            if (_sun != null) _sun.color = _sun0;
        }
    }
}
