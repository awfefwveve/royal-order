using KnifeWar.Core;
using UnityEngine;

namespace KnifeWar.Game
{
    /// <summary>
    /// 카드 한 장 그리기 (IMGUI). 경기 HUD의 손패와 로비 덱 편집이 같이 쓴다.
    /// 제목·그림·효과·마나 비용을 한 장에 다 보여준다. 그림은 진짜 일러스트가 들어오기 전까지 코드로 그린 임시 아이콘.
    /// </summary>
    public static class CardUi
    {
        public const float W = 150f, H = 212f;   // 기본 카드 크기 (비율 유지해서 줄여 그릴 수 있음)

        static readonly Texture2D[] _art = new Texture2D[CardDefs.Count];
        static Texture2D _white;
        static GUIStyle _title, _desc, _cost, _key, _kind;

        public static Color KindColor(CardKind k)
        {
            switch (k)
            {
                case CardKind.Summon: return new Color(0.22f, 0.32f, 0.5f);
                case CardKind.Field: return new Color(0.55f, 0.2f, 0.12f);
                case CardKind.Building: return new Color(0.36f, 0.3f, 0.2f);
                default: return new Color(0.45f, 0.38f, 0.12f);
            }
        }

        public static string KindName(CardKind k)
        {
            switch (k)
            {
                case CardKind.Summon: return L.T("card_kind_summon");
                case CardKind.Field: return L.T("card_kind_field");
                case CardKind.Building: return L.T("card_kind_building");
                default: return L.T("card_kind_buff");
            }
        }

        static bool Exists(int id) => CardDefs.IsValidId(id) || id == CardDefs.ResurrectId;
        public static string Name(int id) => Exists(id) ? L.T("card_" + CardDefs.Get(id).Key + "_name") : "";
        public static string Desc(int id) => Exists(id) ? L.T("card_" + CardDefs.Get(id).Key + "_desc") : "";

