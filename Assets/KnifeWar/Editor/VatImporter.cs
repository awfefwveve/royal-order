using System;
using System.IO;
using KnifeWar.Game;
using UnityEditor;
using UnityEngine;

namespace KnifeWar.EditorTools
{
    /// <summary>
    /// 구운 애니메이션(.kvat, Blender 에서 만든 정점 위치 데이터) → 유니티 메시 + 위치/법선 텍스처 + VatStandard 재질 → SkinSet.MonsterAnim[종류].
    /// 명령: Logs/claude_vatimport.txt 에 "종류번호 파일경로 [idle=a,b] [windup=a,b] [strike=a,b] [recover=a,b]"
    /// 텍스처·법선맵은 같은 종류의 기존 재질(MonsterMaterial)에서 가져온다.
    /// </summary>
    public static class VatImporter
    {
        const string SkinPath = "Assets/KnifeWar/Resources/KnifeWar/SkinSet.asset";
        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        [MenuItem("KnifeWar/아트/구운 애니메이션 가져오기 (Logs/claude_vatimport.txt)", priority = 32)]
        public static void RunFromFile()
        {
            string path = Path.Combine(ProjectRoot, "Logs/claude_vatimport.txt");
            if (!File.Exists(path)) { Debug.LogError("[vat] 명령 파일 없음"); return; }
            string args = File.ReadAllText(path).Trim();
            File.Delete(path);
            try { Run(args); }
            catch (Exception e) { Debug.LogError("[vat] 실패: " + e); }
        }

        static Vector2Int Range(string v) { var p = v.Split(','); return new Vector2Int(int.Parse(p[0]), int.Parse(p[1])); }

        public static void Run(string args)
        {
            var parts = args.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int type = int.Parse(parts[0]);
            string file = parts[1];
            if (!Path.IsPathRooted(file)) file = Path.GetFullPath(Path.Combine(ProjectRoot, file));
            var clip = new MonsterVat();
            for (int i = 2; i < parts.Length; i++)
            {
                var kv = parts[i].Split('=');
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "idle": clip.Idle = Range(kv[1]); break;
                    case "windup": clip.Windup = Range(kv[1]); break;
                    case "strike": clip.Strike = Range(kv[1]); break;
                    case "recover": clip.Recover = Range(kv[1]); break;
                }
            }

            int nv, nf, ntri; float fps;
            Vector2[] uv; int[] tris; Vector4[] pos, nrm;
            using (var br = new BinaryReader(File.OpenRead(file)))
            {
                if (new string(br.ReadChars(4)) != "KVAT") throw new Exception("KVAT 파일 아님");
                br.ReadInt32();                 // 버전
                nv = br.ReadInt32(); nf = br.ReadInt32(); fps = br.ReadSingle(); ntri = br.ReadInt32();
                uv = new Vector2[nv];
                for (int i = 0; i < nv; i++) uv[i] = new Vector2(br.ReadSingle(), br.ReadSingle());
                tris = new int[ntri * 3];
                for (int i = 0; i < tris.Length; i++) tris[i] = br.ReadInt32();
                pos = new Vector4[nv * nf]; nrm = new Vector4[nv * nf];
                for (int i = 0; i < pos.Length; i++) pos[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), 1f);
                for (int i = 0; i < nrm.Length; i++) nrm[i] = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), 0f);
            }
            if (nv > SystemInfo.maxTextureSize) throw new Exception($"정점 {nv} 개가 텍스처 최대 폭 {SystemInfo.maxTextureSize} 보다 많음");

            string name = Path.GetFileNameWithoutExtension(file);
            string dir = $"Assets/KnifeWar/Art/Gen/vat_{name}";
            Directory.CreateDirectory(Path.Combine(ProjectRoot, dir));

            // 메시: 첫 프레임 자세 + 정점 번호 (uv2.x)
            var verts = new Vector3[nv]; var norms = new Vector3[nv]; var idx = new Vector2[nv];
            for (int i = 0; i < nv; i++) { verts[i] = pos[i]; norms[i] = nrm[i]; idx[i] = new Vector2(i, 0f); }
            var mesh = new Mesh { name = name, indexFormat = nv > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(verts); mesh.SetNormals(norms); mesh.SetUVs(0, uv); mesh.SetUVs(2, idx);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            // 경계: 모든 프레임을 덮게 (컬링에 잘리지 않도록)
            var b = new Bounds(pos[0], Vector3.zero);
            for (int i = 0; i < pos.Length; i += 7) b.Encapsulate(pos[i]);
            b.Expand(0.3f);
            mesh.bounds = b;
            mesh.UploadMeshData(false);

            Texture2D Tex(Vector4[] data, string suffix)
            {
                var t = new Texture2D(nv, nf, TextureFormat.RGBAHalf, false, true) { name = name + suffix, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var cols = new Color[data.Length];
                for (int i = 0; i < data.Length; i++) cols[i] = new Color(data[i].x, data[i].y, data[i].z, data[i].w);
                t.SetPixels(cols); t.Apply(false, false);
                return t;
            }
            var posTex = Tex(pos, "_pos");
            var nrmTex = Tex(nrm, "_nrm");

            Replace(mesh, $"{dir}/{name}_mesh.asset");
            Replace(posTex, $"{dir}/{name}_pos.asset");
            Replace(nrmTex, $"{dir}/{name}_nrm.asset");

            var skin = AssetDatabase.LoadAssetAtPath<SkinSet>(SkinPath);
            if (skin == null) throw new Exception("SkinSet 없음: " + SkinPath);
            var shader = Shader.Find("KnifeWar/VatStandard");
            if (shader == null) throw new Exception("VatStandard 셰이더를 못 찾음");
            var mat = new Material(shader) { name = name, enableInstancing = true };
            var src = skin.MonsterMaterial != null && type < skin.MonsterMaterial.Length ? skin.MonsterMaterial[type] : null;
            if (src != null)
            {
                mat.SetTexture("_MainTex", src.mainTexture);
                if (src.HasProperty("_BumpMap")) mat.SetTexture("_BumpMap", src.GetTexture("_BumpMap"));
                if (src.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", src.GetFloat("_Glossiness"));
            }
            mat.SetTexture("_VatPos", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{name}_pos.asset"));
            mat.SetTexture("_VatNrm", AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/{name}_nrm.asset"));
            Replace(mat, $"{dir}/{name}.mat");

            clip.Mesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{dir}/{name}_mesh.asset");
            clip.Material = AssetDatabase.LoadAssetAtPath<Material>($"{dir}/{name}.mat");
            clip.Frames = nf; clip.Fps = fps;
            if (skin.MonsterAnim == null || skin.MonsterAnim.Length < 7) Array.Resize(ref skin.MonsterAnim, 7);
            skin.MonsterAnim[type] = clip;
            EditorUtility.SetDirty(skin);
            AssetDatabase.SaveAssets();
            Debug.Log($"[vat] 종류 {type}: {name} 정점 {nv} 프레임 {nf} ({fps}fps) — 대기 {clip.Idle} 준비 {clip.Windup} 타격 {clip.Strike} 복귀 {clip.Recover}");
        }

        static void Replace(UnityEngine.Object obj, string path)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(obj, path);
        }
    }
}
