using UnityEngine;

namespace KnifeWar.Net
{
    /// <summary>
    /// 시작 화면용 그리기 도구 (IMGUI). 이미지·글자·버튼·막대를 한 가지 모양으로 그린다.
    /// 이미지는 Resources/KnifeWar/UI 에서 읽는다 (keyart, planet, ring, wordmark, title).
    /// 진짜 UI 아트가 들어오면 이 파일의 색·모양만 바꾸면 화면 전체가 같이 바뀐다.
    /// </summary>
    internal static class FE
    {
        public static readonly Color Gold = new Color(0.95f, 0.84f, 0.56f);
        public static readonly Color GoldDeep = new Color(0.85f, 0.70f, 0.37f);
        public static readonly Color Red = new Color(0.93f, 0.33f, 0.33f);
        public static readonly Color Blue = new Color(0.36f, 0.62f, 1f);
        public static readonly Color Ink = new Color(0.04f, 0.045f, 0.06f);
        public static readonly Color Panel = new Color(0.07f, 0.08f, 0.11f, 0.92f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Sub = new Color(1f, 1f, 1f, 0.62f);

        public const float KeyArtAspect = 1672f / 941f;
        public const float TitleAspect = 4f, WordmarkAspect = 4f;

        static Texture2D _keyart, _planet, _ring, _wordmark, _title, _vgrad, _hgrad;
        public static Texture2D KeyArt => _keyart ? _keyart : (_keyart = Resources.Load<Texture2D>("KnifeWar/UI/keyart"));
        public static Texture2D Planet => _planet ? _planet : (_planet = Resources.Load<Texture2D>("KnifeWar/UI/planet"));
        public static Texture2D Ring => _ring ? _ring : (_ring = Resources.Load<Texture2D>("KnifeWar/UI/ring"));
        public static Texture2D Wordmark => _wordmark ? _wordmark : (_wordmark = Resources.Load<Texture2D>("KnifeWar/UI/wordmark"));
        public static Texture2D Title => _title ? _title : (_title = Resources.Load<Texture2D>("KnifeWar/UI/title"));

        /// <summary>위 → 아래로 투명 → 불투명.</summary>
        static Texture2D VGrad => _vgrad ? _vgrad : (_vgrad = Gradient(true));
        /// <summary>왼쪽 → 오른쪽으로 불투명 → 투명.</summary>
        static Texture2D HGrad => _hgrad ? _hgrad : (_hgrad = Gradient(false));

        static Texture2D Gradient(bool vertical)
        {
            var t = new Texture2D(vertical ? 1 : 64, vertical ? 64 : 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int i = 0; i < 64; i++)
            {
                float a = i / 63f;
                a = vertical ? a : 1f - a;
                a = a * a * (3f - 2f * a);
                if (vertical) t.SetPixel(0, 63 - i, new Color(1, 1, 1, a)); else t.SetPixel(i, 0, new Color(1, 1, 1, a));
            }
            t.Apply();
            return t;
        }

        static GUIStyle _text;
        public static float Now => Time.unscaledTime;

        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        public static void Border(Rect r, Color c, float t = 1f)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c);
            Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c);
            Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        public static void Image(Rect r, Texture tex, float alpha = 1f)
        {
            if (tex == null) return;
            var old = GUI.color;
            GUI.color = new Color(1, 1, 1, alpha);
            GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>그림을 화면에 꽉 차게 (비율 유지, 남는 부분 자름). zoom 으로 천천히 다가가는 연출.</summary>
        public static void Cover(Rect r, Texture tex, float aspect, float alpha = 1f, float zoom = 1f, Color? tint = null)
        {
            if (tex == null) { Fill(r, Ink); return; }
            float w = r.width, h = r.width / aspect;
            if (h < r.height) { h = r.height; w = h * aspect; }
            w *= zoom; h *= zoom;
            var dst = new Rect(r.x + (r.width - w) * 0.5f, r.y + (r.height - h) * 0.5f, w, h);
            GUI.BeginGroup(r);
            var old = GUI.color;
            var c = tint ?? Color.white;
            GUI.color = new Color(c.r, c.g, c.b, alpha);
            GUI.DrawTexture(new Rect(dst.x - r.x, dst.y - r.y, dst.width, dst.height), tex, ScaleMode.StretchToFill, false);
            GUI.color = old;
            GUI.EndGroup();
        }

        public static void ShadeBottom(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, VGrad); GUI.color = o; }
        public static void ShadeLeft(Rect r, Color c) { var o = GUI.color; GUI.color = c; GUI.DrawTexture(r, HGrad); GUI.color = o; }

