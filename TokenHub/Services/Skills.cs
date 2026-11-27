using System.IO;
using System.IO.Compression;
using System.Text;

namespace TokenHub.Services;

/// <summary>One skill folder shipped inside the app.</summary>
public sealed record SkillInfo(string Name, string Description);

/// <summary>A purpose-based bucket, so the long list stays readable.</summary>
public sealed record SkillGroup(string Name, string Description, string[] Keywords);

/// <summary>
/// The skill collection that ships inside TokenHub.exe. Installing copies the chosen
/// folders into <c>~/.config/opencode/skills</c>, where OpenCode picks them up on its
/// next start. The archive is an embedded resource, so a single-file build needs no
/// files next to the executable.
/// </summary>
public static class Skills
{
    private const string ResourceName = "skills.zip";
    private const string RuResourceName = "skills.ru.json";
    private const string Prefix = "skills/";
    private const string Marker = "SKILL.md";

    private static readonly object Gate = new();
    private static IReadOnlyList<SkillInfo>? _catalog;
    private static Dictionary<string, string>? _russian;
    private static HashSet<string>? _shipped;

    /// <summary>Where OpenCode looks for skills.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode", "skills");

    /// <summary>A second shared root the agent already reads; drops land here without a rebuild too.</summary>
    public static string AgentsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".agents", "skills");

    public static IReadOnlyList<SkillInfo> Catalog => _catalog ??= ReadCatalog();

    /// <summary>Forgets the cached list after a folder or zip was dropped in.</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _catalog = null;
        }
    }

    /// <summary>
    /// Groups by purpose, in the order shown in the app. The last group is the fallback
    /// and intentionally has no keywords.
    /// </summary>
    public static IReadOnlyList<SkillGroup> Groups { get; } = new List<SkillGroup>
    {
        new("Разработка и архитектура",
            "Языки, фреймворки, API и устройство проектов.",
            new[]
            {
                "architecture", "backend", "frontend", "pattern", "api-design", "database",
                "sql", "orm", "prisma", "postgres", "mysql", "mongo", "redis", "clickhouse",
                "django", "laravel", "rails", "spring", "quarkus", "dotnet", "csharp", "fsharp",
                "kotlin", "java", "python", "rust", "golang", "perl", "php", "react", "vue",
                "nuxt", "nextjs", "nestjs", "angular", "svelte", "flutter", "dart", "bun",
                "vite", "node", "tinystruct", "compose", "coroutines", "jpa", "exposed",
                "ktor", "hexagonal", "clean-architecture", "contract-first", "monorepo",
            }),
        new("Тестирование и качество",
            "Тесты, ревью и проверки перед выпуском.",
            new[]
            {
                "test", "tdd", "qa", "review", "audit", "verification", "regression",
                "lint", "code-quality", "codehealth", "debug", "e2e", "self-evaluation",
                "compliance", "santa-method",
            }),
        new("Интерфейс и дизайн",
            "Вёрстка, внешний вид, движение и доступность.",
            new[]
            {
                "design", "ux", "ui-kit", "accessibility", "a11y", "motion", "animation",
                "canvas", "slides", "visualize", "css", "tailwind", "make-interfaces",
                "frontend-design", "html", "presentation",
            }),
        new("Документы и отчёты",
            "Word, презентации, локализация и ведение документации.",
            new[]
            {
                "docx", "word", "academic", "pptx", "report", "docs", "i18n", "localization",
                "living-docs", "opd-word",
            }),
        new("Roblox и игры",
            "Движок Roblox: код, физика и интерфейсы.",
            new[] { "roblox", "luau", "physics", "game" }),
        new("Агенты и автоматизация",
            "Скиллы, правила, память и циклы для ИИ-агентов.",
            new[]
            {
                "agent", "automation", "loop", "hook", "skill", "memory", "prompt", "council",
                "subagent", "workflow", "mcp", "eval", "ponytail", "karpathy", "rules",
                "distill", "configure", "config", "install", "scout", "stocktake", "growth-log",
                "unified-memory", " ck", "continuous-learning", "budget", "plan-canvas",
            }),
        new("Инфраструктура и безопасность",
            "Сборка, выпуск, служебные программы и секреты.",
            new[]
            {
                "security", "deploy", "production", "ci-cd", "github-actions", "docker",
                "kubernetes", "git",
                "repo", "windows", "desktop", "installer", "cli", "update", "mole", "origin",
                "share", "new-repo",
            }),
        new("Данные и аналитика",
            "Запросы, миграции и разбор данных.",
            new[] { "data", "analytics", "migration", "sentry", "datadog", "databricks" }),
        new("Прочее", "Всё, что не попало в другие разделы.", Array.Empty<string>()),
    };

    /// <summary>Picks the best-matching group by keyword hits; ties keep the earlier group.</summary>
    public static SkillGroup GroupFor(SkillInfo skill)
    {
        string name = skill.Name.ToLowerInvariant();
        string haystack = name + " " + skill.Description.ToLowerInvariant();

        SkillGroup best = Groups[^1];
        int bestScore = 0;

        foreach (var group in Groups)
        {
            int score = 0;
            foreach (string keyword in group.Keywords)
            {
                if (name.Contains(keyword, StringComparison.Ordinal))
                {
                    score += 3;
                }
                else if (haystack.Contains(keyword, StringComparison.Ordinal))
                {
                    score += 1;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = group;
            }
        }

        return best;
    }

    public static bool IsInstalled(string name) =>
        File.Exists(Path.Combine(Folder, name, Marker))
        || File.Exists(Path.Combine(AgentsFolder, name, Marker));

    /// <summary>True when the skill comes from the archive inside the app (so it can be re-installed).</summary>
    public static bool IsShipped(string name)
    {
        _ = Catalog;
        return _shipped?.Contains(name) == true;
    }

    public static int InstalledCount => Catalog.Count(skill => IsInstalled(skill.Name));

    public static int InstallAll() => Install(Catalog.Select(skill => skill.Name));

    /// <summary>Copies the chosen skills and returns how many folders were written.</summary>
    public static int Install(IEnumerable<string> names)
    {
        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return 0;
        }

        using var stream = OpenArchive();
        using var archive = stream is null ? null : new ZipArchive(stream, ZipArchiveMode.Read);
        string root = Path.GetFullPath(Folder);
        int copied = 0;

        foreach (string name in wanted)
        {
            if (CopyFromArchive(archive, name, root))
            {
                copied++;
            }
        }

        return copied;
    }

    /// <summary>Unpacks one skill folder out of the shipped archive; false when it is not there.</summary>
    private static bool CopyFromArchive(ZipArchive? archive, string name, string root)
    {
        if (archive is null)
        {
            return false;
        }

        string skillRoot = Prefix + name + "/";
        bool wrote = false;

        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(skillRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = entry.FullName[skillRoot.Length..];
            if (relative.Length == 0)
            {
                continue;
            }

            string target = Path.GetFullPath(Path.Combine(root, name, relative));

            // Never let a crafted archive write outside the skills folder.
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            wrote = true;
        }

        return wrote;
    }

    /// <summary>Copies one skill folder the user dropped in; returns 1 when it was written.</summary>
    public static int ImportFolder(string path) => ImportFolder(path, Folder);

    /// <summary>Same as <see cref="ImportFolder(string)"/>, with an explicit target root (tests).</summary>
    public static int ImportFolder(string path, string destination)
    {
        string source = Path.GetFullPath(path);
        string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        if (name.Length == 0)
        {
            return 0;
        }

        // A folder holding several skills is accepted too: any child with SKILL.md counts.
        if (!File.Exists(Path.Combine(source, Marker)))
        {
            int nested = 0;
            foreach (string child in SafeFolders(source))
            {
                if (File.Exists(Path.Combine(child, Marker)))
                {
                    nested += ImportFolder(child, destination);
                }
            }

            return nested;
        }

        string target = Path.GetFullPath(Path.Combine(destination, name));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return CopyTree(source, target) ? 1 : 0;
    }

    /// <summary>
    /// Takes every skill folder inside a .zip, whatever its nesting, and copies it into
    /// the skills folder. A flat archive of SKILL.md files works as well as one with a
    /// wrapping <c>skills/</c> directory.
    /// </summary>
    public static int ImportZip(string path) => ImportZip(path, Folder);

    /// <summary>Same as <see cref="ImportZip(string)"/>, with an explicit target root (tests).</summary>
    public static int ImportZip(string path, string destination)
    {
        using var archive = ZipFile.OpenRead(path);
        string root = Path.GetFullPath(destination);

        // Group entries by the folder that holds their SKILL.md.
        var skills = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            string[] parts = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(parts[^1], Marker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string skillRoot = string.Join('/', parts[..^1]);
            if (!skills.TryGetValue(skillRoot, out var list))
            {
                skills[skillRoot] = list = new List<ZipArchiveEntry>();
            }

            list.Add(entry);
        }

        int copied = 0;
        foreach (string skillRoot in skills.Keys)
        {
            string name = Path.GetFileName(skillRoot);
            string target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(target);

                // The marker itself plus every sibling file in the archive is enough:
                // a skill is referenced by its SKILL.md, but scripts next to it matter.
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(skillRoot + "/", StringComparison.OrdinalIgnoreCase)
                        || entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }

                    string relative = entry.FullName[(skillRoot.Length + 1)..];
                    string file = Path.GetFullPath(Path.Combine(target, relative));
                    if (!file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    entry.ExtractToFile(file, overwrite: true);
                }

                copied++;
            }
            catch
            {
                // One broken archive entry must not stop the other skills.
            }
        }

        return copied;
    }

    /// <summary>Recursive copy that overwrites, used by the folder import.</summary>
    private static bool CopyTree(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            foreach (string folder in Directory.GetDirectories(source))
            {
                CopyTree(folder, Path.Combine(target, Path.GetFileName(folder)));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Stream? OpenArchive() =>
        typeof(Skills).Assembly.GetManifestResourceStream(ResourceName);

    private static IReadOnlyList<SkillInfo> ReadCatalog()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var shipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var stream = OpenArchive();
            if (stream is not null)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string rest = entry.FullName[Prefix.Length..];
                    int slash = rest.IndexOf('/');
                    if (slash > 0)
                    {
                        names.Add(rest[..slash]);
                    }
                }

                foreach (string name in names.ToList())
                {
                    shipped[name] = ReadDescription(archive, name);
                }
            }
        }
        catch
        {
            // A broken archive leaves the app usable with an empty list.
        }

        // Anything the user dropped into either skills root counts as part of the set,
        // so a new skill shows up without rebuilding TokenHub.
        _shipped = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        foreach (string root in new[] { Folder, AgentsFolder })
        {
            foreach (string folder in SafeFolders(root))
            {
                string name = Path.GetFileName(folder);
                if (File.Exists(Path.Combine(folder, Marker)))
                {
                    names.Add(name);
                }
            }
        }

        return names.Select(name => new SkillInfo(name, Describe(name, shipped))).ToList();
    }

    /// <summary>Russian text first, then the shipped front matter, then whatever is on disk.</summary>
    private static string Describe(string name, Dictionary<string, string> shipped)
    {
        if (Russian.TryGetValue(name, out string? translated) && translated.Length > 0)
        {
            return Capitalize(translated);
        }

        if (shipped.TryGetValue(name, out string? text) && text.Length > 0)
        {
            return Capitalize(text);
        }

        foreach (string root in new[] { Folder, AgentsFolder })
        {
            string file = Path.Combine(root, name, Marker);
            if (File.Exists(file))
            {
                return Capitalize(ReadFrontMatter(file));
            }
        }

        return string.Empty;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static IEnumerable<string> SafeFolders(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Name-to-Russian map that ships next to the archive; a missing file is fine.</summary>
    private static Dictionary<string, string> Russian
    {
        get
        {
            if (_russian is not null)
            {
                return _russian;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var stream = typeof(Skills).Assembly.GetManifestResourceStream(RuResourceName);
                if (stream is not null)
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        reader.ReadToEnd());
                    if (parsed is not null)
                    {
                        map = new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // English descriptions stay visible instead of an empty list.
            }

            return _russian = map;
        }
    }

    /// <summary>First line of the YAML front matter in SKILL.md, shortened for a tooltip.</summary>
    private static string ReadDescription(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry($"{Prefix}{name}/{Marker}");
        if (entry is null)
        {
            return string.Empty;
        }

        try
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            for (int i = 0; i < 40; i++)
            {
                string? line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    string text = line["description:".Length..].Trim().Trim('"', (char)39);
                    const int limit = 320;
                    if (text.Length > limit)
                    {
                        int cut = text.LastIndexOf(' ', limit);
                        text = (cut > 0 ? text[..cut] : text[..limit]).TrimEnd() + "…";
                    }

                    return text;
                }
            }
        }
        catch
        {
            // A missing or odd SKILL.md only costs the tooltip.
        }

        return string.Empty;
    }

    /// <summary>Pulls the <c>description:</c> line out of a SKILL.md reader.</summary>
    private static string ReadFrontMatter(TextReader reader)
    {
        for (int i = 0; i < 40; i++)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string text = line["description:".Length..].Trim().Trim('"', (char)39);

            // YAML folded blocks ("description: >") keep the text on the following
            // indented lines; without this a skill would read as a lone ">".
            if (text.Length == 0 || text == ">" || text == "|")
            {
                var parts = new List<string>();

                while (true)
                {
                    string? next = reader.ReadLine();
                    if (next is null)
                    {
                        break;
                    }

                    if (next.Length == 0)
                    {
                        continue;
                    }

                    if (!char.IsWhiteSpace(next[0]))
                    {
                        break;
                    }

                    parts.Add(next.Trim());
                    i++;
                }

                text = string.Join(' ', parts);
            }

            const int limit = 320;
            if (text.Length > limit)
            {
                int cut = text.LastIndexOf(' ', limit);
                text = (cut > 0 ? text[..cut] : text[..limit]).TrimEnd() + "...";
            }

            return text;
        }

        return string.Empty;
    }

    private static string ReadFrontMatter(string file)
    {
        try
        {
            using var reader = new StreamReader(file, Encoding.UTF8);
            return ReadFrontMatter(reader);
        }
        catch
        {
            return string.Empty;
        }
    }
}