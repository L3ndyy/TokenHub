using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TokenHub.Services;

namespace TokenHub;

public partial class MainWindow
{

    // ---------------------------------------------------------------- Windows service

    private void RefreshServiceState()
    {
        var state = PoolService.Current;

        Dot(DotService, state switch
        {
            PoolService.State.Running => Kind.Ok,
            PoolService.State.Pending => Kind.Busy,
            _ => Kind.Unknown,
        });

        TxtServiceState.Text = PoolService.Describe();

        // After the app updated itself, the installed service still runs the old files.
        if ((state is PoolService.State.Running or PoolService.State.Stopped) && PoolService.PayloadOutdated)
        {
            TxtServiceHint.Text = "Файлы службы устарели после обновления приложения — нажмите «Обновить файлы службы».";
        }
    }

    private async void ServiceInstall_Click(object sender, RoutedEventArgs e) =>
        await RunServiceAsync(PoolService.Install, "Устанавливаю службу", PoolService.State.Running);

    private async void ServiceStart_Click(object sender, RoutedEventArgs e) =>
        await RunServiceAsync(PoolService.StartService, "Запускаю службу", PoolService.State.Running);

    private async void ServiceStop_Click(object sender, RoutedEventArgs e) =>
        await RunServiceAsync(PoolService.StopService, "Останавливаю службу", PoolService.State.Stopped, freePort: true);

    private async void ServiceUninstall_Click(object sender, RoutedEventArgs e) =>
        await RunServiceAsync(PoolService.Uninstall, "Удаляю службу", PoolService.State.NotInstalled, freePort: true);

    private void ServiceRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshServiceState();
        RefreshPoolState();
        Status("Состояние службы обновлено", Kind.Ok);
    }

    /// <summary>
    /// Runs an elevated service command. Only one process may hold the port, so before
    /// handing it to the service the app releases it, and takes it back if the service
    /// does not end up running.
    /// </summary>
    private async Task RunServiceAsync(
        Func<string> command,
        string title,
        PoolService.State expected,
        bool freePort = false)
    {
        Busy(true);
        Status(title + " (может появиться запрос прав администратора)...", Kind.Busy);

        bool releasePort = freePort || LocalProxy.Instance.Running;
        if (releasePort && LocalProxy.Instance.Running)
        {
            await LocalProxy.Instance.StopAsync();
        }

        string output = await Task.Run(command);

        // Give the SCM a moment to report a settled state.
        await Task.Delay(800);

        bool serviceRunning = PoolService.Current == PoolService.State.Running;
        if (releasePort && !serviceRunning && !LocalProxy.Instance.Running)
        {
            await LocalProxy.Instance.StartAsync();
        }

        Busy(false);
        RefreshServiceState();
        RefreshPoolState();

        if (!string.IsNullOrWhiteSpace(output))
        {
            TxtServiceHint.Text = output.Trim();
        }

        var state = PoolService.Current;
        bool ok = state == expected
                  || (expected == PoolService.State.Running && state == PoolService.State.Pending);

        Status($"{title}: {PoolService.Describe()}", ok ? Kind.Ok : Kind.Fail);
    }

    private void CopyPoolUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!LocalProxy.Instance.Running)
        {
            Status("Локальный прокси не запущен", Kind.Fail);
            return;
        }

        string url = App.Settings.LocalBaseUrl;
        Copy(url);
        Status($"Скопировано: {url}", Kind.Ok);
    }

    private void ResetPool_Click(object sender, RoutedEventArgs e)
    {
        LocalProxy.Instance.ResetCooldowns();

        // The service keeps its own learned pauses; stamp the reset so it drops them too.
        App.Settings.PoolResetUtc = DateTime.UtcNow;
        SettingsStore.Save(App.Settings);

        Status("Паузы ключей сброшены", Kind.Ok);

        RenderKeys();
        RefreshPoolState();
    }

    private void Failover_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool enabled = TglFailover.IsChecked == true;
        App.Settings.AutoFailover = enabled;
        SettingsStore.Save(App.Settings);
        Status(enabled ? "Автопереключение включено" : "Автопереключение выключено", Kind.Ok);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Item 1: closing hides the window so the key pool keeps serving OpenCode.
        // Only the tray menu's "Выход" really exits.
        if (!_exiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        LocalProxy.Instance.Notice -= OnPoolNotice;
        Tray.Remove(new WindowInteropHelper(this).Handle);
        _liveTimer?.Stop();
        SaveBounds();

        base.OnClosing(e);
    }
}
