using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace TokenHub.Services;

/// <summary>
/// OpenAI-compatible proxy on 127.0.0.1 that keeps a pool of Token Harbor keys and
/// retries the same request on the next key when one runs out of credit. OpenCode
/// never sees the failure, so a conversation can continue without restarting.
/// </summary>
public sealed class LocalProxy
{
    public const int DefaultPort = 8477;

    public static LocalProxy Instance { get; } = new();

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly object _gate = new();

    private Func<AppSettings> _settings = static () => new AppSettings();
    private Action<AppSettings> _save = static _ => { };

    private WebApplication? _app;
    private string? _activeKeyId;
    private string _lastError = string.Empty;
    private int _requests;
    private int _failovers;
    private int _cursor;
    private long _promptTokens;
    private long _completionTokens;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeyStat> _perKey = new();

    /// <summary>Per-key counters, shown on the stats dashboard (items V3/V4/T15).</summary>
    public sealed class KeyStat
    {
        public int Requests;
        public int Failures;
        public long TotalMs;
        public long LastMs;
        public DateTime? LastUtc;
    }

    /// <summary>Raised on each key switch, and when the pool runs dry. Arg: title, text.</summary>
    public event Action<string, string>? Notice;

    /// <summary>
    /// Connects the proxy to whoever owns the settings. The app points at
    /// <c>%APPDATA%</c>, the Windows service at <c>%ProgramData%</c>.
    /// </summary>
    public void Configure(Func<AppSettings> settings, Action<AppSettings> save)
    {
        _settings = settings;
        _save = save;
    }

    private AppSettings Settings => _settings();

    public bool Running => _app is not null;

    public int Port { get; private set; }

    public string? ActiveKeyId
    {
        get { lock (_gate) { return _activeKeyId; } }
    }

    public string LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    public int Requests => Volatile.Read(ref _requests);

    public int Failovers => Volatile.Read(ref _failovers);

    public long PromptTokens => Interlocked.Read(ref _promptTokens);

    public long CompletionTokens => Interlocked.Read(ref _completionTokens);

    /// <summary>Per-key counters for the dashboard, or null when the key has never been used.</summary>
    public KeyStat? StatOf(string id) => _perKey.TryGetValue(id, out var stat) ? stat : null;

