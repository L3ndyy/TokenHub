using System.Net;
using System.Net.Sockets;
using System.Text;
using TokenHub.Services;

namespace TokenHub.Tests;

/// <summary>
/// Dependency-free test runner for the failover policy (item T7). It deliberately has
/// no NuGet packages so it builds and runs offline: <c>dotnet run --project TokenHub.Tests</c>.
/// Exit code 0 means every check passed.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        ClassifyTests();
        CooldownTests();
        RankTests();
        SortTests();
        ProbeTests();
        PreserveHealthTests();
        UsageScannerTests();
        UsageStoreTests();
        OpenCodeConfigTests();
        DpapiTests();
        SettingsRoundTripTests();
        LocalProxyTests();

        Console.WriteLine();
        Console.WriteLine($"Пройдено: {_passed}, провалено: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void ClassifyTests()
    {
        Check("401 — неверный ключ", KeyFault.Invalid,
            PoolPolicy.Classify(401, "{\"error\":{\"code\":\"invalid_api_key\"}}"));

        Check("402 balance_zero для модели — не исчерпан ключ", KeyFault.ModelBlocked,
            PoolPolicy.Classify(402,
                "{\"error\":{\"code\":\"balance_zero\",\"message\":\"Your plan does not cover this model\"}}"));

        Check("402 model-only — блокировка модели", KeyFault.ModelBlocked,
            PoolPolicy.Classify(402, "This plan does not cover this model"));

        Check("402 без модели — исчерпан", KeyFault.Exhausted,
            PoolPolicy.Classify(402, "{\"error\":{\"code\":\"balance_zero\"}}"));

        Check("429 — исчерпан", KeyFault.Exhausted,
            PoolPolicy.Classify(429, "Too many requests"));

        Check("region_blocked — переключение не поможет", KeyFault.None,
            PoolPolicy.Classify(403, "{\"error\":{\"code\":\"region_blocked\"}}"));

        Check("403 quota — исчерпан", KeyFault.Exhausted,
            PoolPolicy.Classify(403, "quota exceeded"));

        Check("403 обычный — не переключаем", KeyFault.None,
            PoolPolicy.Classify(403, "Forbidden"));
    }

    private static void CooldownTests()
    {
        CheckMinutes("Retry-After в секундах", 2,
            PoolPolicy.Cooldown("120", 30));

        CheckMinutes("Retry-After некорректен — берём настройку", 30,
            PoolPolicy.Cooldown("nonsense", 30));

        CheckMinutes("Нет заголовка — берём настройку", 15,
            PoolPolicy.Cooldown(null, 15));

        var future = DateTimeOffset.UtcNow.AddSeconds(90).ToString("R");
        double seconds = PoolPolicy.Cooldown(future, 30).TotalSeconds;
        Check("Retry-After как дата", seconds is > 60 and < 120, seconds);
    }

    private static void RankTests()
    {
        var a = new ApiKeyEntry { Id = "a", Label = "A" };
        var b = new ApiKeyEntry { Id = "b", Label = "B" };
        var c = new ApiKeyEntry { Id = "c", Label = "C" };
        var keys = new List<ApiKeyEntry> { a, b, c };

        var ordered = PoolPolicy.Rank(keys, "b", "ordered", _ => 0, 0);
        Check("ordered ставит активный ключ первым", ordered[0].Id == "b",
            string.Join(",", ordered.Select(k => k.Id)));

        var round = PoolPolicy.Rank(keys, "a", "roundRobin", _ => 0, 1);
        Check("roundRobin начинает со второй позиции", round[0].Id == "b",
            string.Join(",", round.Select(k => k.Id)));

        var usage = new Dictionary<string, int> { ["a"] = 10, ["b"] = 2, ["c"] = 5 };
        var least = PoolPolicy.Rank(keys, "a", "leastUsed", id => usage[id], 0);
        Check("leastUsed ставит реже использованный первым", least[0].Id == "b",
            string.Join(",", least.Select(k => k.Id)));
    }

    private static void SortTests()
    {
        var ok = new ApiKeyEntry { Id = "ok", Label = "Б" };
        var bad = new ApiKeyEntry { Id = "bad", Label = "А", Invalid = true };
        var paused = new ApiKeyEntry { Id = "paused", Label = "В", ExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(10) };

        var sorted = PoolPolicy.SortForDisplay(new[] { ok, bad, paused }, "status");
        Check("sort=status: ошибки сверху, потом пауза", sorted[0].Id == "bad" && sorted[1].Id == "paused",
            string.Join(",", sorted.Select(k => k.Id)));

        var byName = PoolPolicy.SortForDisplay(new[] { ok, bad }, "name");
        Check("sort=name: по названию", byName[0].Id == "bad",
            string.Join(",", byName.Select(k => k.Id)));
    }

    private static void ProbeTests()
    {
        var soon = new ApiKeyEntry { Id = "soon", Label = "Скоро", ExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(1) };
        var later = new ApiKeyEntry { Id = "later", Label = "Позже", ExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(20) };
        var flagged = new ApiKeyEntry { Id = "flagged", Label = "Сломан", Invalid = true };

        var order = PoolPolicy.ProbeOrder(new[] { later, flagged, soon });

        Check("проба: первым — кто раньше освободится", order[0].Id == "soon",
            string.Join(",", order.Select(k => k.Id)));
        Check("проба: недействительный ключ — в самом конце", order[^1].Id == "flagged",
            string.Join(",", order.Select(k => k.Id)));
    }

    private static void PreserveHealthTests()
    {
        var known = new ApiKeyEntry
        {
            Id = "k1",
            ExhaustedUntilUtc = DateTime.UtcNow.AddMinutes(20),
            LastStatus = "исчерпан (HTTP 429)",
        };
        known.BlockedModels.Add("gpt-x");

        // What the app writes: the same key, looking perfectly healthy.
        var fresh = new ApiKeyEntry { Id = "k1", LastStatus = "работает" };
        var added = new ApiKeyEntry { Id = "k2", LastStatus = "работает" };

        PoolPolicy.PreserveHealth(new[] { fresh, added }, new[] { known });

        Check("сохранение состояния: пауза не теряется при сохранении настроек",
            fresh.ExhaustedUntilUtc == known.ExhaustedUntilUtc
            && fresh.BlockedModels.Contains("gpt-x")
            && fresh.LastStatus == "исчерпан (HTTP 429)",
            $"{fresh.ExhaustedUntilUtc} / {string.Join(",", fresh.BlockedModels)} / {fresh.LastStatus}");
        Check("сохранение состояния: новый ключ остаётся как загружен",
            added.ExhaustedUntilUtc is null && added.LastStatus == "работает",
            added.LastStatus);
    }

    private static void DpapiTests()
    {
        const string secret = "thk_live_test_abcdefghijklmnop";
        string protectedValue = Dpapi.TryProtect(secret) ?? string.Empty;

        Check("T1: ключ шифруется префиксом enc:v1", protectedValue.StartsWith("enc:v1:", StringComparison.Ordinal),
            protectedValue[..Math.Min(12, protectedValue.Length)]);
        Check("T1: расшифровка возвращает исходный ключ", Dpapi.TryUnprotect(protectedValue) == secret,
            Dpapi.TryUnprotect(protectedValue));
        string masked = Dpapi.Mask(secret);
        Check("T1: маска скрывает середину ключа",
            masked.Length < secret.Length && masked.StartsWith("thk_live", StringComparison.Ordinal)
            && masked.EndsWith("mnop", StringComparison.Ordinal),
            $"{masked} (len {masked.Length}, ожидалось {secret.Length})");
        Check("T1: открытый текст проходит без изменений", Dpapi.TryUnprotect("plain") == "plain", "plain");
    }

    private static void SettingsRoundTripTests()
    {
        const string secret = "thk_live_roundtrip_abcdefghijklmnop";
        string path = Path.Combine(Path.GetTempPath(), $"th-test-{Guid.NewGuid():N}.json");

        try
        {
            var settings = new AppSettings();
            settings.Keys.Add(new ApiKeyEntry { Key = secret, Label = "Тест" });

            try
            {
                string direct = System.Text.Json.JsonSerializer.Serialize(
                    settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Check("T1: прямая сериализация не падает", direct.Contains("enc:v1:", StringComparison.Ordinal),
                    direct[..Math.Min(80, direct.Length)]);
            }
            catch (Exception ex)
            {
                Check("T1: прямая сериализация не падает", false, ex.ToString());
                return;
            }

            SettingsStore.SaveTo(path, settings);

            string text = File.ReadAllText(path);
            Check("T1: settings.json на диске содержит enc:v1", text.Contains("enc:v1:", StringComparison.Ordinal),
                text.Length + " байт");
            Check("T1: открытого ключа в файле нет", !text.Contains(secret, StringComparison.Ordinal), "ok");

            var back = SettingsStore.LoadFrom(path);
            Check("T1: ключ читается обратно", back.Keys.Count == 1 && back.Keys[0].Key == secret,
                back.Keys.Count == 1 ? back.Keys[0].Key : "нет ключей");
        }
        catch (Exception ex)
        {
            Check("T1: сохранение и чтение настроек", false, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Temp file is harmless.
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// OpenCode always streams, and Token Harbor reports tokens in the last SSE chunk.
    /// These checks pin that behaviour down, including chunks split mid-line.
    /// </summary>
    private static void UsageScannerTests()
    {
        long prompt = 0;
        long completion = 0;
        var scanner = new UsageScanner((p, c) => { prompt += p; completion += c; });

        scanner.Scan("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n");
        Check("счётчик токенов: обычные чанки без usage ничего не дают", prompt == 0 && completion == 0,
            $"вход {prompt}, выход {completion}");

        scanner.Scan("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":6,\"completion_tokens\":12}}\n\ndata: [DONE]\n\n");
        Check("счётчик токенов: usage из SSE считает вход и выход", prompt == 6 && completion == 12,
            $"вход {prompt}, выход {completion}");

        prompt = 0;
        completion = 0;
        scanner = new UsageScanner((p, c) => { prompt += p; completion += c; });
        scanner.Scan("data: {\"choices\":[],\"usa");
        scanner.Scan("ge\":{\"prompt_tokens\":100,");
        scanner.Scan(" \"completion_tokens\":250}}\n\n");
        Check("счётчик токенов: чанк, разрезанный посередине", prompt == 100 && completion == 250,
            $"вход {prompt}, выход {completion}");

        prompt = 0;
        completion = 0;
        scanner = new UsageScanner((p, c) => { prompt += p; completion += c; });
        scanner.ScanJson("{\"usage\":{\"input_tokens\":7,\"output_tokens\":9}}");
        Check("счётчик токенов: нестримовый JSON (input_tokens/output_tokens)",
            prompt == 7 && completion == 9, $"вход {prompt}, выход {completion}");

        prompt = 0;
        completion = 0;
        scanner = new UsageScanner((p, c) => { prompt += p; completion += c; });
        scanner.ScanJson("{\"id\":\"chatcmpl-1\",\"choices\":[]}");
        Check("счётчик токенов: ответ без usage не считается", prompt == 0 && completion == 0,
            $"вход {prompt}, выход {completion}");

        prompt = 0;
        completion = 0;
        scanner = new UsageScanner((p, c) => { prompt += p; completion += c; });
        scanner.Scan("data: {\"choices\":[],\"usage\":null}\n\n");
        Check("счётчик токенов: usage:null пропускается", prompt == 0 && completion == 0,
            $"вход {prompt}, выход {completion}");
    }

    /// <summary>
    /// "Today" must survive a restart but must not carry yesterday's numbers forward.
    /// </summary>
    private static void UsageStoreTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "th-usage-" + Guid.NewGuid().ToString("N"));

        try
        {
            UsageStore.Configure(Path.Combine(folder, "settings.json"));
            UsageStore.Reset();

            Check("токены за сегодня: пустой день", UsageStore.Today == (0L, 0L, 0),
                UsageStore.Today);

            UsageStore.Add(120, 30);
            UsageStore.Add(5, 7);

            Check("токены за сегодня: ответы складываются", UsageStore.Today == (125L, 37L, 2),
                UsageStore.Today);

            // A file left over from another day starts from zero instead of adding up.
            File.WriteAllText(
                UsageStore.FilePath,
                "{\"Date\":\"1999-01-01\",\"PromptTokens\":999,\"CompletionTokens\":999,\"Responses\":9}");
            Check("токены за сегодня: вчерашний день не прибавляется", UsageStore.Today == (0L, 0L, 0),
                UsageStore.Today);

            UsageStore.Reset();
            Check("токены за сегодня: сброс обнуляет", UsageStore.Today == (0L, 0L, 0),
                UsageStore.Today);
        }
        catch (Exception ex)
        {
            Check("токены за сегодня: файл счётчика", false, ex.Message);
        }
        finally
        {
            UsageStore.Configure(SettingsStore.FilePath);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp folder is harmless.
            }
        }
    }

    /// <summary>
    /// The first-run wizard must be able to create opencode.json from nothing, and a
    /// second run must not throw away providers the user already had.
    /// </summary>
    private static void OpenCodeConfigTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "th-oc-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "opencode.json");

        try
        {
            OpenCodeConfig.EnsureProvider("http://127.0.0.1:8477/v1", "thk_test", path);

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            string? url = node["provider"]?["tokenharbor"]?["options"]?["baseURL"]?.GetValue<string>();
            string? model = node["model"]?.GetValue<string>();
            int models = (node["provider"]?["tokenharbor"]?["models"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;

            Check("мастер: создаёт opencode.json с провайдером", url == "http://127.0.0.1:8477/v1", url);
            Check("мастер: прописывает модель и список моделей",
                model == "tokenharbor/deepseek-v4.1-flash:free" && models == 3, $"{model} / {models}");

            // Pretend the user already had another provider and an MCP server.
            node["mcp"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Roblox_Studio"] = new System.Text.Json.Nodes.JsonObject(),
            };
            node["provider"]!["ollama"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "Ollama" };
            File.WriteAllText(path, node.ToJsonString());

            OpenCodeConfig.EnsureProvider("http://127.0.0.1:8490/v1", "thk_other", path);

            var again = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            string? newUrl = again["provider"]?["tokenharbor"]?["options"]?["baseURL"]?.GetValue<string>();
            string? ollama = again["provider"]?["ollama"]?["name"]?.GetValue<string>();

            Check("мастер: второй запуск сохраняет чужие провайдеры и mcp",
                newUrl == "http://127.0.0.1:8490/v1" && ollama == "Ollama" && again["mcp"] is not null,
                $"{newUrl} / {ollama} / mcp={(again["mcp"] is not null)}");
        }
        catch (Exception ex)
        {
            Check("мастер: создание конфига OpenCode", false, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp folder is harmless.
            }
        }
    }

    private static void Check(string name, KeyFault expected, KeyFault actual) =>
        Check(name, expected == actual, $"{actual} (ожидалось {expected})");

    private static void CheckMinutes(string name, int minutes, TimeSpan actual)
    {
        var expected = TimeSpan.FromMinutes(minutes);
        Check(name, Math.Abs((actual - expected).TotalSeconds) < 1, $"{actual.TotalSeconds:0} c");
    }

    private static void Check(string name, bool ok, object? actual)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  ok   {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  FAIL {name} — получено: {actual}");
        }
    }

    // ---------------------------------------------------------------- local proxy (integration)

    /// <summary>
    /// Starts the real proxy against a hand-written stub upstream and checks the things
    /// that are easy to break: loopback/Host/Origin guard, the pool credential, and the
    /// free-only rule. No packages, no network.
    /// </summary>
    private static void LocalProxyTests()
    {
        string folder = Path.Combine(Path.GetTempPath(), "th-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var upstream = new StubUpstream();
        var proxy = LocalProxy.Instance;

        try
        {
            StatsStore.Configure(Path.Combine(folder, "settings.json"));
            EventLog.Configure(folder);

            int port = FreePort();
            var settings = new AppSettings
            {
                Upstream = $"http://127.0.0.1:{upstream.Port}",
                ProxyBase = string.Empty,
                ProxySecret = string.Empty,
                LocalPort = port,
                ProxyToken = "thk_pool_testtoken",
            };
            settings.Keys.Add(new ApiKeyEntry { Key = "thk_live_testkey", Label = "тест" });

            proxy.Configure(() => settings, _ => { });
            if (!proxy.StartAsync(port).GetAwaiter().GetResult())
            {
                Check("прокси: запускается на свободном порту", false, proxy.LastError);
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string url = $"http://127.0.0.1:{port}/v1/models";

            CheckHttp("прокси: принимает ключ пула", 200, Send(http, url, "thk_pool_testtoken"), proxy.LastError);
            CheckHttp("прокси: принимает ключ из настроек", 200, Send(http, url, "thk_live_testkey"));
            CheckHttp("прокси: отклоняет чужой ключ", 403, Send(http, url, "thk_live_wrong"));
            CheckHttp("прокси: отклоняет чужой Host", 403,
                Send(http, url, "thk_pool_testtoken", host: "evil.example"));
            CheckHttp("прокси: отклоняет чужой Origin", 403,
                Send(http, url, "thk_pool_testtoken", origin: "https://evil.example"));

            settings.FreeOnly = true;
            string chat = $"http://127.0.0.1:{port}/v1/chat/completions";
            CheckHttp("прокси: режим free-only блокирует платную модель", 400,
                Post(http, chat, "thk_pool_testtoken", "{\"model\":\"deepseek-v4.1-flash\"}"));
            CheckHttp("прокси: режим free-only пропускает :free", 200,
                Post(http, chat, "thk_pool_testtoken", "{\"model\":\"deepseek-v4.1-flash:free\"}"));
        }
        catch (Exception ex)
        {
            Check("прокси: интеграционные проверки", false, ex.Message);
        }
        finally
        {
            proxy.StopAsync().GetAwaiter().GetResult();
            upstream.Dispose();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Temp folder is harmless.
            }
        }
    }

    private static void CheckHttp(string name, int expected, int actual, string? detail = null) =>
        Check(name, expected == actual, $"HTTP {actual} (ожидалось {expected}) {detail}");

    private static int Send(HttpClient http, string url, string token, string? host = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        using var response = http.SendAsync(request).GetAwaiter().GetResult();
        return (int)response.StatusCode;
    }

    private static int Post(HttpClient http, string url, string token, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);

        using var response = http.SendAsync(request).GetAwaiter().GetResult();
        return (int)response.StatusCode;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Minimal HTTP/1.1 server that always answers 200 with a small JSON body.</summary>
    private sealed class StubUpstream : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public StubUpstream()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync();
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private static async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    var head = new StringBuilder();

                    while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read <= 0)
                        {
                            return;
                        }

                        head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                        if (head.Length > 64 * 1024)
                        {
                            return;
                        }
                    }

                    string text = head.ToString();
                    int split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
                    int contentLength = 0;
                    foreach (string line in text[..split].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength);
                        }
                    }

                    int alreadyRead = Encoding.ASCII.GetByteCount(text[split..]);
                    while (alreadyRead < contentLength)
                    {
                        int read = await stream.ReadAsync(buffer);
                        if (read <= 0)
                        {
                            break;
                        }

                        alreadyRead += read;
                    }

                    byte[] body = Encoding.UTF8.GetBytes("{\"object\":\"list\",\"data\":[]}");
                    string reply =
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
                    await stream.WriteAsync(body);
                    await stream.FlushAsync();
                }
                catch
                {
                    // The proxy may close the connection first; nothing to report.
                }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}