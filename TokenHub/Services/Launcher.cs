using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TokenHub.Services;

/// <summary>Everything the buttons actually do.</summary>
public static class Launcher
{
    private static readonly string LocalPrograms = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");

    public static string? OpenCodeExe
    {
        get
        {
            var exe = Path.Combine(LocalPrograms, "ai.opencode.desktop", "OpenCode.exe");
            if (File.Exists(exe))
            {
                return exe;
            }

            var cli = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ai.opencode.desktop", "cli", "2.3.0-x64", "opencode-cli.exe");
            return File.Exists(cli) ? cli : null;
        }
    }

    public static int OpenCodeRunningCount()
    {
        int count = 0;
        try
        {
            count += Process.GetProcessesByName("OpenCode").Length;
        }
        catch
        {
            // Ignore.
        }

        return count;
    }

    public static void StartOpenCode()
    {
        var exe = OpenCodeExe ?? throw new FileNotFoundException("OpenCode не найден");
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        });
    }

    /// <summary>Kills every OpenCode window and starts it again so a new config is picked up.</summary>
    public static void RestartOpenCode()
    {
        foreach (var name in new[] { "OpenCode", "opencode-cli", "opencode-pty" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(4000);
                }
                catch
                {
                    // Already gone.
                }
            }
        }

        Thread.Sleep(1500);
        StartOpenCode();
    }

    public static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public static void RevealFile(string path)
    {
        if (!File.Exists(path))
        {
            OpenFolder(Path.GetDirectoryName(path) ?? path);
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"" });
    }

    public static void OpenWithDefaultApp(string path)
    {
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    /// <summary>Opens an https:// address in the user's browser.</summary>
    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    public static void OpenTerminal(string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);

        // Windows Terminal is the Windows 11 default; fall back to PowerShell.
        foreach (var candidate in new[] { "wt.exe", "powershell.exe" })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = candidate == "wt.exe" ? $"-d \"{workingDirectory}\"" : "-NoExit",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                });
                return;
            }
            catch
            {
                // Try the next shell.
            }
        }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TokenHub";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string value && value.Length > 0;
    }

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                key.SetValue(RunValue, $"\"{exe}\" --minimized");
            }
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
