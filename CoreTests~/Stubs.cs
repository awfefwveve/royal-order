// Minimal Unity API stubs — only for type-checking outside Unity. Not shipped.
using System;

namespace Unity.Burst
{
    public class BurstCompileAttribute : Attribute { public bool CompileSynchronously { get; set; } }
}

namespace Unity.Collections
{
    public enum Allocator { Temp, TempJob, Persistent }
    public class ReadOnlyAttribute : Attribute { }
    public struct NativeArray<T> : IDisposable where T : struct
    {
        T[] _a; int _o; int _n;
        public NativeArray(int length, Allocator a) { _a = new T[length]; _o = 0; _n = length; }
        public T this[int i] { get { if ((uint)i >= (uint)_n) throw new IndexOutOfRangeException(); return _a[_o + i]; } set { if ((uint)i >= (uint)_n) throw new IndexOutOfRangeException(); _a[_o + i] = value; } }
        public int Length => _n;
        public bool IsCreated => _a != null;
        public NativeArray<T> GetSubArray(int start, int length) { var r = this; r._o = _o + start; r._n = length; return r; }
        public void Dispose() { }
    }
    public struct NativeList<T> : IDisposable where T : unmanaged
    {
        System.Collections.Generic.List<T> _l;
        public NativeList(int cap, Allocator a) { _l = new System.Collections.Generic.List<T>(cap); }
        public T this[int i] { get => _l[i]; set => _l[i] = value; }
        public int Length => _l.Count;
        public bool IsCreated => _l != null;
        public void Add(T v) { _l.Add(v); }
        public void RemoveAt(int i) { _l.RemoveAt(i); }
        public void Clear() { _l.Clear(); }
        public void Dispose() { }
    }
}

namespace Unity.Jobs
{
    public struct JobHandle { public void Complete() { } }
    public interface IJob { void Execute(); }
    public interface IJobParallelFor { void Execute(int index); }
    public static class IJobExtensions
    {
        public static JobHandle Schedule<T>(this T job, JobHandle dependsOn = default) where T : struct, IJob { job.Execute(); return default; }
        public static void Run<T>(this T job) where T : struct, IJob { job.Execute(); }
    }
    public static class IJobParallelForExtensions
    {
        public static JobHandle Schedule<T>(this T job, int len, int batch, JobHandle dependsOn = default) where T : struct, IJobParallelFor { for (int i = 0; i < len; i++) job.Execute(i); return default; }
    }
}

namespace Unity.Jobs.LowLevel.Unsafe
{
    public static class JobsUtility
    {
        public static int JobWorkerCount { get; set; }
        public static int JobWorkerMaximumCount => 8;
    }
}

