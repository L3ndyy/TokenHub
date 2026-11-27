using System.IO;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>
/// Token totals that survive a restart. The live counters in <see cref="LocalProxy"/>
/// vanish with the process; these are written to <c>usage.json</c> next to
/// <c>settings.json</c> and start over when the calendar day changes, so the window can
/// show "today" no matter how often the app or the service was restarted.
/// <para>
/// A rolling history of the last <see cref="KeepDays"/> days is kept as well, so the
/// dashboard can draw "requests/tokens per day" and estimate savings without a server.
/// </para>
/// <para>
/// Only one process hosts the proxy at a time, so there is a single writer. The file is
/// small and is read-modified-written on every answer, which keeps a "reset" in the app
/// and a running service in agreement.
/// </para>
/// </summary>
public static class UsageStore
{
    private const int KeepDays = 60;

    private sealed class Totals
    {
        public string Date { get; set; } = string.Empty;
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public int Responses { get; set; }

        /// <summary>Daily totals, newest last. Missing in files written by older versions.</summary>
        public List<DayTotals>? History { get; set; }
    }

    /// <summary>One calendar day of usage, used by the daily chart.</summary>
    public sealed class DayTotals
    {
        public string Date { get; set; } = string.Empty;
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public int Responses { get; set; }
    }

    private static readonly object Gate = new();
    private static string _path = Path.Combine(SettingsStore.Directory, "usage.json");

    public static string FilePath => _path;

    /// <summary>Points the counter at the folder of the settings file actually in use.</summary>
    public static void Configure(string settingsPath)
    {
        lock (Gate)
        {
            string folder = Path.GetDirectoryName(settingsPath) ?? SettingsStore.Directory;
            _path = Path.Combine(string.IsNullOrEmpty(folder) ? SettingsStore.Directory : folder, "usage.json");
        }
    }

    public static (long Prompt, long Completion, int Responses) Today
    {
        get
        {
            lock (Gate)
            {
                var totals = Load();
                return (totals.PromptTokens, totals.CompletionTokens, totals.Responses);
            }
        }
    }

    /// <summary>
    /// The last <paramref name="days"/> calendar days, oldest first, with empty days
    /// filled in as zero so a chart has a stable x-axis.
    /// </summary>
    public static IReadOnlyList<(DateTime Day, long Prompt, long Completion, int Responses)> Recent(int days)
    {
        if (days < 1)
        {
            days = 1;
        }

        lock (Gate)
        {
            var totals = Load();
            var byDate = new Dictionary<string, DayTotals>(StringComparer.Ordinal);
            foreach (var day in totals.History ?? new List<DayTotals>())
            {
                if (!string.IsNullOrEmpty(day?.Date))
                {
                    byDate[day.Date] = day;
                }
            }

            // Today is the live counter, which is always the freshest value.
            byDate[totals.Date] = new DayTotals
            {
                Date = totals.Date,
                PromptTokens = totals.PromptTokens,
                CompletionTokens = totals.CompletionTokens,
                Responses = totals.Responses,
            };

            var list = new List<(DateTime, long, long, int)>(days);
            var start = DateTime.Today.AddDays(-(days - 1));
            for (int i = 0; i < days; i++)
            {
                var date = start.AddDays(i);
                string key = date.ToString("yyyy-MM-dd");
                if (byDate.TryGetValue(key, out var day))
                {
                    list.Add((date, day.PromptTokens, day.CompletionTokens, day.Responses));
                }
                else
                {
                    list.Add((date, 0, 0, 0));
                }
            }

            return list;
        }
    }

    public static void Add(long prompt, long completion)
    {
        if (prompt == 0 && completion == 0)
        {
            return;
        }

        lock (Gate)
        {
            var totals = Load();
            totals.PromptTokens += prompt;
            totals.CompletionTokens += completion;
            totals.Responses++;

            var day = FindOrAdd(totals, totals.Date);
            day.PromptTokens += prompt;
            day.CompletionTokens += completion;
            day.Responses++;

            Prune(totals);
            Save(totals);
        }
    }

    /// <summary>Clears today's totals and the daily history behind the "reset" button.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Save(new Totals { Date = TodayKey() });
        }
    }

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private static DayTotals FindOrAdd(Totals totals, string date)
    {
        totals.History ??= new List<DayTotals>();
        var day = totals.History.Find(d => d?.Date == date);
        if (day is null)
        {
            day = new DayTotals { Date = date };
            totals.History.Add(day);
        }

        return day;
    }

    private static void Prune(Totals totals)
    {
        if (totals.History is null || totals.History.Count <= KeepDays)
        {
            return;
        }

        totals.History.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        totals.History.RemoveRange(0, totals.History.Count - KeepDays);
    }

    private static Totals Load()
    {
        string today = TodayKey();
        Totals? fromDisk = null;

        try
        {
            if (File.Exists(_path))
            {
                fromDisk = JsonSerializer.Deserialize<Totals>(File.ReadAllText(_path));
            }
        }
        catch
        {
            // A half-written file must never stop the proxy from counting.
        }

        var history = fromDisk?.History ?? new List<DayTotals>();
        history.RemoveAll(d => d is null || string.IsNullOrEmpty(d.Date));

        // Yesterday's numbers are not additive to today's: start clean, but keep the
        // daily history so the week chart does not lose its older bars.
        if (fromDisk is null || fromDisk.Date != today)
        {
            return new Totals { Date = today, History = history };
        }

        fromDisk.History = history;

        // A file written before the history existed still has today's counters somewhere:
        // seed today's bar from them once, so nothing is lost on upgrade.
        if (findToday(history) is null && (fromDisk.PromptTokens > 0 || fromDisk.CompletionTokens > 0))
        {
            history.Add(new DayTotals
            {
                Date = today,
                PromptTokens = fromDisk.PromptTokens,
                CompletionTokens = fromDisk.CompletionTokens,
                Responses = fromDisk.Responses,
            });
        }

        return fromDisk;

        DayTotals? findToday(List<DayTotals> list) => list.Find(d => d?.Date == today);
    }

    private static void Save(Totals totals)
    {
        try
        {
            string folder = Path.GetDirectoryName(_path) ?? string.Empty;
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Write-then-rename, so a crash never leaves a truncated counter behind.
            string temp = _path + ".tmp";
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(totals, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // Counting is a nicety; never fail a request over it.
        }
    }
}