using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenHub.Services;

/// <summary>
/// Builds a support bundle (item T8): logs, environment and a settings copy with every
/// secret masked, so it can be shared without leaking keys (item T12).
/// </summary>
internal static class Diagnostics
{
    public static string Export(string targetPath, AppSettings settings)
    {
        using var stream = File.Create(targetPath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddText(zip, "info.txt", EnvironmentReport(settings));

        if (File.Exists(SettingsStore.FilePath))
        {
            AddText(zip, "settings.redacted.json", Redact(File.ReadAllText(SettingsStore.FilePath)));
        }

        if (File.Exists(EventLog.FilePath))
        {
            byte[] log = File.ReadAllBytes(EventLog.FilePath);
            AddBytes(zip, "events.log", log);
        }

        try
        {
            string? url = OpenCodeConfig.GetBaseUrl();
            AddText(zip, "opencode-provider.txt",
                $"baseURL: {url ?? "(не задан)"}{Environment.NewLine}apiKey: {Dpapi.Mask(OpenCodeConfig.GetApiKey())}");
        }
        catch
        {
            // The config may not exist; the bundle is still useful.
        }

        return targetPath;
    }

    private static string EnvironmentReport(AppSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TokenHub — диагностический отчёт");
        sb.AppendLine($"Время:        {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Версия:       {Assembly.GetExecutingAssembly().GetName().Version}");
        sb.AppendLine($"ОС:           {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
        sb.AppendLine($"Платформа:    {Environment.Version}");
        sb.AppendLine();
        sb.AppendLine($"Режим пула:   {(settings.UsePool ? "локальный пул" : "прокси напрямую")}");
        sb.AppendLine($"Стратегия:    {settings.KeyStrategy}");
        sb.AppendLine($"Кулдаун:      {settings.CooldownMinutes} мин");
        sb.AppendLine($"Автоперекл.:  {settings.AutoFailover}");
        sb.AppendLine($"Порт:         {settings.LocalPort}");
        sb.AppendLine($"Ключей:       {settings.Keys.Count}");
        sb.AppendLine($"Служба:       {PoolService.Describe()}");
        sb.AppendLine($"Прокси:       {(LocalProxy.Instance.Running ? "работает" : "остановлен")}");
        return sb.ToString();
    }

    /// <summary>Replaces every secret value with a mask before it leaves the machine.</summary>
    private static string Redact(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject root)
            {
                if (root["ProxySecret"] is JsonValue secret)
                {
                    root["ProxySecret"] = Dpapi.Mask(secret.GetValue<string>());
                }

                if (root["Keys"] is JsonArray keys)
                {
                    foreach (var item in keys)
                    {
                        if (item is JsonObject entry && entry["Key"] is JsonValue key)
                        {
                            entry["Key"] = Dpapi.Mask(Dpapi.TryUnprotect(key.GetValue<string>()));
                        }
                    }
                }
            }

            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch
        {
            return "{ \"error\": \"не удалось разобрать settings.json\" }";
        }
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddBytes(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var outStream = entry.Open();
        outStream.Write(content, 0, content.Length);
    }
}