namespace Unity.Mathematics
{
    public struct int2 { public int x, y; public int2(int x, int y) { this.x = x; this.y = y; } }
    public struct float2
    {
        public float x, y;
        public float2(float x, float y) { this.x = x; this.y = y; }
        public static float2 operator +(float2 a, float2 b) => new float2(a.x + b.x, a.y + b.y);
        public static float2 operator -(float2 a, float2 b) => new float2(a.x - b.x, a.y - b.y);
        public static float2 operator *(float2 a, float b) => new float2(a.x * b, a.y * b);
    }
    public struct float3
    {
        public float x, y, z;
        public float3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float3(float v) { x = y = z = v; }
        public static float3 operator +(float3 a, float3 b) => new float3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static float3 operator -(float3 a, float3 b) => new float3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static float3 operator *(float3 a, float b) => new float3(a.x * b, a.y * b, a.z * b);
        public override string ToString() => $"({x:F2},{y:F2},{z:F2})";
    }
    public struct quaternion { public static quaternion RotateY(float a) => default; }
    public struct float4x4 { public static float4x4 TRS(float3 t, quaternion r, float3 s) => default; }
    public static class math
    {
        public const float PI = 3.14159f;
        public static int clamp(int x, int a, int b) => Math.Clamp(x, a, b);
        public static int max(int a, int b) => Math.Max(a, b);
        public static int min(int a, int b) => Math.Min(a, b);
        public static float min(float a, float b) => Math.Min(a, b);
        public static float max(float a, float b) => Math.Max(a, b);
        public static float ceil(float x) => MathF.Ceiling(x);
        public static float round(float x) => MathF.Round(x);
        public static float saturate(float x) => Math.Clamp(x, 0f, 1f);
        public static float abs(float x) => MathF.Abs(x);
        public static float sin(float x) => MathF.Sin(x);
        public static float lerp(float a, float b, float t) => a + (b - a) * t;
        public static float dot(float2 a, float2 b) => a.x * b.x + a.y * b.y;
        public static float lengthsq(float2 a) => a.x * a.x + a.y * a.y;
        public static float2 normalizesafe(float2 a) { var l = MathF.Sqrt(lengthsq(a)); return l > 0 ? new float2(a.x / l, a.y / l) : default; }
        public static float cos(float x) => MathF.Cos(x);
        public static float sqrt(float x) => MathF.Sqrt(x);
        public static float clamp(float x, float a, float b) => Math.Clamp(x, a, b);
        public static float dot(float3 a, float3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static float lengthsq(float3 a) => dot(a, a);
        public static float3 normalizesafe(float3 a) { var l = MathF.Sqrt(lengthsq(a)); return l > 0 ? a * (1f / l) : default; }
        public static float3 lerp(float3 a, float3 b, float t) => a + (b - a) * t;
        public static uint hash(int2 v) => (uint)(v.x * 73856093 ^ v.y * 19349663) * 2654435761u;
    }
}

namespace UnityEngine
{
    public class Object { public static void Destroy(Object o) { } public static void DestroyImmediate(Object o) { } }
    public class Component : Object { public GameObject gameObject => null; public T GetComponent<T>() => default; }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class GameObject : Object
    {
        public static GameObject CreatePrimitive(PrimitiveType t) => null;
        public T GetComponent<T>() => default;
    }
    public enum PrimitiveType { Capsule, Cube }
    public class MeshFilter : Component { public Mesh sharedMesh; }
    public class Mesh : Object { }
    public class Shader : Object { public static Shader Find(string n) => null; }
    public class Material : Object { public Material(Shader s) { } public bool enableInstancing { get; set; } public Color color { get; set; } }
    public struct Color { public Color(float r, float g, float b) { } }
    public struct Vector3 { public static Vector3 zero; public Vector3(float x, float y, float z) { } }
    public struct Bounds { public Bounds(Vector3 c, Vector3 s) { } }
    public struct RenderParams
    {
        public RenderParams(Material m) { worldBounds = default; shadowCastingMode = default; receiveShadows = true; }
        public Bounds worldBounds; public Rendering.ShadowCastingMode shadowCastingMode; public bool receiveShadows;
    }
    public static class Graphics
    {
        public static void RenderMeshInstanced<T>(in RenderParams rp, Mesh mesh, int sub, Unity.Collections.NativeArray<T> data, int count = -1, int start = 0) where T : unmanaged { }
    }
    public static class QualitySettings { public static int vSyncCount; }
    public static class Application
    {
        public static int targetFrameRate; public static string persistentDataPath => ""; public static bool isBatchMode => false;
        public static void Quit(int c) { }
    }
    public static class Debug { public static void Log(object o) { } public static void LogWarning(object o) { } public static void LogError(object o) { } }
    public static class Mathf { public static int Clamp(int v, int a, int b) => v; }
    public static class Time { public static float unscaledDeltaTime => 0; public static float time => 0; }
    public struct Rect { public float y; public Rect(float a, float b, float c, float d) { y = b; } }
    public static class GUI { public static void Label(Rect r, string s) { } }
    public class HeaderAttribute : Attribute { public HeaderAttribute(string s) { } }
    public class TooltipAttribute : Attribute { public TooltipAttribute(string s) { } }
    public class RangeAttribute : Attribute { public RangeAttribute(float a, float b) { } }
}

namespace UnityEngine.Rendering
{
    public enum ShadowCastingMode { Off, On }
}
