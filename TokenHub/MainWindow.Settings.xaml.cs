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

    /// <summary>Jump to a settings card: the anchor row is the page's short table of contents.</summary>
    private void SettingsAnchor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }
            && FindName(name) is FrameworkElement section)
        {
            section.BringIntoView();
        }
    }

    // ---------------------------------------------------------------- settings backup (T10)

    private void BackupAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Резервная копия TokenHub",
            FileName = $"tokenhub-backup-{DateTime.Now:yyyyMMdd}.json",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            DefaultExt = ".json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(
                dialog.FileName,
                JsonSerializer.Serialize(App.Settings, new JsonSerializerOptions { WriteIndented = true }));
            Journal($"Копия настроек сохранена: {Path.GetFileName(dialog.FileName)}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось сохранить копию: " + ex.Message, Kind.Fail);
        }
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Восстановление настроек TokenHub",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(dialog.FileName));
            if (loaded is null)
            {
                Status("Файл не распознан", Kind.Fail);
                return;
            }

            loaded.Keys ??= new List<ApiKeyEntry>();
            if (MessageBox.Show(
                    $"Заменить текущие настройки? Ключей в копии: {loaded.Keys.Count}.",
                    "TokenHub", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }

            App.ReplaceSettings(loaded);
            RefreshAllFromSettings();
            Journal("Настройки восстановлены из копии", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось восстановить: " + ex.Message, Kind.Fail);
        }
    }

    /// <summary>Re-reads every control after the whole settings object was replaced.</summary>
    private void RefreshAllFromSettings()
    {
        _loading = true;
        var s = App.Settings;

        TglFailover.IsChecked = s.AutoFailover;
        TglMinimized.IsChecked = s.StartMinimized;
        TxtCooldown.Text = s.CooldownMinutes.ToString();
        SelectCombo(CmbStrategy, s.KeyStrategy);
        SelectCombo(CmbSort, s.KeysSort);

        ThemeSystem.IsChecked = s.Theme == "system";
        ThemeDark.IsChecked = s.Theme == "dark";
        ThemeLight.IsChecked = s.Theme == "light";
        TglCompact.IsChecked = s.CompactMode;

        TxtProxyBase.Text = s.ProxyBase;
        TxtSecret.Text = s.ProxySecret;
        TxtUpstream.Text = s.Upstream;
        TxtBaseUrl.Text = s.BaseUrl;

        FillModels(s.DefaultModel);
        TglFreeOnly.IsChecked = s.FreeOnly;

        MarkAccent();
        _loading = false;

        ApplyCompactMode();
        RenderKeys();
        RefreshPoolState();
        RenderProfiles();
        RefreshStats();
        RefreshModeText();
    }

    // ---------------------------------------------------------------- local state

    private void RefreshLocalState()
    {
        int oc = Launcher.OpenCodeRunningCount();
        Dot(DotOc, oc > 0 ? Kind.Ok : Kind.Unknown);
        HomeOcStatus.Text = oc > 0 ? "Запущен" : "Не запущен";
        HomeOcDetail.Text = Launcher.OpenCodeExe is null
            ? "Приложение OpenCode не найдено"
            : $"Процессов: {oc}";

        RefreshModeText();
    }

    private void RefreshModeText()
    {
        var settings = App.Settings;
        string? current = OpenCodeConfig.Exists ? OpenCodeConfig.GetBaseUrl() : null;

        string mode;
        if (current is null)
        {
            mode = "Конфиг OpenCode не найден.";
        }
        else if (current.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                 || current.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            mode = "Через локальный пул ключей — переключение идёт без перезапуска.";
        }
        else if (current.Contains("/api/proxy/", StringComparison.OrdinalIgnoreCase))
        {
            mode = "Открыт через прокси — регион не важен.";
        }
        else if (current.Contains(settings.Upstream, StringComparison.OrdinalIgnoreCase))
        {
            mode = "Напрямую к Token Harbor (в России вернёт region_blocked).";
        }
        else
        {
            mode = "Свой адрес.";
        }

        TxtMode.Text = $"Сейчас: {mode}{Environment.NewLine}Адрес: {current ?? "—"}";
    }

    // ---------------------------------------------------------------- checks
    private async Task CheckAllAsync()
    {
        Busy(true);
        Status("Проверяю соединение и прокси...", Kind.Busy);

        var settings = App.Settings;

        SetDot(DotProxy, Kind.Busy);
        HomeProxyStatus.Text = "Проверка...";

        var health = await Probes.ProxyHealthAsync(settings);
        SetDot(DotProxy, health.Ok ? Kind.Ok : Kind.Fail);
        HomeProxyStatus.Text = health.Ok ? "Работает" : "Не отвечает";
        HomeProxyDetail.Text = health.Ok
            ? string.IsNullOrWhiteSpace(settings.ProxyBase) ? settings.Upstream : settings.ProxyBase
            : health.Detail;

        Busy(false);
        Status(
            health.Ok ? "Прокси отвечает" : "Прокси не отвечает — смотрите вкладку «Сеть и прокси»",
            health.Ok ? Kind.Ok : Kind.Fail);
    }

    private void CheckAll_Click(object sender, RoutedEventArgs e) => _ = CheckAllAsync();

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        Busy(true);
        Status("Проверяю обновления...", Kind.Busy);

        var (ok, newer, info, message) = await Updater.CheckAsync();

        Busy(false);
        BtnUpdate.IsEnabled = true;
        Status(message, ok ? Kind.Ok : Kind.Fail);

        if (info is null)
        {
            if (!ok)
            {
                MessageBox.Show(this, message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Доступна версия {info.Version} (у вас {Updater.CurrentVersion}).\n\n" +
            "Скачать и запустить установщик? Программа закроется и обновится.",
            "Обновление",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            Status("Обновление отложено", Kind.Unknown);
            return;
        }

        await InstallUpdateAsync(info);
    }

    private async void CheckProxy_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        Status("Проверяю прокси…", Kind.Busy);
        var result = await Probes.ProxyHealthAsync(App.Settings);
        LogCheck(result);
        SetDot(DotProxy, result.Ok ? Kind.Ok : Kind.Fail);
        HomeProxyStatus.Text = result.Ok ? "Работает" : "Не отвечает";
        HomeProxyDetail.Text = result.Ok ? App.Settings.ProxyBase : result.Detail;
        Busy(false);
        Status(result.Summary, result.Ok ? Kind.Ok : Kind.Fail);
    }

    private async void CheckModels_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        Status("Запрашиваю список моделей…", Kind.Busy);
        var settings = App.Settings;
        var result = settings.UsePool && LocalProxy.Instance.Running
            ? await Probes.PoolModelsAsync(settings)
            : await Probes.ProxyModelsAsync(settings, OpenCodeConfig.GetApiKey());

        LogCheck(result);
        Busy(false);
        Status(result.Summary, result.Ok ? Kind.Ok : Kind.Fail);
    }

    private async void CheckDirect_Click(object sender, RoutedEventArgs e)
    {
        Busy(true);
        Status("Проверяю прямой доступ…", Kind.Busy);
        var result = await Probes.DirectUpstreamAsync(App.Settings, OpenCodeConfig.GetApiKey());
        LogCheck(result);
        Busy(false);
        Status(result.Summary, result.Ok ? Kind.Ok : Kind.Fail);
    }


    private void LogCheck(ProbeResult result)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {(result.Ok ? "OK  " : "FAIL")} {result.Summary}{Environment.NewLine}          {result.Detail}";
        TxtChecks.Text = string.IsNullOrEmpty(TxtChecks.Text) ? line : TxtChecks.Text + Environment.NewLine + line;
        TxtChecks.ScrollToEnd();
    }

    private static void SetDot(System.Windows.Shapes.Ellipse dot, Kind kind) => Dot(dot, kind);

    // ---------------------------------------------------------------- proxy mode

    private void UseProxy_Click(object sender, RoutedEventArgs e) => ApplyBaseUrl(App.Settings.BaseUrl);

    private void UseDirect_Click(object sender, RoutedEventArgs e) =>
        ApplyBaseUrl($"{App.Settings.Upstream.TrimEnd('/')}/v1");

    private void ApplyBaseUrl(string url)
    {
        try
        {
            string? backup = OpenCodeConfig.SetBaseUrl(url);
            RefreshModeText();
            Status(backup is null
                    ? "Создан новый конфиг OpenCode с нужным адресом"
                    : $"Адрес провайдера обновлён. Бэкап: {Path.GetFileName(backup)}",
                Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось изменить конфиг: " + ex.Message, Kind.Fail);
        }
    }

    private void CopyBaseUrl_Click(object sender, RoutedEventArgs e)
    {
        Copy(App.Settings.BaseUrl);
        Status("Адрес скопирован", Kind.Ok);
    }

    private void CopySecret_Click(object sender, RoutedEventArgs e)
    {
        Copy(App.Settings.ProxySecret);
        Status("Секрет скопирован", Kind.Ok);
    }

    private void ProxyField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.ProxyBase = TxtProxyBase.Text.Trim();
        App.Settings.ProxySecret = TxtSecret.Text.Trim();
        App.Settings.Upstream = TxtUpstream.Text.Trim();
        SettingsStore.Save(App.Settings);

        TxtBaseUrl.Text = App.Settings.BaseUrl;
        RefreshModeText();
    }
}
