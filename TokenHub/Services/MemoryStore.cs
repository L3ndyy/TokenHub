using System.IO;
using System.Text;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>
/// A small local knowledge base for the assistant: facts, decisions and lessons that
/// survive between sessions. Stored as JSONL next to <c>settings.json</c> so the app and
/// the MCP server (which OpenCode spawns) read the same file.
/// <para>
/// Deliberately dependency-free: no database, no server, no network. The token-saving
/// trick is that the model can ask for "what do I already know about X" instead of
/// re-reading a project from scratch.
/// </para>
/// </summary>
public static class MemoryStore
{
    public const int MaxTextLength = 4000;

    /// <summary>One remembered item. Kept flat so a line stays readable in a text editor.</summary>
    public sealed class Entry
    {
        public string Id { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
        public string Kind { get; set; } = "note";
        public string Scope { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static string _path = Path.Combine(SettingsStore.Directory, "memory.jsonl");

    public static string FilePath => _path;

    public static void Configure(string settingsPath)
    {
        lock (Gate)
        {
            string folder = Path.GetDirectoryName(settingsPath) ?? SettingsStore.Directory;
            _path = Path.Combine(
                string.IsNullOrEmpty(folder) ? SettingsStore.Directory : folder,
                "memory.jsonl");
        }
    }

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<Entry> All()
    {
        lock (Gate)
        {
            return Read().OrderByDescending(e => e.At, StringComparer.Ordinal).ToList();
        }
    }

    public static int Count => All().Count;

    public static int TodayCount()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        return All().Count(e => e.At.StartsWith(today, StringComparison.Ordinal));
    }

    public static Entry Add(string text, string kind = "note", string scope = "", IEnumerable<string>? tags = null)
    {
        string clean = Clean(text);
        if (clean.Length == 0)
        {
            throw new ArgumentException("Пустая заметка", nameof(text));
        }

        string tagLine = string.Join(", ", (tags ?? Array.Empty<string>())
            .Select(t => t.Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12));

        lock (Gate)
        {
            var all = Read();

            // The same fact saved twice is noise, not knowledge: keep the newer timestamp.
            var existing = all.FirstOrDefault(e =>
                string.Equals(e.Text, clean, StringComparison.Ordinal)
                && string.Equals(e.Scope ?? string.Empty, scope ?? string.Empty, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.At = NowKey();
                Write(all);
                return existing;
            }

            var entry = new Entry
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                At = NowKey(),
                Kind = NormalizeKind(kind),
                Scope = scope ?? string.Empty,
                Text = clean,
                Tags = tagLine.Length == 0
                    ? new List<string>()
                    : tagLine.Split(',', StringSplitOptions.TrimEntries).ToList(),
            };

            all.Add(entry);
            Write(all);
            return entry;
        }
    }

    public static bool Remove(string id)
    {
        lock (Gate)
        {
            var all = Read();
            int removed = all.RemoveAll(e => string.Equals(e.Id, id, StringComparison.Ordinal));
            if (removed > 0)
            {
                Write(all);
            }

            return removed > 0;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Write(new List<Entry>());
        }
    }

    /// <summary>
    /// Substring search over text and tags. Every query word must appear somewhere, and
    /// results are ordered by match count and recency. No embeddings, no index — the
    /// corpus is small by design.
    /// </summary>
    public static IReadOnlyList<Entry> Search(string query, int limit = 12, string scope = "")
    {
        var words = (query ?? string.Empty)
            .Split(new[] { ' ', '\t', '\n', ',', '.', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length > 1)
            .Distinct()
            .ToArray();

        var all = All().Where(e =>
            string.IsNullOrEmpty(scope)
            || string.IsNullOrEmpty(e.Scope)
            || string.Equals(e.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();

        if (words.Length == 0)
        {
            return all.Take(Math.Max(1, limit)).ToList();
        }

        var scored = new List<(Entry Entry, int Score)>();
        foreach (var entry in all)
        {
            string haystack = (entry.Text + " " + string.Join(' ', entry.Tags) + " " + entry.Kind)
                .ToLowerInvariant();
            int score = 0;
            bool allWords = true;

            foreach (string word in words)
            {
                int hits = CountOccurrences(haystack, word);
                if (hits == 0)
                {
                    allWords = false;
                }

                score += hits;
            }

            if (!allWords)
            {
                continue;
            }

            // A recent note is more likely to be the one the model means.
            if (DateTime.TryParse(entry.At, out var at))
            {
                score += Math.Max(0, 30 - (DateTime.Now - at).Days);
            }

            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Entry.At, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(pair => pair.Entry)
            .ToList();
    }

    /// <summary>All tags with counts, most used first — cheap navigation for the UI.</summary>
    public static IReadOnlyList<(string Tag, int Count)> Tags()
    {
        return All()
            .SelectMany(e => e.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key, Count: g.Count()))
            .OrderByDescending(pair => pair.Count)
            .ThenBy(pair => pair.Tag, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static int CountOccurrences(string text, string word)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += word.Length;
        }

        return count;
    }

    private static string NormalizeKind(string? kind)
    {
        string value = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "lesson" or "урок" => "lesson",
            "decision" or "решение" => "decision",
            "fact" or "факт" => "fact",
            "snippet" or "код" => "snippet",
            _ => "note",
        };
    }

    private static string Clean(string? text)
    {
        string clean = (text ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (clean.Length > MaxTextLength)
        {
            clean = clean[..MaxTextLength].TrimEnd() + "…";
        }

        return clean;
    }

    private static string NowKey() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static List<Entry> Read()
    {
        var list = new List<Entry>();
        if (!File.Exists(_path))
        {
            return list;
        }

        try
        {
            foreach (string line in File.ReadLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(line);
                    if (entry is not null && !string.IsNullOrWhiteSpace(entry.Text))
                    {
                        entry.Id ??= string.Empty;
                        entry.Tags ??= new List<string>();
                        list.Add(entry);
                    }
                }
                catch
                {
                    // A half-written line must not hide the rest of the memory.
                }
            }
        }
        catch
        {
            // Unreadable file: behave like an empty memory, never crash the UI.
        }

        return list;
    }

    private static void Write(List<Entry> entries)
    {
        try
        {
            string folder = Path.GetDirectoryName(_path) ?? string.Empty;
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // A lock file keeps the app and the MCP server from interleaving writes.
            string lockPath = _path + ".lock";
            using var fileLock = AcquireLock(lockPath);

            var sb = new StringBuilder();
            foreach (var entry in entries.OrderBy(e => e.At, StringComparer.Ordinal))
            {
                sb.AppendLine(JsonSerializer.Serialize(entry));
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Memory is a convenience; never fail a request over it.
        }
    }

    private static FileStream? AcquireLock(string lockPath)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }

        return null;
    }
}