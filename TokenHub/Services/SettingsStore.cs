using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TokenHub.Services;

public sealed class ApiKeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Encrypted with DPAPI on disk, plain text only in memory (item T1).</summary>
    [JsonConverter(typeof(ProtectedStringConverter))]
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Set when Token Harbor answered "invalid_api_key". Cleared by a successful check.</summary>
    public bool Invalid { get; set; }

    /// <summary>
    /// Models this key cannot serve because the account has no plan for them. This is
    /// not the same as an exhausted key: free models keep working, so the key is not
    /// put on cooldown for it.
    /// </summary>
    public List<string> BlockedModels { get; set; } = new();

    /// <summary>Failed checks stay quiet until this moment, then the key is tried again.</summary>
    public DateTime? ExhaustedUntilUtc { get; set; }

    public string LastStatus { get; set; } = "не проверен";

    public DateTime? LastCheckUtc { get; set; }

    [JsonIgnore]
    public string Masked
    {
        get
        {
            if (Key.Length <= 16)
            {
                return Key;
            }

            return $"{Key[..12]}...{Key[^4..]}";
        }
    }

    [JsonIgnore]
    public bool Available => Enabled && !Invalid && !(ExhaustedUntilUtc > DateTime.UtcNow);

    /// <summary>True when the key may be used for the given model.</summary>
    public bool Allows(string? model) =>
        string.IsNullOrEmpty(model) || !BlockedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "system";
    public string Accent { get; set; } = "system";
    public bool Autostart { get; set; }

    /// <summary>
    /// Optional region-unlock proxy in front of Token Harbor. It is a private
    /// deployment, so the public build ships placeholders only: the real address and
    /// secret live in %APPDATA%\TokenHub\settings.json on the machine that owns them.
    /// Leave both empty to talk to <see cref="Upstream"/> directly.
    /// </summary>
    public string ProxyBase { get; set; } = "https://example.invalid/api/proxy";
    public string ProxySecret { get; set; } = "replace-with-your-own-proxy-secret";
    public string Upstream { get; set; } = "https://tokenharbor.ai";

    /// <summary>
    /// Keys are tried top-down; the first one is the main key. An exhausted key is
    /// skipped until its cooldown ends, a model-blocked key only for that model.
    /// </summary>
    public List<ApiKeyEntry> Keys { get; set; } = new();

    /// <summary>When true OpenCode talks to the local pool proxy instead of the Vercel one.</summary>
    public bool UsePool { get; set; }

    public bool AutoFailover { get; set; } = true;

    public int CooldownMinutes { get; set; } = 30;

    /// <summary>How the pool picks the next key: ordered (top-down), roundRobin or leastUsed.</summary>
    public string KeyStrategy { get; set; } = "ordered";

    /// <summary>Start hidden in the tray (item T14).</summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Set once the first-run wizard is finished or skipped, so it stops opening on
    /// every start. The button on the dashboard can always open it again.
    /// </summary>
    public bool FirstRunDone { get; set; }

    /// <summary>
    /// Set once the short tour of the app has been shown or skipped.
    /// </summary>
    public bool IntroSeen { get; set; }

    /// <summary>
    /// Credential written into opencode.json instead of a real key. The proxy accepts
    /// it and swaps in a working key, so the user's real keys never sit in a plain
    /// file that other tools can read.
    /// </summary>
    public string ProxyToken { get; set; } = string.Empty;

    /// <summary>Only allow <c>*:free</c> models through the pool.</summary>
    public bool FreeOnly { get; set; }

    /// <summary>Model written into opencode.json as the default.</summary>
    public string DefaultModel { get; set; } = "tokenharbor/deepseek-v4.1-flash:free";

    /// <summary>Creates the pool credential once, so OpenCode and the proxy agree on it.</summary>
    public void EnsureProxyToken()
    {
        if (string.IsNullOrWhiteSpace(ProxyToken))
        {
            ProxyToken = "thk_pool_" + Guid.NewGuid().ToString("N")[..24];
        }
    }

    /// <summary>Sort used when drawing the key list (item V6). Empty/“manual” keeps the pool order.</summary>
    public string KeysSort { get; set; } = "manual";

    // Remembered window geometry (item V8). Null means "not saved yet" — a NaN default
    // would make System.Text.Json throw and silently break every settings save.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1040;
    public double WindowHeight { get; set; } = 660;
    public bool WindowMaximized { get; set; }

    public int LocalPort { get; set; } = 8477;

    /// <summary>
    /// Set by the app when the user clears key pauses. The service compares it so a
    /// pause cleared in the UI is not restored from the pool's memory on the next read.
    /// </summary>
    public DateTime? PoolResetUtc { get; set; }

    [JsonIgnore]
    public string DirectBaseUrl => $"{ProxyBase.TrimEnd('/')}/{ProxySecret}/v1";

    [JsonIgnore]
    public string LocalBaseUrl => $"http://127.0.0.1:{LocalPort}/v1";

    /// <summary>The address OpenCode should use right now.</summary>
    [JsonIgnore]
    public string BaseUrl => UsePool ? LocalBaseUrl : DirectBaseUrl;

    [JsonIgnore]
    public ApiKeyEntry? FirstAvailableKey =>
        Keys.FirstOrDefault(k => k.Available) ?? Keys.FirstOrDefault(k => k.Enabled);
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static readonly object Gate = new();

    public static string Directory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TokenHub");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load() => LoadFrom(FilePath);

    public static void Save(AppSettings settings) => SaveTo(FilePath, settings);

    /// <summary>Set when the last load had to fall back to a backup or failed outright.</summary>
    public static string? LastLoadError { get; private set; }

    /// <summary>Set when the last save failed, so the UI can say so instead of losing keys silently.</summary>
    public static string? LastSaveError { get; private set; }

    /// <summary>Reads a settings file from any path; also used by the Windows service.</summary>
    public static AppSettings LoadFrom(string path)
    {
        LastLoadError = null;

        if (!File.Exists(path) && !File.Exists(path + ".bak"))
        {
            return new AppSettings();
        }

        // The live file first, then the copy written just before the last save. A
        // truncated settings.json used to mean "no keys at all", which is the one
        // failure a key manager must never have.
        foreach (string candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate));
                if (loaded is null)
                {
                    continue;
                }

                loaded.Keys ??= new List<ApiKeyEntry>();
                foreach (var key in loaded.Keys)
                {
                    key.BlockedModels ??= new List<string>();
                }

                if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
                {
                    LastLoadError = "Основной файл настроек повреждён — загружена резервная копия.";
                    EventLog.Write(LastLoadError);
                }

                return loaded;
            }
            catch (Exception ex)
            {
                LastLoadError = "Не удалось прочитать настройки: " + ex.Message;
                EventLog.Write($"{candidate}: {ex.Message}");
            }
        }

        return new AppSettings();
    }

    /// <summary>Writes a settings file to any path; also used by the Windows service.</summary>
    public static void SaveTo(string path, AppSettings settings)
    {
        LastSaveError = null;

        try
        {
            lock (Gate)
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    System.IO.Directory.CreateDirectory(folder);
                }

                string text = JsonSerializer.Serialize(settings, Options);
                string temp = path + ".tmp";
                File.WriteAllText(temp, text);

                if (File.Exists(path))
                {
                    // Atomic on NTFS and keeps the previous good file as .bak.
                    File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
        }
        catch (Exception ex)
        {
            // Settings are a convenience, not critical state — but a silent failure
            // hides real bugs, so record it in the event log (item T9/T12).
            LastSaveError = ex.Message;
            EventLog.Write("Не удалось сохранить настройки: " + ex.Message);
        }
    }
}