    public void ResetStats()
    {
        _perKey.Clear();
        Interlocked.Exchange(ref _promptTokens, 0);
        Interlocked.Exchange(ref _completionTokens, 0);
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _failovers, 0);
    }

    /// <summary>Starts the listener. <paramref name="fixedPort"/> disables the port search.</summary>
    public async Task<bool> StartAsync(int fixedPort = 0)
    {
        if (Running)
        {
            return true;
        }

        var settings = Settings;
        int start = fixedPort > 0
            ? fixedPort
            : settings.LocalPort is > 1024 and < 65000 ? settings.LocalPort : DefaultPort;
        int attempts = fixedPort > 0 ? 1 : 10;

        for (int offset = 0; offset < attempts; offset++)
        {
            int port = start + offset;
            try
            {
                var app = Build(port);
                await app.StartAsync();

                _app = app;
                Port = port;

                if (fixedPort == 0 && settings.LocalPort != port)
                {
                    settings.LocalPort = port;
                    _save(settings);
                }

                return true;
            }
            catch (IOException)
            {
                // Port already taken by another program: try the next one.
            }
            catch (Exception ex)
            {
                SetError("Локальный прокси не запустился: " + ex.Message);
                return false;
            }
        }

        SetError($"Порт {start} занят — локальный прокси не запустился");
        return false;
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;

        if (app is null)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(timeout.Token);
        }
        catch
        {
            // Shutting down anyway.
        }

        await app.DisposeAsync();
    }

    private static WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "TokenHub.LocalProxy",
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = 64L * 1024 * 1024);

        var app = builder.Build();
        app.Run(Instance.HandleAsync);
        return app;
    }

    // ------------------------------------------------------------------ requests

    private async Task HandleAsync(HttpContext context)
    {
        var cancelled = context.RequestAborted;
        string path = context.Request.Path.Value ?? "/";

        if (path is "/__status" or "/status")
        {
            await WriteJsonAsync(context, StatusJson());
            return;
        }

        byte[]? body = await ReadBodyAsync(context.Request, cancelled);
        string clientAuth = context.Request.Headers.Authorization.ToString();
        string? model = ModelOf(body);

        var settings = Settings;

        string url = BuildUrl(path, context.Request.QueryString.Value);
        var tried = new HashSet<string>();

        // Walk every usable key, then (when nothing is ready) probe the key closest to
        // recovering. The old code fixed the attempt count once, before the request, so a
        // pool whose second key was cooling down or had been flagged once answered
        // "no failover" without ever looking at that key.
        for (int attempt = 0; attempt <= settings.Keys.Count; attempt++)
        {
            bool allowProbe = settings.AutoFailover && attempt > 0;
            var key = NextCandidate(tried, model, allowProbe);
            string? authorization = key is null
                ? (string.IsNullOrEmpty(clientAuth) ? null : clientAuth)
                : $"Bearer {key.Key}";

            using var request = BuildRequest(context, url, body, authorization);
            HttpResponseMessage response;
            long started = Environment.TickCount64;

            try
            {
                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancelled);
            }
            catch (Exception ex) when (!cancelled.IsCancellationRequested)
            {
                SetError("Ошибка сети: " + ex.Message);
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                await WriteJsonAsync(context, ErrorJson("proxy_network_error", ex.Message));
                return;
            }

            using (response)
            {
                int status = (int)response.StatusCode;
                long elapsed = Environment.TickCount64 - started;

                if (status >= 400)
                {
                    string text = await ReadLimitedAsync(response, cancelled);
                    var fault = PoolPolicy.Classify(status, text);

                    if (key is not null && settings.AutoFailover && fault != KeyFault.None)
                    {
                        string? retryAfter = response.Headers.RetryAfter?.ToString();
                        MarkKey(key, fault, status, text, model, retryAfter);
                        Record(key.Id, failure: true, elapsed);
                        tried.Add(key.Id);
                        Interlocked.Increment(ref _failovers);

                        // Item T15: a redacted one-line trace; the key itself never appears.
                        EventLog.Write(
                            $"Переключение: {Dpapi.Mask(key.Key)} · {model ?? "модель не указана"} · HTTP {status}");
                        continue;
                    }

                    if (key is not null)
                    {
                        Record(key.Id, failure: true, elapsed);
                        EventLog.Write(
                            $"Ошибка ответа: {Dpapi.Mask(key.Key)} · {model ?? "модель не указана"} · HTTP {status}");
                    }

                    await RelayAsync(context, response, text, cancelled);
                    return;
                }

                if (key is not null)
                {
                    MarkHealthy(key, model);
                    Record(key.Id, failure: false, elapsed);
                }

                Interlocked.Increment(ref _requests);

                // JSON answers are buffered so token usage can be counted (item T15);
                // streamed answers (SSE) are relayed chunk by chunk, counting usage on
                // the way through.
                string? buffered = null;
                if (IsJson(response))
                {
                    buffered = await ReadAllAsync(response, cancelled);
                    new UsageScanner(AddUsage).ScanJson(buffered);
                }

                await RelayAsync(context, response, buffered, cancelled);
                return;
            }
        }

        string reason = string.IsNullOrEmpty(LastError) ? string.Empty : $" Последняя ошибка: {LastError}.";
        string message = "Все ключи Token Harbor исчерпаны или недействительны." + reason +
                         " Добавьте рабочий ключ в TokenHub.";

        SetError(message);
        Raise("Пул ключей исчерпан", message);
        EventLog.Write("Пул исчерпан: " + message);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await WriteJsonAsync(context, ErrorJson("all_keys_exhausted", message));
    }

    private static HttpRequestMessage BuildRequest(
        HttpContext context,
        string url,
        byte[]? body,
        string? authorization)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), url);

        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (body is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(body);
            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        foreach (var header in context.Request.Headers)
        {
            if (HopByHop(header.Key) || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        return request;
    }

    private static bool HopByHop(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase);

    private async Task RelayAsync(
        HttpContext context,
        HttpResponseMessage response,
        string? buffered,
        CancellationToken cancelled)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        if (response.Content.Headers.ContentType is { } contentType)
        {
            context.Response.ContentType = contentType.ToString();
        }

        foreach (var header in response.Headers)
        {
            if (header.Key.StartsWith("X-RateLimit", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        if (buffered is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(buffered);
            context.Response.ContentLength = bytes.Length;
            await context.Response.Body.WriteAsync(bytes, cancelled);
            return;
        }

        if (response.Content.Headers.ContentLength is long length)
        {
            context.Response.ContentLength = length;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancelled);

        // Flush every chunk: server-sent events must reach the client immediately.
        var usage = new UsageScanner(AddUsage);
        var buffer = new byte[16 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, cancelled);
            }
            catch (Exception) when (cancelled.IsCancellationRequested)
            {
                return;
            }

            if (read <= 0)
            {
                return;
            }

            usage.Scan(Encoding.UTF8.GetString(buffer, 0, read));

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancelled);
            await context.Response.Body.FlushAsync(cancelled);
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancelled)
    {
        bool hasBody = request.ContentLength > 0
                       || request.Headers.ContainsKey("Transfer-Encoding");

        if (!hasBody)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancelled);
        return buffer.ToArray();
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancelled)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancelled);
            var buffer = new byte[64 * 1024];
            int total = 0;
            int read;
            while (total < buffer.Length
                   && (read = await stream.ReadAsync(buffer.AsMemory(total), cancelled)) > 0)
            {
                total += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsJson(HttpResponseMessage response)
    {
        string? type = response.Content.Headers.ContentType?.MediaType;
        return type is not null
               && type.Contains("json", StringComparison.OrdinalIgnoreCase)
               && !type.Contains("event-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads the whole body but caps it, so a rogue answer cannot exhaust memory.</summary>
    private static async Task<string> ReadAllAsync(HttpResponseMessage response, CancellationToken cancelled)
    {
        const int cap = 4 * 1024 * 1024;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancelled);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancelled)) > 0 && buffer.Length < cap)
            {
                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Adds the usage of one answer to the running totals, in one place.</summary>
    private void AddUsage(long prompt, long completion)
    {
        if (prompt != 0)
        {
            Interlocked.Add(ref _promptTokens, prompt);
        }

        if (completion != 0)
        {
            Interlocked.Add(ref _completionTokens, completion);
        }

        // Session counters die with the process; this one survives restarts and is
        // what the dashboard shows as "today".
        UsageStore.Add(prompt, completion);
    }

    /// <summary>Reads the requested model out of the JSON body, when there is one.</summary>
    private static string? ModelOf(byte[]? body)
    {
        if (body is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("model", out var model)
                && model.GetString() is { Length: > 0 } name)
            {
                return name;
            }
        }
        catch
        {
            // Not JSON, or not an object: no model to remember.
        }

        return null;
    }

    // ------------------------------------------------------------------ key pool

    private ApiKeyEntry? NextCandidate(HashSet<string> tried, string? model, bool allowProbe)
    {
        var settings = Settings;

        lock (_gate)
        {
            var pool = settings.Keys
                .Where(k => k.Enabled && k.Allows(model) && !tried.Contains(k.Id))
                .ToList();

            // Keys that can serve right now always win.
            var usable = pool.Where(k => k.Available).ToList();

            // Nothing is ready: probe the key that recovers soonest instead of telling
            // OpenCode "all keys exhausted" and leaving the user to fix it by hand.
            if (usable.Count == 0 && allowProbe)
            {
                usable = PoolPolicy.ProbeOrder(pool).Take(1).ToList();
            }

            if (usable.Count == 0)
            {
                return null;
            }

            var ranked = PoolPolicy.Rank(usable, _activeKeyId, settings.KeyStrategy, Usage, _cursor);
            if (settings.KeyStrategy == "roundRobin")
            {
                _cursor++;
            }

            return ranked[0];
        }
    }

    private int Usage(string id) => _perKey.TryGetValue(id, out var stat) ? stat.Requests : 0;

    private void Record(string id, bool failure, long elapsedMs)
    {
        var stat = _perKey.GetOrAdd(id, _ => new KeyStat());
        lock (stat)
        {
            if (failure)
            {
                stat.Failures++;
            }
            else
            {
                stat.Requests++;
            }

            stat.TotalMs += elapsedMs;
            stat.LastMs = elapsedMs;
            stat.LastUtc = DateTime.UtcNow;
        }
    }

    // Classification of a failed response lives in PoolPolicy so it can be unit-tested.

    private void MarkKey(ApiKeyEntry key, KeyFault fault, int status, string body, string? model, string? retryAfter)
    {
        string reason = ShortReason(body);
        string suffix = string.IsNullOrEmpty(reason) ? string.Empty : $" · {reason}";
        string note;

        lock (_gate)
        {
            switch (fault)
            {
                case KeyFault.Invalid:
                    key.Invalid = true;
                    key.LastStatus = $"недействителен{suffix}";
                    note = $"Ключ «{key.Label}» недействителен — переключаюсь на следующий";
                    break;

                case KeyFault.ModelBlocked:
                    if (!string.IsNullOrEmpty(model) && !key.BlockedModels.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        key.BlockedModels.Add(model);
                    }

                    key.LastStatus = $"модель {model ?? "?"} недоступна{suffix}";
                    note = $"Ключ «{key.Label}» не подходит для модели {model ?? "?"} — беру следующий";
                    break;

                default:
                    key.ExhaustedUntilUtc = DateTime.UtcNow.Add(PoolPolicy.Cooldown(retryAfter, Settings.CooldownMinutes));
                    key.LastStatus = $"исчерпан (HTTP {status}){suffix}";
                    note = $"Ключ «{key.Label}» исчерпан — переключаюсь на следующий";
                    break;
            }

            key.LastCheckUtc = DateTime.UtcNow;
            _activeKeyId = null;
            _lastError = $"{key.Label}: {key.LastStatus}";
        }

        _save(Settings);
        Raise("TokenHub: смена ключа", note);
    }

    private void MarkHealthy(ApiKeyEntry key, string? model)
    {
        bool changed;

        lock (_gate)
        {
            changed = _activeKeyId != key.Id;
            _activeKeyId = key.Id;
            _lastError = string.Empty;

            // A key that just served a request is definitely not out of order: clear both
            // the cooldown and a previous (possibly wrong) "invalid" flag, so one
            // transient 401 can never leave a working key switched off forever.
            if (key.ExhaustedUntilUtc is not null)
            {
                key.ExhaustedUntilUtc = null;
                changed = true;
            }

            if (key.Invalid)
            {
                key.Invalid = false;
                changed = true;
            }
        }

        if (changed)
        {
            _save(Settings);
        }

        _ = model;
    }

    public void ResetCooldowns()
    {
        lock (_gate)
        {
            foreach (var key in Settings.Keys)
            {
                key.ExhaustedUntilUtc = null;
                key.Invalid = false;
                key.BlockedModels.Clear();
                key.LastStatus = "не проверен";
            }

            _activeKeyId = null;
            _lastError = string.Empty;
        }

        _save(Settings);
    }

    private void Raise(string title, string text)
    {
        try
        {
            Notice?.Invoke(title, text);
        }
        catch
        {
            // A notification must never break a request.
        }
    }

    /// <summary>Pulls Token Harbor's machine-readable reason out of an error body.</summary>
    private static string ShortReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                foreach (var field in new[] { "code", "type" })
                {
                    if (error.TryGetProperty(field, out var value) && value.GetString() is { Length: > 0 } text)
                    {
                        // Token Harbor codes are snake_case API jargon; show something a
                        // person reads at a glance in the key list.
                        return text switch
                        {
                            "free_tier_limit_reached" => "лимит бесплатного тарифа",
                            "insufficient_quota" => "закончилась квота",
                            "invalid_api_key" => "ключ не принят",
                            "invalid_key" => "ключ не принят",
                            "region_blocked" => "регион заблокирован",
                            "balance_zero" => "нулевой баланс",
                            _ => text,
                        };
                    }
                }
            }
        }
        catch
        {
            // Not JSON or not shaped like an error: no reason available.
        }

        return string.Empty;
    }

    // ------------------------------------------------------------------ helpers

    private string BuildUrl(string path, string? query)
    {
        var settings = Settings;
        return $"{settings.ProxyBase.TrimEnd('/')}/{settings.ProxySecret}{path}{query}";
    }

    private void SetError(string message)
    {
        lock (_gate) { _lastError = message; }
    }

    private static string ErrorJson(string type, string message) =>
        JsonSerializer.Serialize(new { error = new { message, type, code = type } });

    private static Task WriteJsonAsync(HttpContext context, string json)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(json);
    }

    public string StatusJson()
    {
        var settings = Settings;
        var today = UsageStore.Today;

        var payload = new
        {
            running = Running,
            port = Port,
            usePool = settings.UsePool,
            autoFailover = settings.AutoFailover,
            keyStrategy = settings.KeyStrategy,
            cooldownMinutes = settings.CooldownMinutes,
            openCodeBaseUrl = settings.BaseUrl,
            activeKeyId = ActiveKeyId,
            requests = Requests,
            failovers = Failovers,
            promptTokens = PromptTokens,
            completionTokens = CompletionTokens,
            promptTokensToday = today.Prompt,
            completionTokensToday = today.Completion,
            usageResponsesToday = today.Responses,
            lastError = LastError,
            keys = settings.Keys.Select((k, index) =>
            {
                var stat = StatOf(k.Id);
                int calls = (stat?.Requests ?? 0) + (stat?.Failures ?? 0);
                return new
                {
                    id = k.Id,
                    label = k.Label,
                    masked = k.Masked,
                    order = index,
                    primary = index == 0,
                    enabled = k.Enabled,
                    invalid = k.Invalid,
                    available = k.Available,
                    blockedModels = k.BlockedModels,
                    exhaustedUntilUtc = k.ExhaustedUntilUtc,
                    lastStatus = k.LastStatus,
                    requests = stat?.Requests ?? 0,
                    failures = stat?.Failures ?? 0,
                    avgMs = calls > 0 ? (long)(stat!.TotalMs / calls) : 0,
                    lastMs = stat?.LastMs ?? 0,
                };
            }),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}