        public static void Text(Rect r, string s, int size, Color c, TextAnchor a = TextAnchor.MiddleLeft, bool bold = false, bool shadow = true)
        {
            if (_text == null) _text = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, clipping = TextClipping.Overflow };
            _text.font = GUI.skin.font;
            _text.fontSize = Mathf.Max(8, size);
            _text.alignment = a;
            _text.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            if (shadow)
            {
                _text.normal.textColor = new Color(0, 0, 0, c.a * 0.7f);
                GUI.Label(new Rect(r.x + 1, r.y + 2, r.width, r.height), s, _text);
            }
            _text.normal.textColor = c;
            GUI.Label(r, s, _text);
        }

        public static bool Hover(Rect r) => GUI.enabled && r.Contains(Event.current.mousePosition);

        public static bool Clicked(Rect r)
        {
            var e = Event.current;
            if (!GUI.enabled || e.type != EventType.MouseUp || e.button != 0 || !r.Contains(e.mousePosition)) return false;
            e.Use();
            return true;
        }

        public enum Kind { Primary, Line, Ghost, Danger }

        /// <summary>네모 버튼. Primary = 금색, Line = 테두리만, Ghost = 글자만, Danger = 빨강.</summary>
        public static bool Button(Rect r, string label, Kind kind = Kind.Line, int size = 18)
        {
            bool on = GUI.enabled, hover = Hover(r);
            float a = on ? 1f : 0.4f;
            switch (kind)
            {
                case Kind.Primary:
                    Fill(r, (hover ? Gold : GoldDeep) * new Color(1, 1, 1, a));
                    Text(r, label, size, new Color(Ink.r, Ink.g, Ink.b, a), TextAnchor.MiddleCenter, true, false);
                    break;
                case Kind.Danger:
                    Fill(r, new Color(Red.r, Red.g, Red.b, (hover ? 0.95f : 0.8f) * a));
                    Text(r, label, size, new Color(1, 1, 1, a), TextAnchor.MiddleCenter, true);
                    break;
                case Kind.Line:
                    Fill(r, new Color(1, 1, 1, (hover ? 0.12f : 0.05f) * a));
                    Border(r, hover ? new Color(Gold.r, Gold.g, Gold.b, a) : new Color(1, 1, 1, 0.3f * a));
                    Text(r, label, size, new Color(1, 1, 1, a), TextAnchor.MiddleCenter);
                    break;
                default:
                    Text(r, label, size, hover ? Gold : new Color(1, 1, 1, 0.8f * a), TextAnchor.MiddleCenter);
                    break;
            }
            return Clicked(r);
        }

        /// <summary>진행 막대. progress &lt; 0 이면 왕복하는 막대(끝을 모를 때).</summary>
        public static void Bar(Rect r, float progress, Color fill)
        {
            Fill(r, new Color(1, 1, 1, 0.12f));
            if (progress >= 0f) Fill(new Rect(r.x, r.y, r.width * Mathf.Clamp01(progress), r.height), fill);
            else
            {
                float seg = r.width * 0.25f, t = Mathf.PingPong(Now * 0.6f, 1f);
                Fill(new Rect(r.x + (r.width - seg) * t, r.y, seg, r.height), fill);
            }
        }

        public static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }

        /// <summary>회전시켜 그리기 (고리 연출).</summary>
        public static void Rotated(Rect r, Texture tex, float degrees, float alpha)
        {
            var m = GUI.matrix;
            GUIUtility.RotateAroundPivot(degrees, r.center);
            Image(r, tex, alpha);
            GUI.matrix = m;
        }
    }
}
