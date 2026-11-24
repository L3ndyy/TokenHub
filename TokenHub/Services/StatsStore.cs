using System.IO;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>
/// Per-key counters (requests, failures, latency) that survive a restart of the app
/// or the Windows service. They are written to <c>stats.json</c> next to
/// <c>settings.json</c>; losing them costs a graph, never a key, so every failure
/// here is ignored.
/// </summary>
public static class StatsStore
{
    public sealed class Entry
    {
        public int Requests { get; set; }
        public int Failures { get; set; }
        public long TotalMs { get; set; }
        public long LastMs { get; set; }
        public DateTime? LastUtc { get; set; }
    }

    private static readonly object Gate = new();
    private static string _path = Path.Combine(SettingsStore.Directory, "stats.json");

    public static string FilePath => _path;

    /// <summary>Points the store at the folder of the settings file actually in use.</summary>
    public static void Configure(string settingsPath)
    {
        lock (Gate)
        {
            string folder = Path.GetDirectoryName(settingsPath) ?? SettingsStore.Directory;
            _path = Path.Combine(string.IsNullOrEmpty(folder) ? SettingsStore.Directory : folder, "stats.json");
        }
    }

    public static Dictionary<string, Entry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new Dictionary<string, Entry>(StringComparer.Ordinal);
                }

                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                       ?? new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
            catch
            {
                return new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
        }
    }

    public static void Save(IReadOnlyDictionary<string, Entry> stats)
    {
        lock (Gate)
        {
            try
            {
                string folder = Path.GetDirectoryName(_path) ?? string.Empty;
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                string temp = _path + ".tmp";
                File.WriteAllText(
                    temp,
                    JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // A graph is not worth failing a request over.
            }
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch
            {
                // Nothing to clean up.
            }
        }
    }
}
