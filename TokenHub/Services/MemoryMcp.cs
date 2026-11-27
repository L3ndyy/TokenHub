using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TokenHub.Services;

/// <summary>
/// A minimal MCP server over stdio, built into TokenHub.exe and started by OpenCode as
/// <c>TokenHub.exe --mcp-memory</c>. It exposes the local <see cref="MemoryStore"/> as
/// tools, so the assistant can recall a project instead of re-reading it and can save a
/// lesson after solving something.
/// <para>
/// Hand-rolled JSON-RPC on purpose: no SDK, no extra process, no extra installer payload.
/// </para>
/// </summary>
public static class MemoryMcp
{
    private const string ProtocolVersion = "2024-11-05";

    public static int Run()
    {
        // The app is a WinExe, so there is no console window; pipes still work.
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        // OpenCode starts the server with the project folder as the working directory.
        string scope = SafeWorkingDirectory();
        MemoryStore.Configure(SettingsStore.FilePath);

        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string? response;
            try
            {
                response = Handle(line, scope);
            }
            catch (Exception ex)
            {
                response = Error(null, -32603, "Внутренняя ошибка: " + ex.Message);
            }

            if (response is not null)
            {
                output.WriteLine(response);
            }
        }

        return 0;
    }

    private static string? Handle(string line, string scope)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(line);
        }
        catch
        {
            return Error(null, -32700, "Некорректный JSON");
        }

        if (root is not JsonObject request)
        {
            return Error(null, -32600, "Ожидался JSON-RPC объект");
        }

        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? string.Empty;
        JsonNode? parameters = request["params"];

        // Notifications have no id and must not be answered.
        if (id is null && method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = parameters?["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion,
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "tokenhub-memory",
                        ["version"] = "1.0.0",
                    },
                });

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = Tools() });

            case "tools/call":
                return CallTool(id, parameters, scope);

            default:
                return id is null ? null : Error(id, -32601, "Метод не поддерживается: " + method);
        }
    }

    private static JsonArray Tools() => new()
    {
        Tool(
            "memory_search",
            "Долговременная память проекта. Вызови ПЕРЕД тем, как читать много файлов или задавать вопросы: "
            + "вернёт ранее сохранённые факты, решения и уроки по этому проекту. Экономит токены.",
            new JsonObject
            {
                ["query"] = Prop("string", "Что вспомнить: ключевые слова, имена файлов, тема."),
                ["limit"] = Prop("integer", "Сколько заметок вернуть (по умолчанию 8, максимум 30)."),
                ["scope"] = Prop("string", "Ограничить проектом (абсолютный путь). По умолчанию — текущая папка."),
            },
            "query"),

        Tool(
            "memory_save",
            "Сохрани в долговременную память факт, решение или урок, чтобы не выяснять это заново. "
            + "Пиши коротко и по делу: одна мысль — одна заметка.",
            new JsonObject
            {
                ["text"] = Prop("string", "Что запомнить. До 4000 символов."),
                ["kind"] = Prop("string", "note | fact | decision | lesson | snippet"),
                ["tags"] = Prop("array", "Массив коротких тегов, например [\"proxy\", \"build\"]."),
                ["scope"] = Prop("string", "Проект (абсолютный путь). По умолчанию — текущая папка."),
            },
            "text"),

        Tool(
            "memory_recent",
            "Последние заметки памяти — полезно в начале сессии, чтобы понять контекст проекта.",
            new JsonObject
            {
                ["limit"] = Prop("integer", "Сколько заметок вернуть (по умолчанию 10)."),
                ["scope"] = Prop("string", "Проект (абсолютный путь)."),
            }),

        Tool(
            "memory_forget",
            "Удали заметку по id, если она устарела или оказалась неверной.",
            new JsonObject
            {
                ["id"] = Prop("string", "id заметки, который вернули memory_search или memory_recent."),
            },
            "id"),

        Tool(
            "memory_stats",
            "Сколько заметок в памяти и по каким тегам — быстрый способ понять, что уже накоплено.",
            new JsonObject()),

        Tool(
            "skill_search",
            "Поиск по каталогу скиллов, которые умеет ставить TokenHub. Используй, чтобы найти готовую инструкцию под задачу, прежде чем писать свою.",
            new JsonObject
            {
                ["query"] = Prop("string", "Что ищем: тема, язык, инструмент."),
                ["limit"] = Prop("integer", "Сколько скиллов вернуть (по умолчанию 10, максимум 40)."),
            },
            "query"),
    };

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
        }

        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static string? CallTool(JsonNode? id, JsonNode? parameters, string scope)
    {
        string name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
        JsonNode? args = parameters?["arguments"];

        try
        {
            string text = name switch
            {
                "memory_search" => SearchText(args, scope),
                "memory_save" => SaveText(args, scope),
                "memory_recent" => RecentText(args, scope),
                "memory_forget" => ForgetText(args),
                "memory_stats" => StatsText(),
                "skill_search" => SkillSearchText(args),
                _ => throw new InvalidOperationException("Неизвестный инструмент: " + name),
            };

            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text },
                },
                ["isError"] = false,
            });
        }
        catch (Exception ex)
        {
            return Result(id, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = "Ошибка: " + ex.Message },
                },
                ["isError"] = true,
            });
        }
    }

    private static string SearchText(JsonNode? args, string scope)
    {
        string query = args?["query"]?.GetValue<string>() ?? string.Empty;
        int limit = Math.Clamp(Int(args, "limit", 8), 1, 30);
        string wanted = StringArg(args, "scope", scope);

        var found = MemoryStore.Search(query, limit * 3, wanted);
        if (found.Count == 0 && !string.IsNullOrEmpty(wanted))
        {
            // A note saved before the project folder was known is still useful.
            found = MemoryStore.Search(query, limit * 3, string.Empty);
        }

        var scoped = found
            .Where(e => string.IsNullOrEmpty(e.Scope) || string.Equals(e.Scope, wanted, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        if (scoped.Count == 0)
        {
            return $"По запросу «{query}» в памяти ничего нет. Это нормально: сохраняй важное через memory_save.";
        }

        return "Найдено в памяти (id · дата · тип · теги):\n\n"
               + string.Join("\n\n", scoped.Select(Format));
    }

    private static string SaveText(JsonNode? args, string scope)
    {
        string text = args?["text"]?.GetValue<string>() ?? string.Empty;
        string kind = StringArg(args, "kind", "note");
        string wanted = StringArg(args, "scope", scope);
        var tags = new List<string>();

        if (args?["tags"] is JsonArray array)
        {
            foreach (var tag in array)
            {
                if (tag?.GetValue<string>() is { Length: > 0 } value)
                {
                    tags.Add(value);
                }
            }
        }

        var entry = MemoryStore.Add(text, kind, wanted, tags);
        return $"Сохранено: {entry.Id} · {entry.Kind} · {entry.At}"
               + (entry.Tags.Count > 0 ? " · #" + string.Join(" #", entry.Tags) : string.Empty);
    }

    private static string RecentText(JsonNode? args, string scope)
    {
        int limit = Math.Clamp(Int(args, "limit", 10), 1, 50);
        string wanted = StringArg(args, "scope", scope);

        var all = MemoryStore.All()
            .Where(e => string.IsNullOrEmpty(wanted)
                        || string.IsNullOrEmpty(e.Scope)
                        || string.Equals(e.Scope, wanted, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        return all.Count == 0
            ? "Память пока пуста."
            : "Последние заметки:\n\n" + string.Join("\n\n", all.Select(Format));
    }

    private static string ForgetText(JsonNode? args)
    {
        string id = StringArg(args, "id", string.Empty);
        return MemoryStore.Remove(id)
            ? "Заметка " + id + " удалена."
            : "Заметка с id " + id + " не найдена.";
    }

    private static string StatsText()
    {
        var tags = MemoryStore.Tags();
        string tagLine = tags.Count == 0
            ? "тегов пока нет"
            : string.Join(", ", tags.Take(12).Select(t => $"#{t.Tag} ({t.Count})"));

        return $"Заметок: {MemoryStore.Count}, за сегодня: {MemoryStore.TodayCount()}. {tagLine}";
    }

    /// <summary>The catalogue the app can install, so the assistant proposes an existing skill instead of reinventing it.</summary>
    private static string SkillSearchText(JsonNode? args)
    {
        string query = StringArg(args, "query", string.Empty).Trim();
        int limit = Math.Clamp(Int(args, "limit", 10), 1, 40);

        var catalog = Skills.Catalog;
        IEnumerable<SkillInfo> matches = catalog;

        if (query.Length > 0)
        {
            matches = catalog.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var found = matches.Take(limit).ToList();
        if (found.Count == 0)
        {
            return $"Скиллов по запросу «{query}» нет. Всего в наборе: {catalog.Count}.";
        }

        var lines = found.Select(s =>
            $"- {s.Name}{(Skills.IsInstalled(s.Name) ? " (установлен)" : string.Empty)} — {s.Description}");

        return $"Скиллы по запросу «{query}» ({found.Count} из {catalog.Count}):\n" + string.Join("\n", lines);
    }

    private static string Format(MemoryStore.Entry entry)
    {
        string text = entry.Text.Length > 900 ? entry.Text[..900] + "…" : entry.Text;
        string tags = entry.Tags.Count == 0 ? string.Empty : " · #" + string.Join(" #", entry.Tags);
        return $"[{entry.Id}] {entry.At} · {entry.Kind}{tags}\n{text}";
    }

    private static string StringArg(JsonNode? args, string name, string fallback) =>
        args?[name] is JsonValue value && value.TryGetValue<string>(out string? text)
        && !string.IsNullOrWhiteSpace(text)
            ? text
            : fallback;

    private static int Int(JsonNode? args, string name, int fallback)
    {
        if (args?[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out int number))
        {
            return number;
        }

        return value.TryGetValue<string>(out string? text) && int.TryParse(text, out number)
            ? number
            : fallback;
    }

    private static string SafeWorkingDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Result(JsonNode? id, JsonNode result) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? JsonValue.Create((string?)null),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    }.ToJsonString();
}