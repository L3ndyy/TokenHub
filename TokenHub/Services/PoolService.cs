using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TokenHub.Services;

/// <summary>
/// Installs and drives the optional Windows service that hosts the key pool, so the
/// pool keeps working with TokenHub closed (item chosen by the user).
/// Installation and control need administrator rights, so they go through a
/// temporary batch file started with <c>runas</c> — a UAC prompt appears once.
/// </summary>
internal static class PoolService
{
    public const string Name = "TokenHubPool";

    private const string Folder = @"C:\ProgramData\TokenHub";
    private const string TargetExe = @"C:\ProgramData\TokenHub\TokenHub.PoolService.exe";
    private const string SourceFile = @"C:\ProgramData\TokenHub\source.txt";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;

    private const uint StateStopped = 1;
    private const uint StateStartPending = 2;
    private const uint StateStopPending = 3;
    private const uint StateRunning = 4;

    private const int ErrorServiceDoesNotExist = 1060;

    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>Folder next to the app with the service binaries.</summary>
    public static string Payload =>
        Path.Combine(AppContext.BaseDirectory, "poolservice");

    public static bool PayloadReady =>
        File.Exists(Path.Combine(Payload, "TokenHub.PoolService.exe"));

    public enum State
    {
        NotInstalled,
        Stopped,
        Running,
        Pending,
        Unknown,
    }

    public static State Current
    {
        get
        {
            IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
            if (manager == IntPtr.Zero)
            {
                return State.Unknown;
            }

            try
            {
                IntPtr service = OpenService(manager, Name, ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist
                        ? State.NotInstalled
                        : State.Unknown;
                }

                try
                {
                    var status = new ServiceStatus();
                    if (!QueryServiceStatus(service, ref status))
                    {
                        return State.Unknown;
                    }

                    return status.CurrentState switch
                    {
                        StateStopped => State.Stopped,
                        StateRunning => State.Running,
                        StateStartPending or StateStopPending => State.Pending,
                        _ => State.Unknown,
                    };
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }
    }

    public static string Describe() => Current switch
    {
        State.Running => "работает",
        State.Stopped => "остановлена",
        State.NotInstalled => "не установлена",
        State.Pending => "перезапускается",
        _ => "состояние неизвестно",
    };

    /// <summary>Registers the service and starts it. Needs elevation.</summary>
    public static string Install()
    {
        if (!PayloadReady)
        {
            return $"Файлы службы не найдены в {Payload}. Соберите проект TokenHub.PoolService.";
        }

        string settings = SettingsStore.FilePath;

        // The service reads its settings path from source.txt. cmd.exe would write that
        // file in the console code page, so instead the app writes a UTF-8 pointer and
        // the elevated batch just copies it byte for byte.
        string pointer = Path.Combine(Path.GetTempPath(), $"tokenhub-source-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(pointer, settings, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            return "Не удалось подготовить команду: " + ex.Message;
        }

        try
        {
            return RunElevated(new[]
            {
                $"if not exist \"{Folder}\" mkdir \"{Folder}\"",
                $"xcopy /y /i /q \"{Payload}\\*\" \"{Folder}\\\"",
                $"copy /y \"{pointer}\" \"{SourceFile}\"",
                $"sc.exe create {Name} binPath= \"{TargetExe}\" start= auto DisplayName= \"TokenHub Pool Proxy\"",
                $"sc.exe description {Name} \"Pool of Token Harbor API keys with automatic failover.\"",
                $"sc.exe start {Name}",
            });
        }
        finally
        {
            TryDelete(pointer);
        }
    }

    public static string Uninstall() => RunElevated(new[]
    {
        $"sc.exe stop {Name}",
        $"sc.exe delete {Name}",
    });

    public static string StartService() => RunElevated(new[] { $"sc.exe start {Name}" });

    public static string StopService() => RunElevated(new[] { $"sc.exe stop {Name}" });

    /// <summary>True when the installed service binaries differ from the ones shipped with the app (item T4).</summary>
    public static bool PayloadOutdated
    {
        get
        {
            try
            {
                string source = Path.Combine(Payload, "TokenHub.PoolService.exe");
                if (!File.Exists(source) || !File.Exists(TargetExe))
                {
                    return false;
                }

                var installed = new FileInfo(TargetExe);
                var shipped = new FileInfo(source);
                return installed.Length != shipped.Length
                       || installed.LastWriteTimeUtc != shipped.LastWriteTimeUtc;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Copies the current binaries over the installed service and restarts it (item T4).</summary>
    public static string Update()
    {
        if (!PayloadReady)
        {
            return $"Файлы службы не найдены в {Payload}.";
        }

        return RunElevated(new[]
        {
            $"sc.exe stop {Name}",
            "ping -n 3 127.0.0.1 >nul",
            $"xcopy /y /i /q \"{Payload}\\*\" \"{Folder}\\\"",
            $"sc.exe start {Name}",
        });
    }

    /// <summary>Path recorded at install time; the service reads its keys from there.</summary>
    public static string RecordedSettingsPath
    {
        get
        {
            try
            {
                if (File.Exists(SourceFile))
                {
                    return File.ReadAllText(SourceFile).Trim();
                }
            }
            catch
            {
                // Reported as unknown below.
            }

            return "(не записан)";
        }
    }

    private static string RunElevated(IEnumerable<string> commands)
    {
        string stamp = Guid.NewGuid().ToString("N");
        string script = Path.Combine(Path.GetTempPath(), $"tokenhub-{stamp}.cmd");
        string log = Path.Combine(Path.GetTempPath(), $"tokenhub-{stamp}.log");

        var text = new StringBuilder();
        text.AppendLine("@echo off");
        text.AppendLine($"set LOG={log}");
        text.AppendLine($"> \"%LOG%\" 2>&1 echo TokenHub: управление службой {Name}");

        foreach (string command in commands)
        {
            text.AppendLine($"{command} >> \"%LOG%\" 2>&1");
        }

        text.AppendLine(">> \"%LOG%\" 2>&1 echo EXIT=%ERRORLEVEL%");

        try
        {
            // cmd.exe reads a batch file in the console OEM code page, so Cyrillic
            // paths (the app can live under a Russian-named folder) survive only when
            // the script uses that same code page. ASCII would corrupt them.
            File.WriteAllText(script, text.ToString(), OemEncoding());
        }
        catch (Exception ex)
        {
            return "Не удалось подготовить команду: " + ex.Message;
        }

        try
        {
            lock (Gate)
            {
                var info = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden,
                };

                using var process = Process.Start(info);
                if (process is null)
                {
                    return "Не удалось запустить cmd.exe с правами администратора.";
                }

                process.WaitForExit(120_000);
            }
        }
        catch (Exception ex)
        {
            return "Права администратора не выданы: " + ex.Message;
        }
        finally
        {
            TryDelete(script);
        }

        string output = "нет вывода";
        try
        {
            if (File.Exists(log))
            {
                output = File.ReadAllText(log, OemEncoding()).Trim();
            }
        }
        catch
        {
            // Keep the fallback text.
        }

        TryDelete(log);
        return output;
    }

    /// <summary>Code page cmd.exe uses for batch files on this machine (e.g. 866).</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.Default;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temp file; the OS will clear it eventually.
        }
    }
}
