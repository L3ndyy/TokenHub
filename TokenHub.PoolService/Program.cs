using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TokenHub.Services;

namespace TokenHub.PoolService;

/// <summary>
/// Hosts the shared <see cref="LocalProxy"/> as a Windows service, so the key pool
/// keeps answering while the TokenHub window is closed. The service never writes
/// settings.json — the desktop app owns the file — but it keeps the loaded copy in
/// memory and only re-reads it when the app changes the file. Re-loading a fresh
/// object on every access (as it used to) discarded every cooldown, "invalid" flag and
/// blocked-model note the moment it was recorded, so the pool had no memory.
/// </summary>
internal static class Program
{
    private const string ServiceName = "TokenHubPool";

    private const int ServiceWin32OwnProcess = 0x00000010;
    private const int ServiceAcceptStop = 0x00000001;
    private const int ServiceAcceptShutdown = 0x00000004;

    private const int ControlStop = 0x00000001;
    private const int ControlInterrogate = 0x00000004;
    private const int ControlShutdown = 0x00000005;

    private const int StateStopped = 0x00000001;
    private const int StateStartPending = 0x00000002;
    private const int StateStopPending = 0x00000003;
    private const int StateRunning = 0x00000004;

    private const int NoError = 0;
    private const int ErrorFailedServiceControllerConnect = 1063;

    private const string ProgramDataFolder = @"C:\ProgramData\TokenHub";

    private delegate void ServiceMainCallback(int argc, IntPtr argv);

    private delegate int ServiceControlHandler(int control, int eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceName;
        [MarshalAs(UnmanagedType.FunctionPtr)] public ServiceMainCallback? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType;
        public int CurrentState;
        public int ControlsAccepted;
        public int Win32ExitCode;
        public int ServiceSpecificExitCode;
        public int CheckPoint;
        public int WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher(ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, ServiceControlHandler handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    private static readonly ManualResetEventSlim Stopping = new(false);
    private static readonly ManualResetEventSlim Ready = new(false);

    // Held in static fields so the GC cannot collect the callbacks while the SCM uses them.
    private static ServiceMainCallback? _serviceMain;
    private static ServiceControlHandler? _controlHandler;

    private static IntPtr _statusHandle = IntPtr.Zero;
    private static int _lastState = StateStopped;
    private static bool _listening;
    private static string _settingsPath = string.Empty;
    private static string _logPath = string.Empty;

    private static readonly object _settingsGate = new();
    private static AppSettings? _cachedSettings;
    private static DateTime _cachedStampUtc = DateTime.MinValue;
    private static long _cachedSize = -1;
    private static DateTime _resetStampUtc = DateTime.MinValue;

    private static int Main(string[] args)
    {
        _settingsPath = ResolveSettingsPath(args);
        _logPath = ResolveLogPath();

        _serviceMain = ServiceMain;
        _controlHandler = OnControl;

        var table = new[]
        {
            new ServiceTableEntry { ServiceName = ServiceName, ServiceMain = _serviceMain },
            new ServiceTableEntry { ServiceName = null, ServiceMain = null },
        };

        if (StartServiceCtrlDispatcher(table))
        {
            return 0;
        }

        int error = Marshal.GetLastWin32Error();
        if (error == ErrorFailedServiceControllerConnect)
        {
            // Started by hand: run like a console app so it can be tested.
            Log("Запуск вне службы (консольный режим)");
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }

        Log($"StartServiceCtrlDispatcher не сработал, код {error}");
        return error;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _controlHandler!, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log("RegisterServiceCtrlHandlerEx не сработал");
            return;
        }

        Report(StateStartPending);
        Log($"Служба стартует, настройки: {_settingsPath}");

        var run = Task.Run(RunAsync);

        if (!Ready.Wait(TimeSpan.FromSeconds(20)) || !_listening)
        {
            Log("Прокси не поднялся — служба останавливается");
            Stopping.Set();
            Report(StateStopPending);
            run.GetAwaiter().GetResult();
            Report(StateStopped);
            return;
        }

        Report(StateRunning);
        Log("Служба работает");

        run.GetAwaiter().GetResult();

        Report(StateStopped);
        Log("Служба остановлена");
    }

    private static async Task RunAsync()
    {
        try
        {
            LocalProxy.Instance.Configure(
                LoadSettingsCached,
                // The service never writes: the desktop app owns settings.json. Learned
                // state (cooldowns, blocked models) lives in the cached copy instead.
                static _ => { });

            LocalProxy.Instance.Notice += (title, text) => Log($"{title}: {text}");

            int port = SettingsStore.LoadFrom(_settingsPath).LocalPort;
            if (port is <= 1024 or >= 65000)
            {
                port = LocalProxy.DefaultPort;
            }

            if (!await LocalProxy.Instance.StartAsync(port))
            {
                Log("Прокси не запустился: " + LocalProxy.Instance.LastError);
                Ready.Set();
                return;
            }

            _listening = true;
            Ready.Set();
            Log($"Прокси слушает http://127.0.0.1:{LocalProxy.Instance.Port}/v1");

            // Item T11: expose the same status over a named pipe for the desktop app.
            using var pipeCts = new CancellationTokenSource();
            var pipe = Task.Run(() => Pipe.Serve(() => LocalProxy.Instance.StatusJson(), pipeCts.Token));

            Stopping.Wait();

            pipeCts.Cancel();
            try
            {
                pipe.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The pipe thread is a nicety; never block shutdown on it.
            }

            await LocalProxy.Instance.StopAsync();
            Log("Прокси остановлен");
        }
        catch (Exception ex)
        {
            Log("Ошибка: " + ex);
            Ready.Set();
        }
    }

    private static int OnControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StateStopPending);
                Stopping.Set();
                break;

