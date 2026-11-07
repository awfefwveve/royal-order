using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// GLB(glTF 바이너리) → 유니티 메시·텍스처·재질 자산. 외부 패키지 없이 필요한 것만 읽는다:
    /// 정점·법선·UV·삼각형, baseColor·normal 텍스처, 노드 변환. 뼈·애니메이션은 안 읽음 (정적 메시용).
    /// 모든 프리미티브를 메시 하나로 합치고, 발이 원점(바닥 y=0, xz 중심), 앞이 +Z 가 되게 맞춘 뒤 원하는 키로 크기를 맞춘다.
    /// glTF 는 오른손 좌표계라 x 를 뒤집고 삼각형 방향을 바꾼다.
    /// </summary>
    public static class GlbImporter
    {
        public sealed class Result
        {
            public Mesh Mesh;
            public Material Material;
            public Texture2D Albedo, Normal;
            public float RawHeight;     // 크기 맞추기 전 원래 키 (m 단위로 가정)
            public string Error;
        }

        /// <summary>
        /// glbPath 를 읽어 outDir(Assets/...) 아래에 name.asset(메시), name_Albedo.png, name_Normal.png, name.mat 를 만든다.
        /// targetHeight > 0 이면 그 키(m)로 맞춤. yawDeg 는 앞 방향 보정 (y축 회전, 도).
        /// </summary>
        public static Result Import(string glbPath, string outDir, string name, float targetHeight, float yawDeg)
        {
            var r = new Result();
            try
            {
                var bytes = File.ReadAllBytes(glbPath);
                var glb = Parse(bytes);
                var verts = new List<Vector3>();
                var norms = new List<Vector3>();
                var uvs = new List<Vector2>();
                var tris = new List<int>();
                int texImage = -1, normImage = -1;

                // 씬의 노드 트리를 따라 메시 모으기 (노드 변환 적용)
                var scenes = glb.Json.Get("scenes") as List<object>;
                var nodes = glb.Json.Get("nodes") as List<object>;
                var rootIdx = new List<int>();
                if (scenes != null && scenes.Count > 0)
                {
                    int si = glb.Json.Has("scene") ? glb.Json.Int("scene") : 0;
                    var sc = (Dictionary<string, object>)scenes[si];
                    if (sc.TryGetValue("nodes", out var ns)) foreach (var n in (List<object>)ns) rootIdx.Add(Convert.ToInt32(n));
                }
                else if (nodes != null) for (int i = 0; i < nodes.Count; i++) rootIdx.Add(i);

                void Visit(int ni, Matrix4x4 parent)
                {
                    var node = new J((Dictionary<string, object>)nodes[ni]);
                    var local = NodeMatrix(node);
                    var world = parent * local;
                    if (node.Has("mesh")) AppendMesh(glb, node.Int("mesh"), world, verts, norms, uvs, tris, ref texImage, ref normImage);
                    if (node.Has("children")) foreach (var c in (List<object>)node.Raw["children"]) Visit(Convert.ToInt32(c), world);
                }
                if (nodes != null) foreach (var ri in rootIdx) Visit(ri, Matrix4x4.identity);
                if (verts.Count == 0) { r.Error = "메시 없음"; return r; }

                // 오른손 → 왼손: x 뒤집기 + 삼각형 방향 뒤집기
                for (int i = 0; i < verts.Count; i++) { var v = verts[i]; v.x = -v.x; verts[i] = v; }
                for (int i = 0; i < norms.Count; i++) { var n = norms[i]; n.x = -n.x; norms[i] = n; }
                for (int i = 0; i + 2 < tris.Count; i += 3) { (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]); }

                // 앞 방향 보정 → 발 원점 → 키 맞춤
                var rot = Quaternion.Euler(0f, yawDeg, 0f);
                var b = new Bounds(rot * verts[0], Vector3.zero);
                for (int i = 0; i < verts.Count; i++) { verts[i] = rot * verts[i]; b.Encapsulate(verts[i]); }
                for (int i = 0; i < norms.Count; i++) norms[i] = rot * norms[i];
                r.RawHeight = b.size.y;
                float k = targetHeight > 0f && b.size.y > 1e-5f ? targetHeight / b.size.y : 1f;
                var off = new Vector3(b.center.x, b.min.y, b.center.z);
                for (int i = 0; i < verts.Count; i++) verts[i] = (verts[i] - off) * k;

                var mesh = new Mesh { name = name, indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.SetVertices(verts);
                if (norms.Count == verts.Count) mesh.SetNormals(norms);
                if (uvs.Count == verts.Count) mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                if (norms.Count != verts.Count) mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                mesh.RecalculateTangents();

                Directory.CreateDirectory(outDir);
                string meshPath = $"{outDir}/{name}.asset";
                AssetDatabase.CreateAsset(mesh, meshPath);
                r.Mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);

                r.Albedo = SaveImage(glb, texImage, $"{outDir}/{name}_Albedo.png", false);
                r.Normal = SaveImage(glb, normImage, $"{outDir}/{name}_Normal.png", true);

                var mat = new Material(Shader.Find("Standard")) { name = name, enableInstancing = true };
                if (r.Albedo != null) mat.mainTexture = r.Albedo;
                if (r.Normal != null) { mat.EnableKeyword("_NORMALMAP"); mat.SetTexture("_BumpMap", r.Normal); }
                mat.SetFloat("_Glossiness", 0.35f);
                string matPath = $"{outDir}/{name}.mat";
                AssetDatabase.CreateAsset(mat, matPath);
                r.Material = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                AssetDatabase.SaveAssets();
            }
            catch (Exception e) { r.Error = e.Message; }
            return r;
        }

        /// <summary>메시+재질로 프리팹 하나 (MeshFilter/MeshRenderer, 발이 원점).</summary>
        public static GameObject SavePrefab(Result r, string outDir, string name)
        {
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = r.Mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = r.Material;
            string path = $"{outDir}/{name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            UnityEngine.Object.DestroyImmediate(go);
            return prefab;
        }

        // ---------------- glTF 읽기 ----------------

        sealed class Glb
        {
            public J Json;
            public byte[] Bin;
        }

        /// <summary>JSON 사전을 편하게 읽기 위한 얇은 껍데기.</summary>
        sealed class J
        {
            public readonly Dictionary<string, object> Raw;
            public J(Dictionary<string, object> raw) { Raw = raw; }
            public bool Has(string k) => Raw != null && Raw.ContainsKey(k);
            public object Get(string k) => Raw != null && Raw.TryGetValue(k, out var v) ? v : null;
            public int Int(string k, int def = 0) => Has(k) ? Convert.ToInt32(Raw[k]) : def;
            public float F(string k, float def = 0f) => Has(k) ? Convert.ToSingle(Raw[k]) : def;
            public J Obj(string k) => Get(k) is Dictionary<string, object> d ? new J(d) : null;
            public J At(string arr, int i) { var l = Get(arr) as List<object>; return l != null && i >= 0 && i < l.Count ? new J((Dictionary<string, object>)l[i]) : null; }
        }

        static Glb Parse(byte[] b)
        {
            if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46546C67) throw new Exception("GLB 아님");
            int pos = 12; string json = null; byte[] bin = null;
            while (pos + 8 <= b.Length)
            {
                int len = (int)BitConverter.ToUInt32(b, pos); uint type = BitConverter.ToUInt32(b, pos + 4); pos += 8;
                if (type == 0x4E4F534A) json = Encoding.UTF8.GetString(b, pos, len);
                else if (type == 0x004E4942) { bin = new byte[len]; Buffer.BlockCopy(b, pos, bin, 0, len); }
                pos += len;
            }
            if (json == null) throw new Exception("JSON 청크 없음");
            return new Glb { Json = new J((Dictionary<string, object>)MiniJson.Parse(json)), Bin = bin };
        }

        static Matrix4x4 NodeMatrix(J node)
        {
            if (node.Get("matrix") is List<object> m && m.Count == 16)
            {
                var mat = new Matrix4x4();
                for (int i = 0; i < 16; i++) mat[i % 4, i / 4] = Convert.ToSingle(m[i]);   // glTF 는 열 우선
                return mat;
            }
            var t = Vector3.zero; var r = Quaternion.identity; var s = Vector3.one;
            if (node.Get("translation") is List<object> tl) t = new Vector3(F(tl, 0), F(tl, 1), F(tl, 2));
            if (node.Get("rotation") is List<object> rl) r = new Quaternion(F(rl, 0), F(rl, 1), F(rl, 2), F(rl, 3));
            if (node.Get("scale") is List<object> sl) s = new Vector3(F(sl, 0), F(sl, 1), F(sl, 2));
            return Matrix4x4.TRS(t, r, s);
        }
        static float F(List<object> l, int i) => Convert.ToSingle(l[i]);

        static void AppendMesh(Glb g, int meshIdx, Matrix4x4 world, List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris, ref int texImage, ref int normImage)
        {
            var mesh = g.Json.At("meshes", meshIdx);
            var prims = mesh.Get("primitives") as List<object>;
            if (prims == null) return;
            var nrm = world.inverse.transpose;
            foreach (var po in prims)
            {
                var p = new J((Dictionary<string, object>)po);
                int mode = p.Int("mode", 4);
                if (mode != 4) continue;   // 삼각형만
                var attr = p.Obj("attributes");
                if (attr == null || !attr.Has("POSITION")) continue;
                int baseV = verts.Count;
                var pos = ReadVec3(g, attr.Int("POSITION"));
                var nn = attr.Has("NORMAL") ? ReadVec3(g, attr.Int("NORMAL")) : null;
                var uv = attr.Has("TEXCOORD_0") ? ReadVec2(g, attr.Int("TEXCOORD_0")) : null;
                for (int i = 0; i < pos.Length; i++)
                {
                    verts.Add(world.MultiplyPoint3x4(pos[i]));
                    norms.Add(nn != null ? nrm.MultiplyVector(nn[i]).normalized : Vector3.up);
                    uvs.Add(uv != null ? new Vector2(uv[i].x, 1f - uv[i].y) : Vector2.zero);
                }
                if (p.Has("indices"))
                {
                    var idx = ReadInts(g, p.Int("indices"));
                    for (int i = 0; i < idx.Length; i++) tris.Add(baseV + idx[i]);
                }
                else for (int i = 0; i < pos.Length; i++) tris.Add(baseV + i);

                // 첫 재질의 텍스처만 쓴다 (VARCO 결과는 보통 재질 하나)
                if (texImage < 0 && p.Has("material"))
                {
                    var m = g.Json.At("materials", p.Int("material"));
                    var pbr = m?.Obj("pbrMetallicRoughness");
                    var bct = pbr?.Obj("baseColorTexture");
                    if (bct != null) texImage = TextureImage(g, bct.Int("index"));
                    var nt = m?.Obj("normalTexture");
                    if (nt != null) normImage = TextureImage(g, nt.Int("index"));
                }
            }
        }

        static int TextureImage(Glb g, int texIdx)
        {
            var t = g.Json.At("textures", texIdx);
            return t != null && t.Has("source") ? t.Int("source") : -1;
        }

        static Texture2D SaveImage(Glb g, int imageIdx, string path, bool normal)
        {
            if (imageIdx < 0) return null;
            var img = g.Json.At("images", imageIdx);
            if (img == null || !img.Has("bufferView")) return null;
            var (data, _) = ReadBufferView(g, img.Int("bufferView"));
            string mime = img.Get("mimeType") as string ?? "image/png";
            if (mime == "image/jpeg") path = Path.ChangeExtension(path, ".jpg");
            File.WriteAllBytes(path, data);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti != null)
            {
                ti.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.maxTextureSize = 2048;
                ti.sRGBTexture = !normal;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static (byte[] data, int stride) ReadBufferView(Glb g, int bvIdx)
        {
            var bv = g.Json.At("bufferViews", bvIdx);
            int off = bv.Int("byteOffset"), len = bv.Int("byteLength"), stride = bv.Int("byteStride");
            var data = new byte[len];
            Buffer.BlockCopy(g.Bin, off, data, 0, len);
            return (data, stride);
        }

        static int CompSize(int ct) => ct == 5126 || ct == 5125 ? 4 : (ct == 5123 || ct == 5122 ? 2 : 1);
        static int CompCount(string type) => type == "SCALAR" ? 1 : type == "VEC2" ? 2 : type == "VEC3" ? 3 : type == "VEC4" ? 4 : 1;

        static float ReadComp(byte[] d, int o, int ct, bool normalized)
        {
            switch (ct)
            {
                case 5126: return BitConverter.ToSingle(d, o);
                case 5125: return BitConverter.ToUInt32(d, o);
                case 5123: { float v = BitConverter.ToUInt16(d, o); return normalized ? v / 65535f : v; }
                case 5122: { float v = BitConverter.ToInt16(d, o); return normalized ? Mathf.Max(v / 32767f, -1f) : v; }
                case 5121: { float v = d[o]; return normalized ? v / 255f : v; }
                case 5120: { float v = (sbyte)d[o]; return normalized ? Mathf.Max(v / 127f, -1f) : v; }
            }
            return 0f;
        }

        static float[] ReadAccessor(Glb g, int accIdx, out int comps)
        {
            var acc = g.Json.At("accessors", accIdx);
            int count = acc.Int("count"), ct = acc.Int("componentType");
            comps = CompCount(acc.Get("type") as string);
            bool normalized = acc.Has("normalized") && (bool)acc.Raw["normalized"];
            var outp = new float[count * comps];
            if (!acc.Has("bufferView")) return outp;
            var (data, stride) = ReadBufferView(g, acc.Int("bufferView"));
            int cs = CompSize(ct);
            int elem = stride > 0 ? stride : cs * comps;
            int start = acc.Int("byteOffset");
            for (int i = 0; i < count; i++)
                for (int c = 0; c < comps; c++)
                    outp[i * comps + c] = ReadComp(data, start + i * elem + c * cs, ct, normalized);
            return outp;
        }

        static Vector3[] ReadVec3(Glb g, int acc)
        {
            var f = ReadAccessor(g, acc, out int c);
            var v = new Vector3[f.Length / c];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector3(f[i * c], f[i * c + 1], c > 2 ? f[i * c + 2] : 0f);
            return v;
        }
        static Vector2[] ReadVec2(Glb g, int acc)
        {
            var f = ReadAccessor(g, acc, out int c);
            var v = new Vector2[f.Length / c];
            for (int i = 0; i < v.Length; i++) v[i] = new Vector2(f[i * c], f[i * c + 1]);
            return v;
        }
        static int[] ReadInts(Glb g, int acc)
        {
            var f = ReadAccessor(g, acc, out int c);
            var v = new int[f.Length];
            for (int i = 0; i < v.Length; i++) v[i] = (int)f[i];
            return v;
        }
    }

    /// <summary>아주 작은 JSON 파서 (객체→Dictionary, 배열→List, 숫자→double, 그 외 string/bool/null).</summary>
    public static class MiniJson
    {
        public static object Parse(string s) { int i = 0; return Value(s, ref i); }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (s[i] == '}') { i++; return d; }
                    string k = Str(s, ref i);
                    Ws(s, ref i); i++;   // ':'
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') i++;
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (s[i] == ']') { i++; return l; }
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') i++;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            return double.Parse(s.Substring(st, i - st), System.Globalization.CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder(); i++;   // 여는 따옴표
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>문자열을 JSON 문자열 리터럴로.</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\""); else if (c == '\\') sb.Append("\\\\"); else if (c == '\n') sb.Append("\\n"); else if (c == '\r') sb.Append("\\r"); else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
