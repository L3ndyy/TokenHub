using System.IO;

namespace TokenHub.Services;

/// <summary>
/// Append-only activity log for the app (item T9). Writes to %APPDATA%\TokenHub\events.log,
/// rotates at 512 KB and never records a whole API key (item T12).
/// </summary>
internal static class EventLog
{
    private const long Limit = 512 * 1024;

    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(SettingsStore.Directory, "events.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > Limit)
                {
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static string Read()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
        }
        catch (Exception ex)
        {
            return "Не удалось прочитать журнал: " + ex.Message;
        }
    }

    public static void Clear()
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
        }
        catch
        {
            // Ignore.
        }
    }
}
