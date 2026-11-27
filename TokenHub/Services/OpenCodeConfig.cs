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

    /// <summary>True when opencode.json already points at the built-in memory server.</summary>
    public static bool HasMemory()
    {
        try
        {
            return LoadNode(FilePath)?["mcp"]?["tokenhub-memory"] is JsonObject;
        }
        catch
        {
            return false;
        }
    }

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
    public static string? SetProvider(string baseUrl, string? apiKey, string? model = null)
    {
        var node = LoadNode(FilePath);
        if (node?["provider"]?["tokenharbor"]?["options"] is not JsonObject options)
        {
            return EnsureProvider(baseUrl, apiKey, null, model);
        }

        var backup = Backup();
        options["baseURL"] = baseUrl;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options["apiKey"] = apiKey;
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            node!["model"] = model;
        }

        File.WriteAllText(FilePath, node!.ToJsonString(WriteOptions));
        return backup;
    }

    /// <summary>
    /// Creates or repairs the Token Harbor provider block, leaving every other provider
    /// untouched. Used by the first-run wizard, where the folder may not exist yet.
    /// Returns the backup path when an existing config was backed up.
    /// </summary>
    public static string? EnsureProvider(string baseUrl, string? apiKey, string? path = null, string? model = null)
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

        if (string.IsNullOrWhiteSpace(model))
        {
            node["model"] ??= DefaultModel;
        }
        else
        {
            node["model"] = model;
        }

        File.WriteAllText(target, node.ToJsonString(WriteOptions));
        return backup;
    }

    private static JsonObject DefaultModels() => new()
    {
        ["deepseek-v4.1-flash:free"] = new JsonObject { ["name"] = "DeepSeek V4.1 Flash (free)" },
        ["deepseek-v4-flash:free"] = new JsonObject { ["name"] = "DeepSeek V4 Flash (free)" },
        ["deepseek-v4.1-flash"] = new JsonObject { ["name"] = "DeepSeek V4.1 Flash (paid)" },
    };

    /// <summary>Model ids the provider block advertises, without the provider prefix.</summary>
    public static IReadOnlyList<string> KnownModels() =>
        DefaultModels().Select(pair => pair.Key).ToList();

    /// <summary>
    /// Registers the built-in memory MCP server and the short usage guide in opencode.json.
    /// Returns true when the file already advertised exactly this command.
    /// </summary>
    public static bool EnsureMemory(string exePath, string guidePath)
    {
        if (!File.Exists(FilePath) || string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        JsonNode? node;
        try
        {
            node = LoadNode(FilePath);
        }
        catch
        {
            return false;
        }

        if (node is not JsonObject root)
        {
            return false;
        }

        var mcp = root["mcp"] as JsonObject ?? new JsonObject();
        root["mcp"] = mcp;

        string? current = (root["mcp"]?["tokenhub-memory"]?["command"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        bool sameCommand = string.Equals(current, exePath, StringComparison.OrdinalIgnoreCase)
                           && (root["mcp"]?["tokenhub-memory"]?["command"] as JsonArray)?.Count == 2;

        var instructions = root["instructions"] as JsonArray ?? new JsonArray();
        root["instructions"] = instructions;

        bool hasGuide = !string.IsNullOrWhiteSpace(guidePath)
                        && instructions.Any(n => string.Equals(n?.GetValue<string>(), guidePath, StringComparison.OrdinalIgnoreCase));

        if (sameCommand && hasGuide)
        {
            return true;
        }

        mcp["tokenhub-memory"] = new JsonObject
        {
            ["type"] = "local",
            ["command"] = new JsonArray(exePath, "--mcp-memory"),
            ["enabled"] = true,
        };

        if (!string.IsNullOrWhiteSpace(guidePath) && !hasGuide)
        {
            WriteGuide(guidePath);
            instructions.Add(guidePath);
        }

        Backup();
        File.WriteAllText(FilePath, root.ToJsonString(WriteOptions));
        return false;
    }

    private static void WriteGuide(string path)
    {
        const string guide =
            "# TokenHub Memory\n\n" +
            "У тебя есть долговременная память проекта через MCP-сервер `tokenhub-memory`.\n\n" +
            "- Нужен контекст, история решения или имена файлов — вызови `memory_search` до чтения кода.\n" +
            "- Решил задачу, разобрался в причине бага или выбрал подход — сохрани короткий урок через `memory_save` с `kind=\"lesson\"`.\n" +
            "- Факты о проекте сохраняй с `kind=\"fact\"`, решения — с `kind=\"decision\"`.\n" +
            "- Не сохраняй секреты, ключи и содержимое `.env`.\n" +
            "- Одна заметка — одна мысль; не дублируй уже сохранённое.\n";

        try
        {
            string folder = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(path, guide);
        }
        catch
        {
            // A missing guide only makes the memory less discoverable, not broken.
        }
    }

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