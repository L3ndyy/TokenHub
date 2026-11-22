using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenHub.Services;

/// <summary>
/// Reads and edits the OpenCode provider config (baseURL of the Token Harbor provider).
/// It can also create the whole file: a freshly installed OpenCode has no
/// <c>~/.config/opencode</c> at all, and the first-run wizard fills that in.
/// </summary>
public static class OpenCodeConfig
{
    private const string DefaultModel = "tokenharbor/deepseek-v4.1-flash:free";

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

    /// <summary>True when OpenCode has never been pointed at Token Harbor, or has no config at all.</summary>
    public static bool NeedsSetup => GetBaseUrl() is null;

    public static string? GetBaseUrl()
    {
        try
        {
            var node = LoadNode(FilePath);
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
            var node = LoadNode(FilePath);
            return node?["provider"]?["tokenharbor"]?["options"]?["apiKey"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Points the Token Harbor provider at the given baseURL. Returns the backup path.</summary>
    public static string? SetBaseUrl(string baseUrl)
    {
        var node = LoadNode(FilePath);
        if (node?["provider"]?["tokenharbor"]?["options"] is not JsonObject options)
        {
            // No provider block yet: build the whole thing instead of failing.
            return EnsureProvider(baseUrl, GetApiKey());
        }

        var backup = Backup();
        options["baseURL"] = baseUrl;
        File.WriteAllText(FilePath, node!.ToJsonString(WriteOptions));
        return backup;
    }

    /// <summary>Writes both the address and the API key OpenCode should use.</summary>
    public static string? SetProvider(string baseUrl, string? apiKey)
    {
        var node = LoadNode(FilePath);
        if (node?["provider"]?["tokenharbor"]?["options"] is not JsonObject options)
        {
            return EnsureProvider(baseUrl, apiKey);
        }

        var backup = Backup();
        options["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options["apiKey"] = apiKey;
        }

        File.WriteAllText(FilePath, node!.ToJsonString(WriteOptions));
        return backup;
    }

    /// <summary>
    /// Creates or repairs the Token Harbor provider block, leaving every other provider
    /// untouched. Used by the first-run wizard, where the folder may not exist yet.
    /// Returns the backup path when an existing config was backed up.
    /// </summary>
    public static string? EnsureProvider(string baseUrl, string? apiKey, string? path = null)
    {
        string target = path ?? FilePath;
        string? backup = null;

        JsonNode? node = null;
        if (File.Exists(target))
        {
            // Backups belong to the real config; an explicit path (tests) keeps its own.
            backup = path is null ? Backup() : null;
            try
            {
                node = LoadNode(target);
            }
            catch
            {
                // Unreadable JSON: the backup above is the way back, so start clean.
            }
        }
        else
        {
            string? folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
        }

        node ??= new JsonObject();
        node["$schema"] ??= "https://opencode.ai/config.json";

        var provider = node["provider"] as JsonObject ?? new JsonObject();
        node["provider"] = provider;

        var tokenHarbor = provider["tokenharbor"] as JsonObject ?? new JsonObject();
        provider["tokenharbor"] = tokenHarbor;

        tokenHarbor["npm"] ??= "@ai-sdk/openai-compatible";
        tokenHarbor["name"] ??= "Token Harbor";

        var options = tokenHarbor["options"] as JsonObject ?? new JsonObject();
        tokenHarbor["options"] = options;
        options["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options["apiKey"] = apiKey;
        }

        // OpenCode needs the model list spelled out for an OpenAI-compatible provider.
        if (tokenHarbor["models"] is not JsonObject)
        {
            tokenHarbor["models"] = DefaultModels();
        }

        node["model"] ??= DefaultModel;

        File.WriteAllText(target, node.ToJsonString(WriteOptions));
        return backup;
    }

    private static JsonObject DefaultModels() => new()
    {
        ["deepseek-v4.1-flash:free"] = new JsonObject { ["name"] = "DeepSeek V4.1 Flash (free)" },
        ["deepseek-v4-flash:free"] = new JsonObject { ["name"] = "DeepSeek V4 Flash (free)" },
        ["deepseek-v4.1-flash"] = new JsonObject { ["name"] = "DeepSeek V4.1 Flash (paid)" },
    };

    public static string Backup()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(SettingsStore.Directory, $"opencode.json.backup-{stamp}");
        File.Copy(FilePath, target, overwrite: true);
        return target;
    }

    private static JsonNode? LoadNode(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path), nodeOptions: null, documentOptions: ParseOptions);
    }
}