        static void EnsureStyles()
        {
            if (_title != null) return;
            _white = Texture2D.whiteTexture;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            _desc = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.UpperLeft, wordWrap = true };
            _cost = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _key = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _kind = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleRight };
            _title.normal.textColor = _desc.normal.textColor = _cost.normal.textColor = _key.normal.textColor = Color.white;
            _kind.normal.textColor = new Color(1, 1, 1, 0.7f);
        }

        /// <summary>
        /// 카드를 그린다. 누르면 true (버튼처럼). affordable=false 면 어둡게.
        /// hotkey: 왼쪽 위 작은 키 표시 ("1"~"4"), 없으면 null.
        /// </summary>
        public static bool Draw(Rect r, int id, bool affordable = true, string hotkey = null, bool selected = false, float alpha = 1f)
        {
            EnsureStyles();
            if (!Exists(id))
            {
                Fill(r, new Color(0, 0, 0, 0.35f * alpha));
                return false;
            }
            ref readonly var c = ref CardDefs.Get(id);
            float k = r.width / W;   // 크기 배율
            float dim = affordable ? 1f : 0.45f;
            var old = GUI.color;

            // 테두리·배경
            Fill(r, new Color(0.92f, 0.85f, 0.6f, (selected ? 1f : 0.85f) * alpha));
            var inner = new Rect(r.x + 3 * k, r.y + 3 * k, r.width - 6 * k, r.height - 6 * k);
            var bg = id == CardDefs.ResurrectId ? new Color(0.5f, 0.42f, 0.1f) : KindColor(c.Kind);
            Fill(inner, new Color(bg.r * dim, bg.g * dim, bg.b * dim, alpha));

            // 제목
            _title.fontSize = Mathf.Max(9, Mathf.RoundToInt(15 * k));
            GUI.color = new Color(dim, dim, dim, alpha);
            GUI.Label(new Rect(inner.x + 20 * k, inner.y + 2 * k, inner.width - 40 * k, 26 * k), Name(id), _title);

            // 그림
            var artRect = new Rect(inner.x + 10 * k, inner.y + 30 * k, inner.width - 20 * k, 84 * k);
            GUI.DrawTexture(artRect, Art(id), ScaleMode.StretchToFill);

            // 효과
            _desc.fontSize = Mathf.Max(7, Mathf.RoundToInt(11 * k));
            GUI.Label(new Rect(inner.x + 8 * k, inner.y + 118 * k, inner.width - 16 * k, inner.height - 124 * k), Desc(id), _desc);
            _kind.fontSize = Mathf.Max(7, Mathf.RoundToInt(10 * k));
            GUI.Label(new Rect(inner.x, inner.yMax - 16 * k, inner.width - 6 * k, 14 * k), KindName(c.Kind), _kind);
            GUI.color = old;

            // 마나 비용 (오른쪽 위 동그라미)
            float cr = 24 * k;
            var costRect = new Rect(r.xMax - cr - 4 * k, r.y + 4 * k, cr, cr);
            Fill(costRect, new Color(0.25f, 0.45f, 1f, alpha));
            _cost.fontSize = Mathf.Max(8, Mathf.RoundToInt(15 * k));
            GUI.Label(costRect, c.Cost.ToString(), _cost);

            // 단축키
            if (!string.IsNullOrEmpty(hotkey))
            {
                var keyRect = new Rect(r.x + 4 * k, r.y + 4 * k, 22 * k, 22 * k);
                Fill(keyRect, new Color(0, 0, 0, 0.6f * alpha));
                _key.fontSize = Mathf.Max(8, Mathf.RoundToInt(13 * k));
                GUI.Label(keyRect, hotkey, _key);
            }

            // 클릭
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        // ---- 임시 그림 (코드로 그린 아이콘) ----

        static Texture2D _resArt;

        public static Texture2D Art(int id)
        {
            if (id == CardDefs.ResurrectId) { if (_resArt == null) _resArt = Paint(CardDefs.Resurrect); return _resArt; }
            if (!CardDefs.IsValidId(id)) return Texture2D.blackTexture;
            if (_art[id] != null) return _art[id];
            _art[id] = Paint(CardDefs.Get(id));
            return _art[id];
        }

        static Texture2D Paint(in CardDef c)
        {
            int id = c.Id;
            const int w = 96, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = "CardArt_" + id, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            Color sky = c.Kind == CardKind.Field ? new Color(0.3f, 0.08f, 0.05f) : new Color(0.12f, 0.16f, 0.24f);
            Color ground = c.Kind == CardKind.Field ? new Color(0.2f, 0.05f, 0.03f) : new Color(0.2f, 0.26f, 0.18f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = y < 14 ? ground : Color.Lerp(sky, sky * 1.6f, (y - 14f) / (h - 14f));

            void Disc(float cx, float cy, float rad, Color col)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float dx = x - cx, dy = y - cy;
                        if (dx * dx + dy * dy <= rad * rad) px[y * w + x] = col;
                    }
            }
            void Box(int x0, int y0, int x1, int y1, Color col)
            {
                for (int y = Mathf.Max(0, y0); y <= Mathf.Min(h - 1, y1); y++)
                    for (int x = Mathf.Max(0, x0); x <= Mathf.Min(w - 1, x1); x++) px[y * w + x] = col;
            }
            void Tri(float ax, float ay, float bx, float by, float cx, float cy, Color col)
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
                        float d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
                        float d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
                        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
                        if (!(neg && pos)) px[y * w + x] = col;
                    }
            }
            void Figure(float cx, float footY, float height, Color body, Color head)
            {
                float hr = height * 0.14f;
                Box((int)(cx - height * 0.12f), (int)footY, (int)(cx + height * 0.12f), (int)(footY + height * 0.68f), body);
                Disc(cx, footY + height * 0.82f, hr, head);
            }

            var steel = new Color(0.75f, 0.78f, 0.82f);
            var skin = new Color(0.85f, 0.68f, 0.52f);
            var dark = new Color(0.18f, 0.16f, 0.2f);
            var green = new Color(0.45f, 0.6f, 0.3f);
            switch (c.Key)
            {
                case "footman":
                    Figure(48, 12, 40, steel, skin);
                    Box(60, 10, 62, 58, new Color(0.5f, 0.35f, 0.2f)); Tri(57, 58, 65, 58, 61, 63, steel);
                    break;
                case "shield":
                    Figure(44, 12, 42, steel, skin);
                    Disc(60, 32, 14, new Color(0.6f, 0.45f, 0.2f)); Disc(60, 32, 9, new Color(0.3f, 0.4f, 0.7f));
                    break;
                case "archer":
                    Figure(40, 12, 38, new Color(0.35f, 0.5f, 0.3f), skin);
                    for (int t = 0; t < 40; t++) { float a = Mathf.Lerp(-1.1f, 1.1f, t / 39f); Disc(56 + Mathf.Cos(a) * 16, 32 + Mathf.Sin(a) * 18, 1.4f, new Color(0.5f, 0.35f, 0.2f)); }
                    Box(56, 31, 74, 33, steel);
                    break;
                case "ogre":
                    Figure(48, 10, 56, green, green * 1.2f);
                    Box(26, 20, 32, 46, green); Box(64, 20, 70, 46, green);
                    break;
                case "twinogre":
                    Box(36, 10, 60, 46, green * 0.9f);
                    Disc(41, 52, 7, green * 1.2f); Disc(55, 52, 7, green * 1.2f);
                    Box(24, 18, 32, 48, green * 0.9f); Box(64, 18, 72, 48, green * 0.9f);
                    break;
                case "deathknight":
                    Figure(44, 10, 50, dark, new Color(0.3f, 0.28f, 0.35f));
                    Box(58, 12, 61, 56, new Color(0.6f, 0.2f, 0.8f)); Disc(48, 51, 5, new Color(0.7f, 0.2f, 0.9f));
                    break;
                case "volcano":
                    Tri(10, 14, 86, 14, 48, 58, new Color(0.25f, 0.12f, 0.1f));
                    Tri(38, 46, 58, 46, 48, 58, new Color(1f, 0.5f, 0.1f));
                    Disc(48, 60, 6, new Color(1f, 0.75f, 0.2f));
                    Box(44, 14, 52, 46, new Color(1f, 0.35f, 0.05f));
                    break;
                case "barracks":
                    Box(24, 12, 72, 40, new Color(0.55f, 0.42f, 0.3f));
                    Tri(18, 40, 78, 40, 48, 60, new Color(0.5f, 0.2f, 0.15f));
                    Box(44, 12, 52, 28, new Color(0.25f, 0.18f, 0.12f));
                    break;
                case "manawell":
                    Box(30, 12, 66, 30, new Color(0.5f, 0.5f, 0.55f));
                    Disc(48, 32, 16, new Color(0.3f, 0.5f, 1f)); Disc(48, 32, 10, new Color(0.6f, 0.8f, 1f));
                    Box(28, 40, 68, 44, new Color(0.45f, 0.3f, 0.2f)); Box(30, 30, 34, 44, new Color(0.45f, 0.3f, 0.2f)); Box(62, 30, 66, 44, new Color(0.45f, 0.3f, 0.2f));
                    break;
                case "resurrect":   // 금빛 기둥 속에서 일어나는 기사
                    Box(40, 14, 56, 62, new Color(0.95f, 0.8f, 0.35f, 1f));
                    Box(44, 14, 52, 62, new Color(1f, 0.95f, 0.7f));
                    Figure(48, 14, 40, steel, skin);
                    for (int t = 0; t < 12; t++) Disc(30 + t * 3.2f, 20 + (t % 3) * 14, 1.5f, new Color(1f, 0.9f, 0.5f));
                    break;
                default:   // warhorn
                    Tri(20, 20, 76, 44, 76, 14, new Color(0.85f, 0.65f, 0.25f));
                    Disc(76, 29, 10, new Color(0.95f, 0.8f, 0.35f));
                    Disc(22, 20, 4, new Color(0.6f, 0.45f, 0.2f));
                    break;
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
