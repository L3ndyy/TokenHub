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
    private const string Prefix = "skills/";
    private const string Marker = "SKILL.md";

    private static readonly Lazy<IReadOnlyList<SkillInfo>> LazyCatalog = new(ReadCatalog);

    /// <summary>Where OpenCode looks for skills.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "opencode", "skills");

    public static IReadOnlyList<SkillInfo> Catalog => LazyCatalog.Value;

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
        File.Exists(Path.Combine(Folder, name, Marker));

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
        if (stream is null)
        {
            return 0;
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        string root = Path.GetFullPath(Folder);
        int copied = 0;

        foreach (string name in wanted)
        {
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
                    System.IO.Directory.CreateDirectory(target);
                    continue;
                }

                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                wrote = true;
            }

            if (wrote)
            {
                copied++;
            }
        }

        return copied;
    }

    private static Stream? OpenArchive() =>
        typeof(Skills).Assembly.GetManifestResourceStream(ResourceName);

    private static IReadOnlyList<SkillInfo> ReadCatalog()
    {
        var list = new List<SkillInfo>();

        try
        {
            using var stream = OpenArchive();
            if (stream is null)
            {
                return list;
            }

            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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

            foreach (string name in names)
            {
                list.Add(new SkillInfo(name, ReadDescription(archive, name)));
            }
        }
        catch
        {
            // A broken archive leaves the app usable with an empty list.
        }

        return list;
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
                    string text = line["description:".Length..].Trim().Trim('"', '\'');
                    return text.Length > 160 ? text[..157] + "..." : text;
                }
            }
        }
        catch
        {
            // A missing or odd SKILL.md only costs the tooltip.
        }

        return string.Empty;
    }
}