            case ControlInterrogate:
                Report(_lastState < StateRunning ? StateRunning : _lastState);
                break;

            default:
                // Unknown control codes must be acknowledged without an error.
                break;
        }

        return NoError;
    }

    private static void Report(int state)
    {
        if (_statusHandle == IntPtr.Zero)
        {
            return;
        }

        _lastState = state;

        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == StateRunning ? ServiceAcceptStop | ServiceAcceptShutdown : 0,
            CheckPoint = state is StateStartPending or StateStopPending ? 1 : 0,
            WaitHint = state is StateStartPending or StateStopPending ? 15000 : 0,
        };

        SetServiceStatus(_statusHandle, ref status);
    }

    /// <summary>
    /// Settings path: explicit argument, else the pointer file written at install
    /// time, else the machine-wide default.
    /// </summary>
    private static string ResolveSettingsPath(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] is "--settings" or "-s")
            {
                return args[i + 1];
            }
        }

        try
        {
            string pointer = Path.Combine(ProgramDataFolder, "source.txt");
            if (File.Exists(pointer))
            {
                string recorded = File.ReadAllText(pointer).Trim();
                if (recorded.Length > 0)
                {
                    return recorded;
                }
            }
        }
        catch
        {
            // Fall through to the default below.
        }

        return Path.Combine(ProgramDataFolder, "settings.json");
    }

    private static string ResolveLogPath()
    {
        // Console mode may run without administrative rights, so log to temp there.
        bool elevated = Path.Exists(ProgramDataFolder) && CanWrite(ProgramDataFolder);
        return elevated
            ? Path.Combine(ProgramDataFolder, "service.log")
            : Path.Combine(Path.GetTempPath(), "TokenHub.PoolService.log");
    }

    /// <summary>
    /// Loads settings once and reuses the same object until settings.json actually
    /// changes. Key cooldowns and "invalid" flags are recorded on that object, so they
    /// must survive across requests; a fresh deserialization per access discarded them.
    /// When the app does rewrite the file, the learned health is carried over so an
    /// unrelated save (window position, a key rename) cannot un-pause a key mid-cooldown.
    /// </summary>
    private static AppSettings LoadSettingsCached()
    {
        DateTime stampUtc = DateTime.MinValue;
        long size = -1;

        try
        {
            var info = new FileInfo(_settingsPath);
            if (info.Exists)
            {
                stampUtc = info.LastWriteTimeUtc;
                size = info.Length;
            }
        }
        catch
        {
            // An unreadable stamp simply forces a reload below.
        }

        lock (_settingsGate)
        {
            if (_cachedSettings is not null && stampUtc == _cachedStampUtc && size == _cachedSize)
            {
                return _cachedSettings;
            }

            var fresh = SettingsStore.LoadFrom(_settingsPath);

            // A user-requested reset wins over everything the pool has learned.
            bool reset = false;
            if (fresh.PoolResetUtc is { } resetStamp && resetStamp > _resetStampUtc)
            {
                _resetStampUtc = resetStamp;
                reset = true;
            }

            if (_cachedSettings is not null && !reset)
            {
                PoolPolicy.PreserveHealth(fresh.Keys, _cachedSettings.Keys);
            }

            _cachedSettings = fresh;
            _cachedStampUtc = stampUtc;
            _cachedSize = size;
            return _cachedSettings;
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void Log(string message)
    {
        try
        {
            var folder = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            const long limit = 512 * 1024;
            var info = new FileInfo(_logPath);
            if (info.Exists && info.Length > limit)
            {
                File.Move(_logPath, _logPath + ".old", overwrite: true);
            }

            File.AppendAllText(
                _logPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Logging must never take the service down.
        }
    }
}
