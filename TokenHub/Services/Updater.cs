using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace TokenHub.Services;

/// <summary>A published release that carries an installer.</summary>
public sealed record UpdateInfo(string Version, string Notes, string DownloadUrl, long SizeBytes);

/// <summary>
/// Looks up the newest published build on GitHub Releases and, when the user asks,
/// downloads its installer and starts it. Nothing is replaced without that click.
/// </summary>
public static class Updater
{
    public const string Repository = "L3ndyy/TokenHub";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TokenHub");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Version of the running build, e.g. <c>1.0.0</c>.</summary>
    public static string CurrentVersion { get; } = ReadCurrentVersion();

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<commit>" when a source revision is known.
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Reads the latest release. <c>Ok</c> means the check itself worked; <c>Newer</c>
    /// is true only when that release is a newer version with an installer attached.
    /// </summary>
    public static async Task<(bool Ok, bool Newer, UpdateInfo? Info, string Message)> CheckAsync(
        CancellationToken token = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{Repository}/releases/latest";
            using var response = await Http.GetAsync(url, token);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return (false, false, null, "Релизы ещё не опубликованы.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return (false, false, null, $"GitHub ответил HTTP {(int)response.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            string version = tag.TrimStart('v', 'V');
            string notes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";

            string? download = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    download = asset.TryGetProperty("browser_download_url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out long bytes)
                        ? bytes
                        : 0;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(download))
            {
                return (true, false, null, $"Версия {version} опубликована без установщика.");
            }

            bool newer = Compare(version, CurrentVersion) > 0;
            var info = new UpdateInfo(version, notes, download, size);
            string message = newer
                ? $"Доступна версия {version}."
                : $"Установлена последняя версия ({CurrentVersion}).";

            return (true, newer, newer ? info : null, message);
        }
        catch (Exception ex)
        {
            return (false, false, null, "Не удалось проверить обновления: " + ex.Message);
        }
    }

    /// <summary>Downloads the installer into %TEMP% and starts it, returning its path.</summary>
    public static async Task<string> DownloadAndRunAsync(UpdateInfo info, CancellationToken token = default)
    {
        string path = Path.Combine(Path.GetTempPath(), $"TokenHub-Setup-{info.Version}.exe");

        using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, token);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return path;
    }

    private static int Compare(string left, string right)
    {
        return System.Version.TryParse(Pad(left), out var a) && System.Version.TryParse(Pad(right), out var b)
            ? a.CompareTo(b)
            : string.CompareOrdinal(left, right);

        static string Pad(string value) => value.Split('.', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [var major] => $"{major}.0.0",
            [var major, var minor] => $"{major}.{minor}.0",
            [var major, var minor, var patch, ..] => $"{major}.{minor}.{patch}",
            _ => "0.0.0",
        };
    }
}
