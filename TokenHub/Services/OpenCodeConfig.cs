using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenHub.Services;

/// <summary>Reads and edits the OpenCode provider config (baseURL of the Token Harbor provider).</summary>
public static class OpenCodeConfig
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode", "opencode.json");

    public static bool Exists => File.Exists(FilePath);

    public static string? GetBaseUrl()
    {
        try
        {
            var node = LoadNode();
            return node?["provider"]?["tokenharbor"]?["options"]?["baseURL"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static string? GetApiKey()
    {
        try
        {
            var node = LoadNode();
            return node?["provider"]?["tokenharbor"]?["options"]?["apiKey"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Points the Token Harbor provider at the given baseURL. Returns the backup path.</summary>
    public static string SetBaseUrl(string baseUrl)
    {
        var node = LoadNode() ?? throw new InvalidOperationException("opencode.json не найден или повреждён");
        var options = node["provider"]?["tokenharbor"]?["options"]
                      ?? throw new InvalidOperationException("В конфиге нет провайдера tokenharbor");

        var backup = Backup();
        options["baseURL"] = baseUrl;
        File.WriteAllText(FilePath, node.ToJsonString(WriteOptions));
        return backup;
    }

    /// <summary>Writes both the address and the API key OpenCode should use.</summary>
    public static string SetProvider(string baseUrl, string? apiKey)
    {
        var node = LoadNode() ?? throw new InvalidOperationException("opencode.json не найден или повреждён");
        var options = node["provider"]?["tokenharbor"]?["options"]
                      ?? throw new InvalidOperationException("В конфиге нет провайдера tokenharbor");

        var backup = Backup();
        options["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options["apiKey"] = apiKey;
        }

        File.WriteAllText(FilePath, node.ToJsonString(WriteOptions));
        return backup;
    }

    public static string Backup()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(SettingsStore.Directory, $"opencode.json.backup-{stamp}");
        File.Copy(FilePath, target, overwrite: true);
        return target;
    }

    private static JsonNode? LoadNode()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(FilePath), nodeOptions: null, documentOptions: ParseOptions);
    }
}
