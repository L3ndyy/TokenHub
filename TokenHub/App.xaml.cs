using System.Windows;
using TokenHub.Services;

namespace TokenHub;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>True when launched with --minimized, so the window starts in the tray (item T14).</summary>
    public static bool StartMinimized { get; private set; }

    private Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        // Item T3: only one TokenHub may run. A second launch just wakes the first.
        _instance = new Mutex(true, @"Local\TokenHub.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Native.RequestShow("TokenHub");
            Shutdown();
            return;
        }

        Settings = SettingsStore.Load();
        Theme.Apply(Settings.Theme, Settings.Accent);

        // Daily token totals live next to the settings file this process owns.
        UsageStore.Configure(SettingsStore.FilePath);

        // Item T1: rewrite once so any keys stored in plain text by older versions
        // are upgraded to DPAPI ciphertext right away.
        SettingsStore.Save(Settings);
        EventLog.Write(
            $"TokenHub запущен: ключей {Settings.Keys.Count}, режим — " +
            (Settings.UsePool ? "локальный пул" : "прокси напрямую"));

        ImportExistingKey();

        LocalProxy.Instance.Configure(() => Settings, SettingsStore.Save);
        _ = StartPoolAsync();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            LocalProxy.Instance.StopAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Exiting anyway.
        }

        try
        {
            _instance?.ReleaseMutex();
            _instance?.Dispose();
        }
        catch
        {
            // Nothing to do on the way out.
        }

        base.OnExit(e);
    }

    /// <summary>Takes over a settings object loaded from a backup file (item T10).</summary>
    public static void ReplaceSettings(AppSettings settings)
    {
        Settings = settings;
        Settings.Keys ??= new List<ApiKeyEntry>();
        foreach (var key in Settings.Keys)
        {
            key.BlockedModels ??= new List<string>();
        }

        Theme.Apply(Settings.Theme, Settings.Accent);
        SettingsStore.Save(Settings);
    }

    private static async Task StartPoolAsync()
    {
        // A running Windows service already owns the port; a second listener would
        // silently move to another port and OpenCode would look at the wrong one.
        if (PoolService.Current == PoolService.State.Running)
        {
            return;
        }

        await LocalProxy.Instance.StartAsync();
        SynchronizePoolUrl();
    }

    /// <summary>
    /// If the proxy had to move to another port, OpenCode still points at the old
    /// one. Repair only a stale 127.0.0.1 address, never an unrelated one.
    /// </summary>
    private static void SynchronizePoolUrl()
    {
        if (!Settings.UsePool || !LocalProxy.Instance.Running)
        {
            return;
        }

        try
        {
            string wanted = Settings.LocalBaseUrl;
            string? current = OpenCodeConfig.GetBaseUrl();

            if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)
                || current is null
                || !current.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            OpenCodeConfig.SetProvider(wanted, Settings.FirstAvailableKey?.Key);
        }
        catch
        {
            // A failed sync must not stop the app from starting.
        }
    }

    /// <summary>
    /// On the first run the pool is empty, but OpenCode already has one real key.
    /// Picking it up means failover works without re-typing anything.
    /// </summary>
    private static void ImportExistingKey()
    {
        if (Settings.Keys.Count > 0)
        {
            return;
        }

        string? key = OpenCodeConfig.GetApiKey();
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("thk_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string trimmed = key.Trim();
        if (Settings.Keys.Any(k => k.Key == trimmed))
        {
            return;
        }

        Settings.Keys.Add(new ApiKeyEntry { Key = trimmed, Label = "Ключ из OpenCode" });
        SettingsStore.Save(Settings);
    }
}
