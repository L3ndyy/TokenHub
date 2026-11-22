using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TokenHub.Services;

namespace TokenHub;

public partial class MainWindow : Window
{
    /// <summary>Drag payload format, so key order is never dragged out as plain text.</summary>
    private const string KeyIdFormat = "TokenHub.KeyId";

    private enum Kind
    {
        Unknown,
        Ok,
        Busy,
        Fail,
    }

    private bool _loading = true;
    private bool _exiting;
    private string? _dragKeyId;
    private Point _dragStart;
    private string _lastEvent = string.Empty;
    private readonly List<long> _samples = new();
    private DispatcherTimer? _liveTimer;
    private long _serviceRequests;
    private readonly Dictionary<string, (int Requests, int Failures, long AvgMs)> _serviceKeyStats = new();
    private readonly Dictionary<string, ToggleButton> _skillChips = new();
    private string _serviceHealth = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        RestoreGeometry();
        Loaded += OnLoaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private static string? FindProjectRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "tokenharbor-proxy")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }

    private static string ProjectRoot() =>
        FindProjectRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ProxyFolder => Path.Combine(ProjectRoot(), "tokenharbor-proxy");

    // ---------------------------------------------------------------- startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings;

        TxtProxyBase.Text = settings.ProxyBase;
        TxtSecret.Text = settings.ProxySecret;
        TxtUpstream.Text = settings.Upstream;
        TxtBaseUrl.Text = settings.BaseUrl;
        TxtChecks.Text = string.Empty;

        TglAutostart.IsChecked = Launcher.IsAutostartEnabled();
        TglFailover.IsChecked = settings.AutoFailover;
        TglMinimized.IsChecked = settings.StartMinimized;

        TxtCooldown.Text = settings.CooldownMinutes.ToString();
        SelectCombo(CmbStrategy, settings.KeyStrategy);
        SelectCombo(CmbSort, settings.KeysSort);

        ThemeSystem.IsChecked = settings.Theme == "system";
        ThemeDark.IsChecked = settings.Theme == "dark";
        ThemeLight.IsChecked = settings.Theme == "light";

        MarkAccent();

        SettingsPaths.Text =
            $"TokenHub:   {SettingsStore.FilePath}{Environment.NewLine}" +
            $"OpenCode:   {OpenCodeConfig.FilePath}{Environment.NewLine}" +
            $"Версия:     {Updater.CurrentVersion} (API-прокси и лаунчер)";

        _loading = false;

        RefreshLocalState();
        RefreshServiceState();
        RefreshStats();
        StartLiveStats();
        _ = RefreshServiceStatusAsync();
        _ = StartupChecksAsync();
        OpenWizardIfNeeded();

        // Item T14: an autostart launch should stay in the tray, not pop up.
        if (App.StartMinimized || settings.StartMinimized)
        {
            // Hide() inside Loaded leaves a visible window with nothing painted in
            // it: WPF is still in the middle of showing the window. Wait for the
            // first render, then slip into the tray.
            ContentRendered += (_, _) => Hide();
        }
    }

    /// <summary>
    /// A freshly installed OpenCode has no config at all, so the first launch should end
    /// with a working setup instead of a JSON file the user has to write themselves.
    /// Runs after the window has painted, and only while the wizard has never been seen.
    /// </summary>
    private void OpenWizardIfNeeded()
    {
        if (App.Settings.FirstRunDone || App.StartMinimized || App.Settings.StartMinimized)
        {
            return;
        }

        if (!OpenCodeConfig.NeedsSetup)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(OpenWizard));
    }

    // ---------------------------------------------------------------- window geometry (V8)

    private void RestoreGeometry()
    {
        var s = App.Settings;
        if (s.WindowWidth is >= 600 and <= 20000)
        {
            Width = s.WindowWidth;
        }

        if (s.WindowHeight is >= 400 and <= 20000)
        {
            Height = s.WindowHeight;
        }

        if (s.WindowLeft is double left && s.WindowTop is double top && IsOnVirtualScreen(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (s.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnVirtualScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50
        && top >= SystemParameters.VirtualScreenTop - 50
        && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50
        && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;

    /// <summary>False when the user asked Windows to keep animations off (item V7).</summary>
    private static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    private void SaveBounds()
    {
        try
        {
            var s = App.Settings;
            s.WindowMaximized = WindowState == WindowState.Maximized;
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : base.RestoreBounds;

            if (!double.IsNaN(bounds.Left))
            {
                s.WindowLeft = bounds.Left;
                s.WindowTop = bounds.Top;
                s.WindowWidth = bounds.Width;
                s.WindowHeight = bounds.Height;
            }

            SettingsStore.Save(s);
        }
        catch
        {
            // Geometry is a convenience; never fail closing over it.
        }
    }

    // ---------------------------------------------------------------- journal & status (T9, V9)

    /// <summary>Sets the status bar and appends the same line to the activity log.</summary>
    private void Journal(string message, Kind kind)
    {
        Status(message, kind);
        EventLog.Write(message);

        _lastEvent = $"{DateTime.Now:HH:mm}  {message}";
        if (TxtLastEvent is not null)
        {
            TxtLastEvent.Text = _lastEvent;
        }
    }

    private void OpenJournal_Click(object sender, RoutedEventArgs e) => OpenJournal();

    private void StatusBar_Click(object sender, MouseButtonEventArgs e) => OpenJournal();

    private void OpenJournal()
    {
        new LogWindow { Owner = this }.Show();
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Диагностический отчёт TokenHub",
            FileName = $"tokenhub-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            Filter = "ZIP (*.zip)|*.zip|Все файлы (*.*)|*.*",
            DefaultExt = ".zip",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Diagnostics.Export(dialog.FileName, App.Settings);
            Journal($"Диагностика сохранена: {Path.GetFileName(dialog.FileName)}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось создать отчёт: " + ex.Message, Kind.Fail);
        }
    }

    // ---------------------------------------------------------------- statistics (V3, V4, T15)

    private void RefreshStats()
    {
        var proxy = LocalProxy.Instance;
        var sb = new System.Text.StringBuilder();

        if (proxy.Running)
        {
            sb.AppendLine($"Запросов: {proxy.Requests} · переключений: {proxy.Failovers}");
            sb.AppendLine($"Токенов за сессию: вход {proxy.PromptTokens} · выход {proxy.CompletionTokens}");
            sb.AppendLine(TodayLine());
            sb.AppendLine($"Стратегия: {StrategyName(App.Settings.KeyStrategy)}");

            foreach (var key in App.Settings.Keys)
            {
                var stat = proxy.StatOf(key.Id);
                int ok = stat?.Requests ?? 0;
                int fail = stat?.Failures ?? 0;
                long avg = ok + fail > 0 ? stat!.TotalMs / (ok + fail) : 0;
                sb.AppendLine($"   • {key.Label}: успешно {ok}, ошибок {fail}, средн. {avg} мс");
            }
        }
        else if (PoolService.Current == PoolService.State.Running)
        {
            sb.AppendLine("Счётчики службы Windows подтягиваются по каналу...");
        }
        else
        {
            sb.AppendLine("Локальный прокси не запущен.");
        }

        TxtPoolStats.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// "Today" comes from usage.json, so it survives restarts of the app and of the
    /// Windows service, unlike the session counters above it.
    /// </summary>
    private static string TodayLine()
    {
        var today = UsageStore.Today;
        return $"Токенов за сегодня: вход {today.Prompt} · выход {today.Completion} (ответов {today.Responses})";
    }

    /// <summary>Refreshes the dashboard and the sparkline while the window is open (item V3).</summary>
    private void StartLiveStats()
    {
        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveTimer.Tick += async (_, _) =>
        {
            if (!IsVisible)
            {
                return;
            }

            if (LocalProxy.Instance.Running)
            {
                RefreshStats();
            }
            else
            {
                await RefreshServiceStatusAsync();
            }

            DrawSpark();
        };
        _liveTimer.Start();
        DrawSpark();
    }

    /// <summary>Plots requests-per-interval as a simple line on a Canvas (item V3).</summary>
    private void DrawSpark()
    {
        if (SparkCanvas is null)
        {
            return;
        }

        SparkCanvas.Children.Clear();

        double width = SparkCanvas.ActualWidth;
        double height = SparkCanvas.ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        long total = LocalProxy.Instance.Running ? LocalProxy.Instance.Requests : _serviceRequests;
        _samples.Add(total);
        while (_samples.Count > 60)
        {
            _samples.RemoveAt(0);
        }

        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = ResBrush("Br.Accent"),
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };

        if (_samples.Count >= 2)
        {
            var deltas = new List<double>();
            for (int i = 1; i < _samples.Count; i++)
            {
                deltas.Add(Math.Max(0, _samples[i] - _samples[i - 1]));
            }

            double peak = Math.Max(1, deltas.Max());
            for (int i = 0; i < deltas.Count; i++)
            {
                double x = deltas.Count == 1 ? 0 : width * i / (deltas.Count - 1);
                double y = height - 3 - (deltas[i] / peak) * (height - 8);
                line.Points.Add(new Point(x, y));
            }
        }

        SparkCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0,
            X2 = width,
            Y1 = height - 2,
            Y2 = height - 2,
            Stroke = ResBrush("Br.ControlBorder"),
            StrokeThickness = 1,
        });

        if (line.Points.Count > 0)
        {
            SparkCanvas.Children.Add(line);
        }
    }

    /// <summary>When only the service runs, its counters arrive over the named pipe (item T11).</summary>
    private async Task RefreshServiceStatusAsync()
    {
        if (PoolService.Current != PoolService.State.Running)
        {
            return;
        }

        string? json = await Task.Run(() => Pipe.Query());
        if (string.IsNullOrEmpty(json))
        {
            return;
        }

        bool healthChanged = false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            long req = Long(root, "requests");
            long fail = Long(root, "failovers");
            long pin = Long(root, "promptTokens");
            long pout = Long(root, "completionTokens");
            long pinToday = Long(root, "promptTokensToday");
            long poutToday = Long(root, "completionTokensToday");
            long todayAnswers = Long(root, "usageResponsesToday");
            _serviceRequests = req;

            // Item V4: the service owns the live counters, so read them over the pipe.
            _serviceKeyStats.Clear();
            var signature = new System.Text.StringBuilder();

            if (root.TryGetProperty("keys", out var keysElement)
                && keysElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in keysElement.EnumerateArray())
                {
                    string? id = item.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                    if (id is null)
                    {
                        continue;
                    }

                    _serviceKeyStats[id] = (
                        (int)Long(item, "requests"),
                        (int)Long(item, "failures"),
                        Long(item, "avgMs"));

                    // The service is the one talking to Token Harbor, so its view of a key
                    // ("исчерпан" on a chat 429) is the truth — a /models check cannot see
                    // that, which is why an exhausted key used to look "работает".
                    var key = App.Settings.Keys.FirstOrDefault(k => k.Id == id);
                    if (key is null)
                    {
                        continue;
                    }

                    bool invalid = item.TryGetProperty("invalid", out var invalidValue)
                                   && invalidValue.ValueKind == JsonValueKind.True;
                    DateTime? until = item.TryGetProperty("exhaustedUntilUtc", out var untilValue)
                                      && untilValue.ValueKind == JsonValueKind.String
                        ? untilValue.GetDateTime()
                        : null;
                    string status = item.TryGetProperty("lastStatus", out var statusValue)
                        ? statusValue.GetString() ?? string.Empty
                        : string.Empty;
                    var blocked = BlockedOf(item);

                    signature.Append(id).Append('|').Append(invalid).Append('|')
                        .Append(until).Append('|').Append(status).Append('|')
                        .Append(string.Join(",", blocked)).Append(';');

                    key.Invalid = invalid;
                    key.ExhaustedUntilUtc = until;
                    key.BlockedModels = blocked;
                    key.LastStatus = status;
                }
            }

            healthChanged = signature.ToString() != _serviceHealth;
            _serviceHealth = signature.ToString();

            TxtPoolStats.Text =
                $"Служба Windows:{Environment.NewLine}" +
                $"Запросов: {req} · переключений: {fail}{Environment.NewLine}" +
                $"Токенов за сессию: вход {pin} · выход {pout}{Environment.NewLine}" +
                $"Токенов за сегодня: вход {pinToday} · выход {poutToday} (ответов {todayAnswers})";
        }
        catch
        {
            // Older service without the pipe; the hint stays as-is.
        }

        // Redraw only when a key's health actually moved, so scrolling and dragging
        // are not disturbed every two seconds.
        if (healthChanged)
        {
            RenderKeys();
            RefreshPoolState();
        }
    }

    private static List<string> BlockedOf(JsonElement item)
    {
        var list = new List<string>();
        if (item.TryGetProperty("blockedModels", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in array.EnumerateArray())
            {
                if (model.GetString() is { Length: > 0 } name)
                {
                    list.Add(name);
                }
            }
        }

        return list;
    }

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : 0;

    private void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        LocalProxy.Instance.ResetStats();
        UsageStore.Reset();
        RefreshStats();
        Journal("Счётчики статистики сброшены (сессия и текущий день)", Kind.Ok);
    }

    private static string StrategyName(string strategy) => strategy switch
    {
        "roundRobin" => "по кругу",
        "leastUsed" => "реже использованный",
        _ => "по порядку",
    };

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

        TxtProxyBase.Text = s.ProxyBase;
        TxtSecret.Text = s.ProxySecret;
        TxtUpstream.Text = s.Upstream;
        TxtBaseUrl.Text = s.BaseUrl;

        MarkAccent();
        _loading = false;

        RenderKeys();
        RefreshPoolState();
        RefreshStats();
        RefreshModeText();
    }

    // ---------------------------------------------------------------- pool behaviour (T6, T16)

    private static void SelectCombo(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem candidate
                && string.Equals((string?)candidate.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = candidate;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static string? SelectedTag(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Strategy_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        string tag = SelectedTag(CmbStrategy) ?? "ordered";
        App.Settings.KeyStrategy = tag;
        SettingsStore.Save(App.Settings);
        RefreshStats();
        Journal($"Стратегия выбора ключа: {StrategyName(tag)}", Kind.Ok);
    }

    private void Cooldown_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (int.TryParse(TxtCooldown.Text.Trim(), out int minutes))
        {
            App.Settings.CooldownMinutes = Math.Clamp(minutes, 1, 24 * 60);
            SettingsStore.Save(App.Settings);
        }
    }

    private void Minimized_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.StartMinimized = TglMinimized.IsChecked == true;
        SettingsStore.Save(App.Settings);
    }

    private void SearchKeys_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RenderKeys();
    }

    private void SortKeys_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.KeysSort = SelectedTag(CmbSort) ?? "manual";
        SettingsStore.Save(App.Settings);
        RenderKeys();
    }

    private void ServiceUpdate_Click(object sender, RoutedEventArgs e) =>
        _ = RunServiceAsync(PoolService.Update, "Обновляю файлы службы", PoolService.State.Running);

    // ---------------------------------------------------------------- command palette (V10)

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenPalette();
        }
    }

    private void OpenPalette()
    {
        var palette = new PaletteWindow { Owner = this };
        palette.ShowDialog();

        if (palette.Chosen is { } command)
        {
            RunPaletteCommand(command);
        }
    }

    private void RunPaletteCommand(string command)
    {
        switch (command)
        {
            case "checkAll": _ = CheckAllKeysAsync(); break;
            case "apply": _ = ApplyPoolAsync(inform: true); break;
            case "keys": NavKeys.IsChecked = true; break;
            case "network": NavNetwork.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
            case "journal": OpenJournal(); break;
            case "diagnostics": ExportDiagnostics_Click(this, new RoutedEventArgs()); break;
            case "config": OpenConfig_Click(this, new RoutedEventArgs()); break;
            case "hub": OpenHubFolder_Click(this, new RoutedEventArgs()); break;
            case "opencode": LaunchOpenCode_Click(this, new RoutedEventArgs()); break;
            case "terminal": OpenTerminal_Click(this, new RoutedEventArgs()); break;
        }
    }

    /// <summary>Home checks first, then a quiet sweep over the key pool (auto-check on start).</summary>
    private async Task StartupChecksAsync()
    {
        await CheckAllAsync();

        if (App.Settings.Keys.Count > 0)
        {
            await CheckAllKeysAsync();
        }

        await OfferServiceUpdateAsync();
    }

    /// <summary>
    /// After the app updates itself, the installed Windows service still runs the old
    /// binaries. Ask once at start-up instead of leaving a stale proxy behind, and let
    /// the user decline: the button in "Ключи" does the same thing any time.
    /// </summary>
    private async Task OfferServiceUpdateAsync()
    {
        if (PoolService.Current is not (PoolService.State.Running or PoolService.State.Stopped))
        {
            return;
        }

        if (!PoolService.PayloadOutdated)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Файлы службы Windows остались от прежней версии приложения." + Environment.NewLine + Environment.NewLine +
            "Обновить их сейчас? Служба ненадолго остановится и запустится заново, Windows один раз попросит права администратора.",
            "TokenHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            Status("Файлы службы можно обновить в разделе «Ключи»", Kind.Unknown);
            return;
        }

        await RunServiceAsync(PoolService.Update, "Обновляю файлы службы", PoolService.State.Running);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        Native.TryEnableMica(handle, Theme.IsDark(App.Settings.Theme));

        // Item 1: the window lives in the notification area so the pool keeps running
        // after the user "closes" it.
        if (Tray.Install(handle, "TokenHub — пул ключей Token Harbor"))
        {
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        }

        LocalProxy.Instance.Notice += OnPoolNotice;
    }

    // ---------------------------------------------------------------- tray

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (Native.ShowMessage != 0 && msg == Native.ShowMessage)
        {
            // Another launch asked us to come to the front. Go through WPF's
            // Show(), otherwise a window hidden in the tray comes back blank.
            handled = true;
            Dispatcher.BeginInvoke(new Action(ShowFromTray));
            return IntPtr.Zero;
        }

        if (msg != Tray.CallbackMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;

        if (Tray.IsLeftClick(lParam))
        {
            ShowFromTray();
            return IntPtr.Zero;
        }

        if (!Tray.IsRightClick(lParam))
        {
            return IntPtr.Zero;
        }

        switch (Tray.ShowMenu(hwnd, LocalProxy.Instance.Running || PoolService.Current == PoolService.State.Running))
        {
            case Tray.CmdOpen:
                ShowFromTray();
                break;

            case Tray.CmdStatus:
                OpenQuickFlyout();
                break;

            case Tray.CmdKeysPage:
                NavKeys.IsChecked = true;
                ShowFromTray();
                break;

            case Tray.CmdCheckKeys:
                _ = CheckAllKeysAsync();
                break;

            case Tray.CmdExit:
                ExitApplication();
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Compact flyout beside the clock (item V13).</summary>
    private void OpenQuickFlyout()
    {
        var flyout = new QuickWindow();
        flyout.ShowDialog();

        switch (flyout.Action)
        {
            case "open":
                ShowFromTray();
                break;
            case "check":
                _ = CheckAllKeysAsync();
                break;
            case "keys":
                NavKeys.IsChecked = true;
                ShowFromTray();
                break;
            case "journal":
                OpenJournal();
                break;
        }
    }

    private void HideToTray()
    {
        Hide();

        bool pool = LocalProxy.Instance.Running || PoolService.Current == PoolService.State.Running;
        Tray.Notify(
            new WindowInteropHelper(this).Handle,
            "TokenHub продолжает работать",
            pool
                ? "Пул ключей остаётся доступен для OpenCode. Двойной щелчок по значку откроет окно."
                : "Приложение свёрнуто в трей. Двойной щелчок по значку откроет окно.");
    }

    private void ExitApplication()
    {
        if (App.Settings.UsePool && LocalProxy.Instance.Running)
        {
            var answer = MessageBox.Show(
                "TokenHub держит локальный прокси, через который OpenCode получает ответы." + Environment.NewLine + Environment.NewLine +
                "Если выйти сейчас, прокси остановится и OpenCode перестанет работать до следующего запуска TokenHub." + Environment.NewLine + Environment.NewLine +
                "Проще нажать «Закрыть» — окно свернётся в трей, а прокси продолжит работать. Всё равно выйти?",
                "TokenHub",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        _exiting = true;
        Close();
    }

    /// <summary>
    /// Raised by the proxy on a background thread whenever it switches keys or runs dry.
    /// Item 6: the user gets a Windows notification, not only a status-bar line.
    /// </summary>
    private void OnPoolNotice(string title, string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool warning = title.Contains("исчерпан", StringComparison.OrdinalIgnoreCase);
            Tray.Notify(new WindowInteropHelper(this).Handle, title, text, warning);
            Status(text, warning ? Kind.Fail : Kind.Ok);

            RenderKeys();
            RefreshPoolState();
        });
    }

    // ---------------------------------------------------------------- chrome

    private void Caption_Click(object sender, RoutedEventArgs e)
    {
        switch ((string)((Button)sender).Tag)
        {
            case "min":
                WindowState = WindowState.Minimized;
                break;
            case "max":
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                break;
            case "close":
                Close();
                break;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelHome is null)
        {
            return;
        }

        string tag = (string)((RadioButton)sender).Tag;
        PanelHome.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        PanelLaunch.Visibility = tag == "launch" ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = tag == "network" ? Visibility.Visible : Visibility.Collapsed;
        PanelKeys.Visibility = tag == "keys" ? Visibility.Visible : Visibility.Collapsed;
        PanelSkills.Visibility = tag == "skills" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // Item V7: a short fade between panels, skipped when Windows disables animations.
        UIElement? shown = tag switch
        {
            "launch" => PanelLaunch,
            "network" => PanelNetwork,
            "keys" => PanelKeys,
            "skills" => PanelSkills,
            "settings" => PanelSettings,
            _ => PanelHome,
        };

        if (AnimationsEnabled && shown is not null)
        {
            shown.Opacity = 0;
            shown.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }

        if (tag == "keys")
        {
            RenderKeys();
            RefreshPoolState();
        }

        if (tag == "network")
        {
            RefreshModeText();
        }

        if (tag == "skills")
        {
            RenderSkills();
        }
    }

    // ---------------------------------------------------------------- status

    private void Busy(bool active)
    {
        BusyRing.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Status(string message, Kind kind = Kind.Unknown)
    {
        TxtStatus.Text = message;
        StatusDot.Fill = Brush(kind);
    }

    private static Brush Brush(Kind kind) => (Brush)Application.Current.Resources[kind switch
    {
        Kind.Ok => "Br.Success",
        Kind.Fail => "Br.Danger",
        Kind.Busy => "Br.Accent",
        _ => "Br.Muted",
    }];

    private static void Dot(System.Windows.Shapes.Ellipse dot, Kind kind) => dot.Fill = Brush(kind);

    private static void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another process; not worth interrupting the user.
        }
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

        try
        {
            Busy(true);
            Status($"Скачиваю {info.Version}...", Kind.Busy);
            string path = await Updater.DownloadAndRunAsync(info);
            Busy(false);
            Status($"Установщик {info.Version} запущен", Kind.Ok);
            EventLog.Write($"Обновление: запущен установщик {path}");
        }
        catch (Exception ex)
        {
            Busy(false);
            Status("Не удалось скачать установщик", Kind.Fail);
            MessageBox.Show(this, ex.Message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    // ---------------------------------------------------------------- launching




    private void LaunchOpenCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.StartOpenCode();
            Status("OpenCode запускается…", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось запустить OpenCode: " + ex.Message, Kind.Fail);
        }

        Thread.Sleep(700);
        RefreshLocalState();
    }

    private void RestartOpenCode_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.OpenCodeRunningCount() > 0)
        {
            var confirm = MessageBox.Show(
                "Перезапустить OpenCode?\n\nТекущая сессия и это окно агента закроются.\n" +
                "Нужно, чтобы приложение подхватило новый адрес провайдера.",
                "TokenHub",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        try
        {
            Launcher.RestartOpenCode();
            Status("OpenCode перезапущен", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось перезапустить: " + ex.Message, Kind.Fail);
        }
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.OpenTerminal(ProjectRoot());
            Status("Терминал открыт", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status(ex.Message, Kind.Fail);
        }
    }

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.RevealFile(OpenCodeConfig.FilePath);
            Status("Открываю конфиг OpenCode", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status(ex.Message, Kind.Fail);
        }
    }

    private void OpenHubFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.OpenFolder(SettingsStore.Directory);
            Status("Открываю папку TokenHub", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status(ex.Message, Kind.Fail);
        }
    }


    private void OpenProxyFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.OpenFolder(ProxyFolder);
            Status("Открываю проект прокси", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status(ex.Message, Kind.Fail);
        }
    }



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

    // ---------------------------------------------------------------- appearance

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton button)
        {
            return;
        }

        App.Settings.Theme = (string)button.Tag;
        SettingsStore.Save(App.Settings);
        ApplyTheme();
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        App.Settings.Accent = (string)button.Tag;
        SettingsStore.Save(App.Settings);
        ApplyTheme();
        MarkAccent();
    }

    private void ApplyTheme()
    {
        Theme.Apply(App.Settings.Theme, App.Settings.Accent);
        Native.SetDarkTitleBar(new WindowInteropHelper(this).Handle, Theme.IsDark(App.Settings.Theme));
    }

    private void MarkAccent()
    {
        if (AccentRow is null)
        {
            return;
        }

        var text = (Brush)Application.Current.Resources["Br.Text"];
        foreach (var child in AccentRow.Children)
        {
            if (child is Button swatch)
            {
                bool selected = string.Equals((string?)swatch.Tag, App.Settings.Accent, StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = selected ? text : Brushes.Transparent;
            }
        }
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        bool enabled = TglAutostart.IsChecked == true;
        try
        {
            Launcher.SetAutostart(enabled);
            App.Settings.Autostart = enabled;
            SettingsStore.Save(App.Settings);
            Status(enabled ? "Автозапуск включён" : "Автозапуск выключен", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось изменить автозапуск: " + ex.Message, Kind.Fail);
        }
    }

    // ---------------------------------------------------------------- key pool

    private ApiKeyEntry? FindKey(string id) =>
        App.Settings.Keys.FirstOrDefault(k => k.Id == id);

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private static Brush ResBrush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>Draws the pool state card: proxy address, counters and the active key.</summary>
    private void RefreshPoolState()
    {
        var proxy = LocalProxy.Instance;
        var settings = App.Settings;
        bool serviceRunning = PoolService.Current == PoolService.State.Running;

        if (!proxy.Running && !serviceRunning)
        {
            Dot(DotPool, Kind.Fail);
            TxtPoolState.Text = "Локальный прокси не запущен.";
            TxtPoolHint.Text = string.IsNullOrEmpty(proxy.LastError)
                ? "Перезапустите TokenHub."
                : proxy.LastError;
            return;
        }

        int available = settings.Keys.Count(k => k.Available);
        Dot(DotPool, available > 0 ? Kind.Ok : Kind.Fail);

        int port = serviceRunning ? settings.LocalPort : proxy.Port;
        string where = serviceRunning ? "служба Windows" : "приложение TokenHub";

        TxtPoolState.Text =
            $"Прокси работает: http://127.0.0.1:{port}/v1 ({where}){Environment.NewLine}" +
            $"Доступных ключей: {available} из {settings.Keys.Count}.";

        var active = settings.Keys.FirstOrDefault(k => k.Id == proxy.ActiveKeyId);
        string activeText = active is not null
            ? $"{active.Label} ({active.Masked})"
            : settings.Keys.Count > 0
                ? $"по порядку, первый — «{settings.Keys[0].Label}»"
                : "нет ключей";

        var lines = new List<string>
        {
            $"Активный ключ: {activeText}",
        };

        if (serviceRunning)
        {
            lines.Add("Запросы обслуживает служба Windows — счётчики смотрите в её статусе.");
        }
        else
        {
            lines.Add($"Запросов: {proxy.Requests}, переключений: {proxy.Failovers}");
        }

        lines.Add(settings.UsePool
            ? "OpenCode настроен на пул ключей."
            : "В OpenCode адрес пока не применён — нажмите «Применить в OpenCode».");

        if (!string.IsNullOrEmpty(proxy.LastError))
        {
            lines.Add(proxy.LastError);
        }

        TxtPoolHint.Text = string.Join(Environment.NewLine, lines);
    }

    private void RenderKeys()
    {
        var settings = App.Settings;

        TglFailover.IsChecked = settings.AutoFailover;

        // Item V6: the on-screen order is a view — the saved pool order is untouched.
        var visible = PoolPolicy.SortForDisplay(settings.Keys, settings.KeysSort);
        string query = TxtSearch.Text.Trim();

        if (query.Length > 0)
        {
            visible = visible
                .Where(k =>
                    (k.Label ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || k.Masked.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (k.LastStatus ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || k.BlockedModels.Any(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        KeysList.Children.Clear();
        foreach (var key in visible)
        {
            KeysList.Children.Add(BuildKeyRow(key, settings.Keys.IndexOf(key)));
        }

        if (settings.Keys.Count == 0)
        {
            TxtKeysEmpty.Text = "Ключей пока нет. Нажмите «Добавить ключ», чтобы вставить ключ Token Harbor.";
            TxtKeysEmpty.Visibility = Visibility.Visible;
        }
        else if (visible.Count == 0)
        {
            TxtKeysEmpty.Text = $"Ничего не найдено по запросу «{query}».";
            TxtKeysEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            TxtKeysEmpty.Visibility = Visibility.Collapsed;
        }

        TxtKeysHint.Visibility = settings.Keys.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildKeyRow(ApiKeyEntry key, int index)
    {
        Kind state = key.Invalid
            ? Kind.Fail
            : key.ExhaustedUntilUtc > DateTime.UtcNow ? Kind.Fail
            : key.Available ? Kind.Ok
            : Kind.Unknown;

        var grip = new TextBlock
        {
            Text = "\uE76F",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = ResBrush("Br.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.SizeAll,
            ToolTip = "Перетащите, чтобы изменить порядок",
        };

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = Brush(state),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };

        if (index == 0)
        {
            titleRow.Children.Add(new Border
            {
                Background = ResBrush("Br.Accent"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "Главный",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ResBrush("Br.OnAccent"),
                },
            });
        }

        titleRow.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(key.Label) ? key.Masked : key.Label,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResBrush("Br.Text"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Item V5: one small chip states what the key is doing right now.
        (string chipText, string chipColor) = key.Invalid
            ? ("Ошибка ключа", "Br.Danger")
            : key.ExhaustedUntilUtc > DateTime.UtcNow
                ? ($"Исчерпан до {key.ExhaustedUntilUtc!.Value.ToLocalTime():HH:mm}", "Br.Danger")
                : !key.Enabled
                    ? ("Выключен", "Br.Muted")
                    : key.BlockedModels.Count > 0
                        ? ("Частично доступен", "Br.Accent")
                        : ("Работает", "Br.Success");

        titleRow.Children.Add(new Border
        {
            Background = ResBrush(chipColor),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = chipText,
                FontSize = 11,
                Foreground = ResBrush("Br.OnAccent"),
            },
        });

        string statusLine = $"{key.Masked} · {key.LastStatus}";
        if (key.BlockedModels.Count > 0)
        {
            statusLine += $" · нет плана: {string.Join(", ", key.BlockedModels)}";
        }

        var caption = new TextBlock
        {
            Text = statusLine,
            FontSize = 12,
            Foreground = ResBrush("Br.TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var text = new StackPanel();
        text.Children.Add(titleRow);
        text.Children.Add(caption);

        // Item V4: live per-key counters, straight from the proxy.
        var stat = LocalProxy.Instance.StatOf(key.Id);
        if (stat is not null && stat.Requests + stat.Failures > 0)
        {
            int calls = stat.Requests + stat.Failures;
            text.Children.Add(new TextBlock
            {
                Text = $"Запросов: {stat.Requests} · ошибок: {stat.Failures} · средн. {stat.TotalMs / calls} мс",
                FontSize = 11,
                Foreground = ResBrush("Br.TextSecondary"),
                Margin = new Thickness(0, 3, 0, 0),
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
            });
        }
        else if (_serviceKeyStats.TryGetValue(key.Id, out var serviceStat)
                 && serviceStat.Requests + serviceStat.Failures > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = $"Запросов: {serviceStat.Requests} · ошибок: {serviceStat.Failures} · средн. {serviceStat.AvgMs} мс",
                FontSize = 11,
                Foreground = ResBrush("Br.TextSecondary"),
                Margin = new Thickness(0, 3, 0, 0),
                Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
            });
        }

        text.Margin = new Thickness(10, 0, 12, 0);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (index > 0)
        {
            buttons.Children.Add(RowButton("Сделать главным", key.Id, MakePrimary_Click, key.Label));
        }

        buttons.Children.Add(RowButton("Проверить", key.Id, CheckKey_Click, key.Label));
        buttons.Children.Add(RowButton(key.Enabled ? "Выключить" : "Включить", key.Id, ToggleKey_Click, key.Label));
        buttons.Children.Add(RowButton("Удалить", key.Id, DeleteKey_Click, key.Label));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(grip, 0);
        Grid.SetColumn(dot, 1);
        Grid.SetColumn(text, 2);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(grip);
        grid.Children.Add(dot);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        var border = new Border
        {
            Background = ResBrush("Br.Control"),
            BorderBrush = ResBrush("Br.ControlBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
            Tag = key.Id,
            AllowDrop = true,
        };

        System.Windows.Automation.AutomationProperties.SetName(
            border, $"Ключ {key.Label}, {chipText}");

        border.DragOver += KeyRow_DragOver;
        border.Drop += KeyRow_Drop;
        border.PreviewMouseLeftButtonDown += KeyRow_MouseDown;
        border.MouseMove += KeyRow_MouseMove;

        return border;
    }

    // ---------------------------------------------------------------- drag and drop

    private void KeyRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons keep their own click behaviour; only the row body starts a drag.
        if (sender is FrameworkElement element
            && element.Tag is string id
            && !IsInsideButton(e.OriginalSource))
        {
            _dragKeyId = id;
            _dragStart = e.GetPosition(this);
        }
    }

    private static bool IsInsideButton(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button)
            {
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void KeyRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragKeyId is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(KeyIdFormat, _dragKeyId);
        _dragKeyId = null;
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void KeyRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(KeyIdFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void KeyRow_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border target
            || target.Tag is not string targetId
            || e.Data.GetData(KeyIdFormat) is not string sourceId
            || sourceId == targetId)
        {
            return;
        }

        var keys = App.Settings.Keys;
        var moved = keys.FirstOrDefault(k => k.Id == sourceId);
        int index = keys.FindIndex(k => k.Id == targetId);
        if (moved is null || index < 0)
        {
            return;
        }

        keys.Remove(moved);
        keys.Insert(index, moved);
        SettingsStore.Save(App.Settings);

        Status($"Порядок обновлён — главный ключ «{keys[0].Label}»", Kind.Ok);

        RenderKeys();
        RefreshPoolState();
    }

    private void MakePrimary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        var keys = App.Settings.Keys;
        var move = keys.FirstOrDefault(k => k.Id == id);
        if (move is null)
        {
            return;
        }

        keys.Remove(move);
        keys.Insert(0, move);

        // The main key is tried first, so it must not be parked.
        move.Enabled = true;

        SettingsStore.Save(App.Settings);
        Status($"«{move.Label}» теперь главный ключ", Kind.Ok);

        RenderKeys();
        RefreshPoolState();
    }

    private static Button RowButton(string content, string id, RoutedEventHandler handler, string? subject = null)
    {
        var button = new Button
        {
            Content = content,
            Style = StyleOf("SubtleButton"),
            Tag = id,
            Margin = new Thickness(8, 0, 0, 0),
        };
        button.Click += handler;

        // Item V12: screen readers announce "Проверить — Ключ 1", not a bare verb.
        System.Windows.Automation.AutomationProperties.SetName(
            button,
            string.IsNullOrWhiteSpace(subject) ? content : $"{content} — {subject}");

        return button;
    }

    private async void AddKey_Click(object sender, RoutedEventArgs e)
    {
        string raw = TxtNewKey.Text.Trim();

        if (raw.Length < 20 || !raw.StartsWith("thk_", StringComparison.OrdinalIgnoreCase))
        {
            Status("Ключ должен начинаться с thk_ и быть скопирован целиком", Kind.Fail);
            return;
        }

        if (App.Settings.Keys.Any(k => k.Key == raw))
        {
            TxtNewKey.Text = string.Empty;
            Status("Такой ключ уже добавлен", Kind.Fail);
            return;
        }

        var entry = new ApiKeyEntry
        {
            Key = raw,
            Label = $"Ключ {App.Settings.Keys.Count + 1}",
        };
        App.Settings.Keys.Add(entry);
        SettingsStore.Save(App.Settings);
        TxtNewKey.Text = string.Empty;

        RenderKeys();
        RefreshPoolState();

        Busy(true);
        Status("Проверяю новый ключ...", Kind.Busy);
        var (ok, message) = await KeyProbe.CheckAsync(entry);
        Busy(false);
        Status($"Ключ добавлен: {message}", ok ? Kind.Ok : Kind.Fail);

        RenderKeys();
        RefreshPoolState();
    }

    private async void CheckKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || FindKey(id) is not { } key)
        {
            return;
        }

        Busy(true);
        Status($"Проверяю ключ {key.Masked}...", Kind.Busy);
        var (ok, message) = await KeyProbe.CheckAsync(key);
        Busy(false);
        Status($"{key.Masked}: {message}", ok ? Kind.Ok : Kind.Fail);

        RenderKeys();
        RefreshPoolState();
    }

    private void ToggleKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id || FindKey(id) is not { } key)
        {
            return;
        }

        key.Enabled = !key.Enabled;
        SettingsStore.Save(App.Settings);
        Status(key.Enabled ? "Ключ включён" : "Ключ выключен", Kind.Ok);

        RenderKeys();
        RefreshPoolState();
    }

    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        App.Settings.Keys.RemoveAll(k => k.Id == id);
        SettingsStore.Save(App.Settings);
        Status("Ключ удалён", Kind.Ok);

        RenderKeys();
        RefreshPoolState();
    }

    private async void ApplyPool_Click(object sender, RoutedEventArgs e) =>
        await ApplyPoolAsync(inform: true);

    /// <summary>Item 7: one button applies the pool address and restarts OpenCode right away.</summary>
    private async void ApplyPoolRestart_Click(object sender, RoutedEventArgs e)
    {
        if (!await ApplyPoolAsync(inform: false))
        {
            return;
        }

        if (Launcher.OpenCodeRunningCount() > 0)
        {
            var answer = MessageBox.Show(
                "OpenCode будет закрыт и запущен заново, чтобы прочитать адрес пула ключей." + Environment.NewLine + Environment.NewLine +
                "Текущий диалог агента закроется. Продолжить?",
                "TokenHub",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
            {
                Status("Адрес применён — перезапустите OpenCode вручную", Kind.Ok);
                return;
            }
        }

        try
        {
            Launcher.RestartOpenCode();
            Status("OpenCode перезапущен — можно работать", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось перезапустить OpenCode: " + ex.Message, Kind.Fail);
        }
    }

    private async Task<bool> ApplyPoolAsync(bool inform)
    {
        try
        {
            if (!LocalProxy.Instance.Running && PoolService.Current != PoolService.State.Running)
            {
                await LocalProxy.Instance.StartAsync();
            }

            if (!LocalProxy.Instance.Running && PoolService.Current != PoolService.State.Running)
            {
                Status("Локальный прокси не запустился: " + LocalProxy.Instance.LastError, Kind.Fail);
                return false;
            }

            App.Settings.UsePool = true;
            SettingsStore.Save(App.Settings);

            string? key = App.Settings.FirstAvailableKey?.Key
                          ?? App.Settings.Keys.FirstOrDefault()?.Key;
            string? backup = OpenCodeConfig.SetProvider(App.Settings.LocalBaseUrl, key);
            string backupLine = backup is null
                ? "Создан новый конфиг OpenCode."
                : $"Бэкап конфига: {Path.GetFileName(backup)}";

            RefreshModeText();
            RefreshPoolState();

            if (inform)
            {
                MessageBox.Show(
                    "OpenCode переключён на пул ключей:" + Environment.NewLine +
                    App.Settings.LocalBaseUrl + Environment.NewLine + Environment.NewLine +
                    "Нужен один перезапуск OpenCode, чтобы он прочитал новый адрес. Дальше ключи можно " +
                    "добавлять, выключать и удалять на ходу — OpenCode будет незаметно брать следующий " +
                    "рабочий ключ, и диалог не прервётся." + Environment.NewLine + Environment.NewLine +
                    backupLine,
                    "TokenHub",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Status("OpenCode переключён на пул ключей", Kind.Ok);
            return true;
        }
        catch (Exception ex)
        {
            Status("Не удалось применить пул: " + ex.Message, Kind.Fail);
            return false;
        }
    }

    // ---------------------------------------------------------------- key checks

    private void CheckAllKeys_Click(object sender, RoutedEventArgs e) => _ = CheckAllKeysAsync();

    /// <summary>Item 4: sweeps every key and refreshes its state.</summary>
    private async Task CheckAllKeysAsync()
    {
        var settings = App.Settings;
        if (settings.Keys.Count == 0)
        {
            Status("Ключей нет — сначала добавьте ключ", Kind.Fail);
            return;
        }

        Busy(true);
        Status("Проверяю все ключи...", Kind.Busy);

        foreach (var key in settings.Keys.ToList())
        {
            await KeyProbe.CheckAsync(key);
        }

        Busy(false);
        RenderKeys();
        RefreshPoolState();

        // The sweep writes its own state; pull the pool's authoritative view so the
        // summary matches the chips (a /models 200 does not mean the chat quota is back).
        await RefreshServiceStatusAsync();

        int ok = settings.Keys.Count(k => k.Available);
        string summary = ok == settings.Keys.Count
            ? $"Проверено ключей: {settings.Keys.Count}. Все готовы к работе"
            : $"Проверено ключей: {settings.Keys.Count}, доступных сейчас: {ok}";
        Status(summary, ok > 0 ? Kind.Ok : Kind.Fail);
    }

    // ---------------------------------------------------------------- export / import

    private void ExportKeys_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings;
        if (settings.Keys.Count == 0)
        {
            Status("Нечего экспортировать — ключей нет", Kind.Fail);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Экспорт ключей Token Harbor",
            FileName = $"tokenhub-keys-{DateTime.Now:yyyyMMdd}.json",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            DefaultExt = ".json",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bundle = new KeyBundle
            {
                ExportedUtc = DateTime.UtcNow,
                Keys = settings.Keys.Select(k => new KeyRecord
                {
                    Key = k.Key,
                    Label = k.Label,
                    Enabled = k.Enabled,
                }).ToList(),
            };

            File.WriteAllText(
                dialog.FileName,
                JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true }));

            Status($"Экспортировано ключей: {bundle.Keys.Count}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось экспортировать: " + ex.Message, Kind.Fail);
        }
    }

    private void ImportKeys_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт ключей Token Harbor",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var bundle = JsonSerializer.Deserialize<KeyBundle>(File.ReadAllText(dialog.FileName));
            if (bundle?.Keys is null || bundle.Keys.Count == 0)
            {
                Status("В файле нет ключей", Kind.Fail);
                return;
            }

            int added = 0;
            foreach (var record in bundle.Keys)
            {
                string value = (record.Key ?? string.Empty).Trim();
                if (value.Length < 20 || !value.StartsWith("thk_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (App.Settings.Keys.Any(k => k.Key == value))
                {
                    continue;
                }

                App.Settings.Keys.Add(new ApiKeyEntry
                {
                    Key = value,
                    Label = string.IsNullOrWhiteSpace(record.Label)
                        ? $"Ключ {App.Settings.Keys.Count + 1}"
                        : record.Label.Trim(),
                    Enabled = record.Enabled,
                });
                added++;
            }

            SettingsStore.Save(App.Settings);
            RenderKeys();
            RefreshPoolState();

            Status(added > 0 ? $"Импортировано ключей: {added}" : "Новых ключей не найдено",
                added > 0 ? Kind.Ok : Kind.Fail);
        }
        catch (Exception ex)
        {
            Status("Не удалось импортировать: " + ex.Message, Kind.Fail);
        }
    }

    // ---------------------------------------------------------------- skills

    private void OpenWizard_Click(object sender, RoutedEventArgs e) => OpenWizard();

    /// <summary>Opens the setup wizard, also used on the very first run.</summary>
    private void OpenWizard()
    {
        var wizard = new FirstRunWindow { Owner = this };
        if (wizard.ShowDialog() != true)
        {
            RefreshModeText();
            return;
        }

        RefreshAllFromSettings();
        RenderSkills();
        _ = CompleteSetupAsync(wizard.SkillsInstalled);
    }

    /// <summary>
    /// Finishes the wizard: brings the pool up, points OpenCode at it and offers the one
    /// restart OpenCode needs to read the new config.
    /// </summary>
    private async Task CompleteSetupAsync(int skillsInstalled)
    {
        bool connected = await ApplyPoolAsync(inform: false);
        string skills = skillsInstalled > 0 ? $" Скиллов установлено: {skillsInstalled}." : string.Empty;

        if (!connected)
        {
            MessageBox.Show(
                this,
                "Настройки сохранены, но подключить пул не удалось — подробности в строке состояния." +
                Environment.NewLine + Environment.NewLine +
                "Попробуйте кнопку «Применить в OpenCode» в разделе «Ключи»." + skills,
                "TokenHub", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Launcher.OpenCodeRunningCount() == 0)
        {
            MessageBox.Show(
                this,
                "Готово: OpenCode настроен на пул ключей." + skills +
                Environment.NewLine + Environment.NewLine +
                "Запустить OpenCode можно кнопкой на «Главной».",
                "TokenHub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Готово: OpenCode настроен на пул ключей." + skills +
            Environment.NewLine + Environment.NewLine +
            "Перезапустить OpenCode сейчас, чтобы он прочитал новый конфиг? Текущий диалог закроется.",
            "TokenHub", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            Status("OpenCode настроен — перезапустите его вручную", Kind.Ok);
            return;
        }

        try
        {
            Launcher.RestartOpenCode();
            Status("OpenCode перезапущен — можно работать", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось перезапустить OpenCode: " + ex.Message, Kind.Fail);
        }
    }

    /// <summary>Draws the shipped skill collection as selectable chips.</summary>
    private void RenderSkills()
    {
        if (SkillsList is null)
        {
            return;
        }

        _skillChips.Clear();
        SkillsList.Children.Clear();

        string filter = TxtSkillSearch?.Text?.Trim() ?? string.Empty;

        foreach (var skill in Skills.Catalog)
        {
            if (filter.Length > 0
                && !skill.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !skill.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A trailing dot marks an installed skill without needing a second control.
            string label = Skills.IsInstalled(skill.Name) ? skill.Name + " ·" : skill.Name;

            var chip = new ToggleButton
            {
                Style = (Style)FindResource("SkillChip"),
                Content = label,
                Tag = skill.Name,
                ToolTip = string.IsNullOrEmpty(skill.Description)
                    ? skill.Name
                    : $"{skill.Name}{Environment.NewLine}{Environment.NewLine}{skill.Description}",
            };

            chip.Checked += (_, _) => UpdateSkillsHint();
            chip.Unchecked += (_, _) => UpdateSkillsHint();

            _skillChips[skill.Name] = chip;
            SkillsList.Children.Add(chip);
        }

        RefreshSkillsState();
        UpdateSkillsHint();
    }

    private void RefreshSkillsState()
    {
        if (TxtSkillsState is null)
        {
            return;
        }

        TxtSkillsState.Text = Skills.Catalog.Count == 0
            ? "В этой сборке нет набора скиллов."
            : $"Скиллов в наборе: {Skills.Catalog.Count} · уже установлено: {Skills.InstalledCount}";
        TxtSkillsPath.Text = Skills.Folder;
    }

    private void UpdateSkillsHint()
    {
        if (TxtSkillsHint is null)
        {
            return;
        }

        if (_skillChips.Count == 0)
        {
            TxtSkillsHint.Text = Skills.Catalog.Count == 0 ? string.Empty : "Ничего не найдено.";
            return;
        }

        int selected = _skillChips.Count(pair => pair.Value.IsChecked == true);
        TxtSkillsHint.Text = $"Выбрано: {selected} из {_skillChips.Count}. Точка после названия — скилл уже установлен.";
    }

    private void SkillSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RenderSkills();
    }

    private void InstallAllSkills_Click(object sender, RoutedEventArgs e) =>
        InstallSkills(Skills.Catalog.Select(skill => skill.Name), "все");

    private void InstallSelectedSkills_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _skillChips
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToList();

        if (chosen.Count == 0)
        {
            Status("Сначала выберите хотя бы один скилл", Kind.Fail);
            return;
        }

        InstallSkills(chosen, "выбранные");
    }

    private void InstallSkills(IEnumerable<string> names, string what)
    {
        try
        {
            Busy(true);
            int copied = Skills.Install(names);
            Busy(false);

            RenderSkills();
            Journal($"Скиллы ({what}): установлено {copied} в {Skills.Folder}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Busy(false);
            Status("Не удалось установить скиллы: " + ex.Message, Kind.Fail);
        }
    }

    private void OpenSkillsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Skills.Folder);
            Launcher.OpenFolder(Skills.Folder);
            Status("Открываю папку скиллов", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status(ex.Message, Kind.Fail);
        }
    }

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

/// <summary>Shape of an exported key file, so a bundle can be typed instead of anonymous.</summary>
internal sealed class KeyRecord
{
    public string? Key { get; set; }

    public string? Label { get; set; }

    public bool Enabled { get; set; } = true;
}

internal sealed class KeyBundle
{
    public int Version { get; set; } = 1;

    public DateTime ExportedUtc { get; set; }

    public List<KeyRecord> Keys { get; set; } = new();
}
