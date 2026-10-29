using System.Net;
using System.Text;
using System.Text.Json;

namespace KnifeWar.Backend;

/// <summary>
/// 표준 라이브러리 HttpListener 위의 아주 작은 라우터 (외부 프레임워크 없음).
/// 상용 단계에서 앞단에 리버스 프록시(nginx 등)를 두고 HTTPS를 처리한다.
/// </summary>
public sealed class HttpServer
{
    public sealed class Req
    {
        public HttpListenerRequest Raw;
        public string Body;
        public Dictionary<string, string> Params = new();
        public string Header(string name) => Raw.Headers[name] ?? "";
        public T Json<T>() where T : class
        {
            if (string.IsNullOrWhiteSpace(Body)) return null;
            try { return JsonSerializer.Deserialize<T>(Body, Web); } catch { return null; }
        }
    }

    public readonly struct Res
    {
        public readonly int Code;
        public readonly string Body;
        public readonly string ContentType;
        public Res(int code, string body, string type = "application/json") { Code = code; Body = body; ContentType = type; }
    }

    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static Res Ok(object o) => new(200, JsonSerializer.Serialize(o, Web));
    public static Res Ok() => new(200, "{}");
    public static Res Text(string s) => new(200, s, "text/plain");
    public static Res Status(int code, object o = null) => new(code, o == null ? "{}" : JsonSerializer.Serialize(o, Web));

    readonly List<(string method, string[] parts, Func<Req, Task<Res>> handler)> _routes = new();

    // ---- IP별 요청 제한 (토큰 버킷): 초당 RatePerSec, 순간 최대 Burst. 게임 서버(X-Server-Secret)는 제외 ----
    public double RatePerSec = 10, Burst = 40;

    // ---- 개발용 정적 파일 (패치 테스트): /patch/... → StaticDir. 상용에서는 CDN을 쓴다 ----
    public string StaticPrefix = "/patch/";
    public string StaticDir;

    async Task<bool> TryStatic(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "";
        if (string.IsNullOrEmpty(StaticDir) || ctx.Request.HttpMethod != "GET" || !path.StartsWith(StaticPrefix)) return false;
        string rel = Uri.UnescapeDataString(path[StaticPrefix.Length..]).Replace('/', Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(StaticDir);
        string full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(root) || !File.Exists(full)) { await Write(ctx, Status(404)); return true; }
        try
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = full.EndsWith(".json") ? "application/json" : "application/octet-stream";
            ctx.Response.ContentLength64 = new FileInfo(full).Length;
            await using (var fs = File.OpenRead(full)) await fs.CopyToAsync(ctx.Response.OutputStream);
            ctx.Response.Close();
        }
        catch { }
        return true;
    }
    readonly Dictionary<string, (double tokens, long at)> _buckets = new();
    long _lastBucketSweep;

    bool Allow(HttpListenerRequest r)
    {
        if (!string.IsNullOrEmpty(r.Headers["X-Server-Secret"])) return true;
        string ip = r.RemoteEndPoint?.Address.ToString() ?? "?";
        // 앞단 리버스 프록시(같은 컴퓨터)를 거치면 실제 IP는 X-Forwarded-For 첫 값
        if (r.RemoteEndPoint != null && IPAddress.IsLoopback(r.RemoteEndPoint.Address))
        {
            var fwd = r.Headers["X-Forwarded-For"];
            if (!string.IsNullOrEmpty(fwd)) ip = fwd.Split(',')[0].Trim();
        }
        long now = Environment.TickCount64;
        lock (_buckets)
        {
            if (now - _lastBucketSweep > 60_000)
            {
                _lastBucketSweep = now;
                foreach (var k in _buckets.Where(kv => now - kv.Value.at > 60_000).Select(kv => kv.Key).ToList()) _buckets.Remove(k);
            }
            var b = _buckets.TryGetValue(ip, out var v) ? v : (Burst, now);
            double tokens = Math.Min(Burst, b.Item1 + (now - b.Item2) / 1000.0 * RatePerSec);
            if (tokens < 1) { _buckets[ip] = (tokens, now); return false; }
            _buckets[ip] = (tokens - 1, now);
            return true;
        }
    }
    readonly HttpListener _listener = new();
    readonly Action<string> _log;

    public HttpServer(IEnumerable<string> prefixes, Action<string> log)
    {
        foreach (var p in prefixes) _listener.Prefixes.Add(p);
        _log = log;
    }

    public void Map(string method, string path, Func<Req, Res> h) => _routes.Add((method, Split(path), r => Task.FromResult(h(r))));
    public void MapAsync(string method, string path, Func<Req, Task<Res>> h) => _routes.Add((method, Split(path), h));

    static string[] Split(string path) => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (ct.IsCancellationRequested) { break; }
            _ = Task.Run(() => Handle(ctx));
        }
        _listener.Stop();
    }

    async Task Handle(HttpListenerContext ctx)
    {
        Res res;
        try
        {
            if (await TryStatic(ctx)) return;
            if (!Allow(ctx.Request)) { await Write(ctx, Status(429)); return; }
            var req = new Req { Raw = ctx.Request };
            if (ctx.Request.HasEntityBody)
            {
                if (ctx.Request.ContentLength64 > 64 * 1024) { await Write(ctx, Status(413)); return; }
                using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                req.Body = await sr.ReadToEndAsync();
            }
            res = await Route(req);
        }
        catch (Exception e)
        {
            _log($"error {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: {e}");
            res = Status(500);
        }
        await Write(ctx, res);
    }

    async Task<Res> Route(Req req)
    {
        var parts = Split(req.Raw.Url?.AbsolutePath ?? "/");
        foreach (var (method, pattern, handler) in _routes)
        {
            if (method != req.Raw.HttpMethod || pattern.Length != parts.Length) continue;
            bool match = true;
            req.Params.Clear();
            for (int i = 0; i < parts.Length && match; i++)
            {
                if (pattern[i].StartsWith('{')) req.Params[pattern[i].Trim('{', '}')] = Uri.UnescapeDataString(parts[i]);
                else if (!string.Equals(pattern[i], parts[i], StringComparison.OrdinalIgnoreCase)) match = false;
            }
            if (match) return await handler(req);
        }
        return Status(404);
    }

    static async Task Write(HttpListenerContext ctx, Res res)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(res.Body ?? "");
            ctx.Response.StatusCode = res.Code;
            ctx.Response.ContentType = res.ContentType + "; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch { /* 클라가 먼저 끊음 */ }
    }
}
