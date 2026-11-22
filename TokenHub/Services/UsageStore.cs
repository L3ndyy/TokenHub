using System.IO;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>
/// Token totals that survive a restart. The live counters in <see cref="LocalProxy"/>
/// vanish with the process; these are written to <c>usage.json</c> next to
/// <c>settings.json</c> and start over when the calendar day changes, so the window can
/// show "today" no matter how often the app or the service was restarted.
/// <para>
/// Only one process hosts the proxy at a time, so there is a single writer. The file is
/// small and is read-modified-written on every answer, which keeps a "reset" in the app
/// and a running service in agreement.
/// </para>
/// </summary>
public static class UsageStore
{
    private sealed class Totals
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
            Save(totals);
        }
    }

    /// <summary>Clears today's totals behind the "Сбросить" button on the stats card.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Save(new Totals { Date = TodayKey() });
        }
    }

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

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

        // Yesterday's numbers are not additive to today's: start clean.
        return fromDisk is not null && fromDisk.Date == today
            ? fromDisk
            : new Totals { Date = today };
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