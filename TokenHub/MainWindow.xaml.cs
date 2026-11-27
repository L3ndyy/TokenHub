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
    private bool _trayHintShown;
    private string? _dragKeyId;
    private Point _dragStart;
    private string _lastEvent = string.Empty;
    private long _serviceRequests;
    private long _serviceFailovers;
    private DispatcherTimer? _liveTimer;
    private readonly Dictionary<string, (int Requests, int Failures, long AvgMs)> _serviceKeyStats = new();
    private int _skillsShown;
    private readonly HashSet<string> _openSkillGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openSkills = new(StringComparer.Ordinal);
    private static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private string _serviceHealth = string.Empty;
    private UpdateInfo? _pendingUpdate;

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

        FillModels(settings.DefaultModel);
        TglFreeOnly.IsChecked = settings.FreeOnly;

        ThemeSystem.IsChecked = settings.Theme == "system";
        ThemeDark.IsChecked = settings.Theme == "dark";
        ThemeLight.IsChecked = settings.Theme == "light";

        MarkAccent();

        StartLogoBreath();

        ApplyCompactMode();

        SettingsPaths.Text =
            $"TokenHub:   {SettingsStore.FilePath}{Environment.NewLine}" +
            $"OpenCode:   {OpenCodeConfig.FilePath}{Environment.NewLine}" +
            $"Версия:     {Updater.CurrentVersion} (API-прокси и лаунчер)";

        _loading = false;

        RefreshLocalState();
        RefreshServiceState();
        RenderProfiles();
        RefreshStats();
        StartLiveStats();
        _ = RefreshServiceStatusAsync();
        _ = StartupChecksAsync();
        OpenWizardIfNeeded();

        // A settings file that had to fall back to its backup is important enough to
        // say out loud; the log line alone would be easy to miss.
        if (!string.IsNullOrEmpty(SettingsStore.LastLoadError))
        {
            Journal(SettingsStore.LastLoadError, Kind.Fail);
        }

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
        if (App.StartMinimized || App.Settings.StartMinimized)
        {
            return;
        }

        bool needsSetup = !App.Settings.FirstRunDone && OpenCodeConfig.NeedsSetup;

        // The tour is shown once to everyone, even when OpenCode is already configured;
        // the wizard only follows when there is actually something to configure.
        if (App.Settings.IntroSeen && !needsSetup)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            bool goFurther = true;

            if (!App.Settings.IntroSeen)
            {
                var intro = new IntroWindow { Owner = this };
                intro.ShowDialog();
                App.Settings.IntroSeen = true;
                SettingsStore.Save(App.Settings);
                goFurther = intro.GoFurther;
            }

            if (needsSetup && goFurther)
            {
                OpenWizard();
            }
        }));
    }

    private void ShowIntro_Click(object sender, RoutedEventArgs e) =>
        ShowIntro(thenWizard: false);

    private void OpenTokenHarbor_Click(object sender, RoutedEventArgs e) =>
        Launcher.OpenUrl("https://tokenharbor.ai");

    private void ShowIntro(bool thenWizard)
    {
        var intro = new IntroWindow { Owner = this };
        intro.ShowDialog();

        if (!App.Settings.IntroSeen)
        {
            App.Settings.IntroSeen = true;
            SettingsStore.Save(App.Settings);
        }

        if (thenWizard || intro.GoFurther)
        {
            OpenWizard();
        }
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

    /// <summary>
    /// A very slow colour drift on the logo tile. It is the only decorative animation in
    /// the window and it disappears when Windows asks for reduced motion.
    /// </summary>
    private void StartLogoBreath()
    {
        if (!AnimationsEnabled || LogoTile is null)
        {
            return;
        }

        var breath = new DoubleAnimation
        {
            From = 1.0,
            To = 0.85,
            Duration = TimeSpan.FromSeconds(5),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        LogoTile.BeginAnimation(UIElement.OpacityProperty, breath);
    }

    private void RefreshStats()
    {
        var proxy = LocalProxy.Instance;

        // One number first: what the week would have cost on the paid equivalent of the
        // current model. It is an estimate, so the caption says so.
        var week = UsageStore.Recent(7);
        long prompt = week.Sum(d => d.Prompt);
        long completion = week.Sum(d => d.Completion);
        long tokens = prompt + completion;
        int answers = week.Sum(d => d.Responses);
        long switches = proxy.Running ? proxy.Failovers : _serviceFailovers;

        if (TxtSaved is not null)
        {
            TxtSaved.Text = Format.Rub(Pricing.Rub(prompt, completion, App.Settings.DefaultModel));
        }

        if (TxtStatRequests is not null)
        {
            TxtStatRequests.Text = Format.Compact(answers);
            TxtStatRequestsLabel.Text = Format.Plural(
                answers, "запрос за неделю", "запроса за неделю", "запросов за неделю");
            TxtStatTokens.Text = Format.Compact(tokens);
            TxtStatTokensLabel.Text = Format.Plural(
                (int)Math.Min(tokens, int.MaxValue),
                "токен за неделю", "токена за неделю", "токенов за неделю");
            TxtStatSwitches.Text = Format.Compact(switches);
            TxtStatSwitchesLabel.Text = Format.Plural(
                (int)Math.Min(switches, int.MaxValue), "переключение", "переключения", "переключений");
        }

        DrawHistory(week);

        var sb = new System.Text.StringBuilder();
        var today = UsageStore.Today;

        if (answers == 0)
        {
            sb.AppendLine("Пока нет ответов за неделю. Сделайте запрос в OpenCode — цифры появятся здесь.");
        }
        else
        {
            sb.AppendLine(
                $"Сегодня: вход {Format.Compact(today.Prompt)} · выход {Format.Compact(today.Completion)}"
                + $" ({today.Responses} {Format.Plural(today.Responses, "ответ", "ответа", "ответов")})");
        }

        if (proxy.Running)
        {
            sb.AppendLine($"Стратегия: {StrategyName(App.Settings.KeyStrategy)} · за сессию {proxy.Requests} запросов");

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
            sb.AppendLine("Запросы обслуживает служба Windows — окно только показывает счётчики.");
        }
        else
        {
            sb.AppendLine("Локальный прокси не запущен.");
        }

        TxtPoolStats.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Bars for the last seven days. Bars are tokens per day, today is accent-coloured,
    /// and the weekday is printed under each one so the chart reads without a legend.
    /// </summary>
    private void DrawHistory(IReadOnlyList<(DateTime Day, long Prompt, long Completion, int Responses)> week)
    {
        if (HistoryCanvas is null)
        {
            return;
        }

        HistoryCanvas.Children.Clear();

        double width = HistoryCanvas.ActualWidth;
        double height = HistoryCanvas.ActualHeight;
        if (width <= 20 || height <= 20 || week.Count == 0)
        {
            return;
        }

        const double labelHeight = 16;
        double plotHeight = height - labelHeight;
        double slot = width / week.Count;
        double barWidth = Math.Max(4, Math.Min(26, slot - 8));
        double peak = Math.Max(1, week.Max(d => d.Prompt + d.Completion));
        var accent = ResBrush("Br.Accent");
        var idle = ResBrush("Br.Muted");
        var labelBrush = ResBrush("Br.TextSecondary");

        for (int i = 0; i < week.Count; i++)
        {
            var day = week[i];
            long value = day.Prompt + day.Completion;
            bool isToday = day.Day.Date == DateTime.Today;
            double barHeight = value <= 0 ? 2 : Math.Max(3, value / peak * (plotHeight - 8));
            double x = slot * i + (slot - barWidth) / 2;

            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = barHeight,
                RadiusX = 3,
                RadiusY = 3,
                Fill = value <= 0 ? idle : accent,
                Opacity = isToday ? 1.0 : 0.55,
                ToolTip = value <= 0
                    ? $"{day.Day:dd.MM}: нет данных"
                    : $"{day.Day:dd.MM}: {Format.Compact(value)} токенов, {day.Responses} ответов",
            };
            Canvas.SetLeft(bar, x);
            Canvas.SetTop(bar, plotHeight - barHeight);
            HistoryCanvas.Children.Add(bar);

            var label = new TextBlock
            {
                Text = DayName(day.Day),
                FontSize = 10,
                Foreground = isToday ? ResBrush("Br.Text") : labelBrush,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Width = slot,
                TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(label, slot * i);
            Canvas.SetTop(label, plotHeight + 2);
            HistoryCanvas.Children.Add(label);
        }
    }

    private static string DayName(DateTime day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday => "пн",
        DayOfWeek.Tuesday => "вт",
        DayOfWeek.Wednesday => "ср",
        DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт",
        DayOfWeek.Saturday => "сб",
        _ => "вс",
    };

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

            if (!LocalProxy.Instance.Running)
            {
                await RefreshServiceStatusAsync();
            }

            RefreshStats();
        };
        _liveTimer.Start();
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
            _serviceFailovers = fail;

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
        if (MessageBox.Show(
                this,
                "Сбросить статистику? Обнулятся сегодняшние счётчики и история по дням — график за неделю начнётся заново. Ключи и настройки не затрагиваются.",
                "TokenHub",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        LocalProxy.Instance.ResetStats();
        UsageStore.Reset();
        RefreshStats();
        Journal("Счётчики статистики сброшены (сегодня и история по дням)", Kind.Ok);
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

    // ---------------------------------------------------------------- pool behaviour (T6, T16)

    /// <summary>Fills the model picker; keeps a custom model visible instead of dropping it.</summary>
    private void FillModels(string current)
    {
        var models = OpenCodeConfig.KnownModels().ToList();
        string id = current.StartsWith("tokenharbor/", StringComparison.OrdinalIgnoreCase)
            ? current["tokenharbor/".Length..]
            : current;

        if (!string.IsNullOrWhiteSpace(id) && !models.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            models.Insert(0, id);
        }

        CmbModel.ItemsSource = models;
        CmbModel.SelectedItem = models.FirstOrDefault(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase))
                                ?? models.FirstOrDefault();

        if (CmbProfileModel is not null)
        {
            object? chosen = CmbProfileModel.SelectedItem;
            CmbProfileModel.ItemsSource = models;
            CmbProfileModel.SelectedItem = chosen is string text && models.Contains(text)
                ? text
                : CmbModel.SelectedItem;
        }
    }

    private void Model_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbModel.SelectedItem is not string model)
        {
            return;
        }

        App.Settings.DefaultModel = "tokenharbor/" + model;
        SettingsStore.Save(App.Settings);

        // Only touch opencode.json when the pool actually drives it.
        if (App.Settings.UsePool && OpenCodeConfig.Exists)
        {
            try
            {
                OpenCodeConfig.SetProvider(App.Settings.LocalBaseUrl, App.Settings.ProxyToken, App.Settings.DefaultModel);
            }
            catch (Exception ex)
            {
                Journal("Не удалось записать модель в OpenCode: " + ex.Message, Kind.Fail);
                return;
            }
        }

        Journal($"Модель по умолчанию: {model}", Kind.Ok);
    }

    // ---------------------------------------------------------------- project profiles

    private void RenderProfiles()
    {
        if (ProfilesList is null)
        {
            return;
        }

        ProfilesList.Children.Clear();
        var profiles = App.Settings.Profiles ??= new List<ProjectProfile>();

        if (profiles.Count == 0)
        {
            ProfilesList.Children.Add(new TextBlock
            {
                Text = "Профилей нет. Выберите модель и добавьте папку проекта — TokenHub запомнит её.",
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });
            return;
        }

        foreach (var profile in profiles.ToList())
        {
            ProfilesList.Children.Add(BuildProfileRow(profile));
        }
    }

    private FrameworkElement BuildProfileRow(ProjectProfile profile)
    {
        string folder = Path.GetFileName(
            profile.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(folder) ? profile.Path : folder,
            Style = (Style)FindResource("CardTitle"),
            FontSize = 13,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{profile.Model} · {profile.Path}",
            Style = (Style)FindResource("Caption"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 2, 12, 0),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var apply = new Button
        {
            Content = "Применить",
            Style = (Style)FindResource("SubtleButton"),
            Tag = profile.Path,
            Margin = new Thickness(0, 0, 6, 0),
        };
        AutomationProperties.SetName(apply, "Применить профиль к " + folder);
        apply.Click += ApplyProfile_Click;
        buttons.Children.Add(apply);

        var remove = new Button
        {
            Content = "Убрать",
            Style = (Style)FindResource("SubtleButton"),
            Tag = profile.Path,
        };
        AutomationProperties.SetName(remove, "Убрать профиль " + folder);
        remove.Click += RemoveProfile_Click;
        buttons.Children.Add(remove);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(text);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = ResBrush("Br.Control"),
            BorderBrush = ResBrush("Br.ControlBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
        };
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка проекта для профиля",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string path = dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar);
        var profiles = App.Settings.Profiles ??= new List<ProjectProfile>();

        if (profiles.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Status("Профиль для этой папки уже есть.", Kind.Unknown);
            return;
        }

        string model = CmbProfileModel.SelectedItem as string ?? App.Settings.DefaultModel;
        if (!model.StartsWith("tokenharbor/", StringComparison.OrdinalIgnoreCase))
        {
            model = "tokenharbor/" + model;
        }

        profiles.Add(new ProjectProfile { Path = path, Model = model });
        SettingsStore.Save(App.Settings);
        RenderProfiles();
        Status($"Профиль добавлен: {Path.GetFileName(path)} → {model}", Kind.Ok);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
        {
            return;
        }

        App.Settings.Profiles?.RemoveAll(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        SettingsStore.Save(App.Settings);
        RenderProfiles();
        Status("Профиль убран.", Kind.Ok);
    }

    private void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string path)
        {
            return;
        }

        var profile = App.Settings.Profiles?
            .FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            return;
        }

        try
        {
            string file = Path.Combine(path, "opencode.json");
            System.Text.Json.Nodes.JsonNode root = File.Exists(file)
                ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) ?? new System.Text.Json.Nodes.JsonObject()
                : new System.Text.Json.Nodes.JsonObject();

            if (root is not System.Text.Json.Nodes.JsonObject obj)
            {
                Status("opencode.json в проекте имеет неожиданный формат — не трогаю.", Kind.Fail);
                return;
            }

            obj["$schema"] ??= "https://opencode.ai/config.json";
            obj["model"] = profile.Model;

            File.WriteAllText(
                file,
                obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Journal($"Профиль {Path.GetFileName(path)}: модель {profile.Model}", Kind.Ok);
            Status($"Профиль применён: {Path.GetFileName(path)} → {profile.Model}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Status("Не удалось применить профиль: " + ex.Message, Kind.Fail);
        }
    }

    private void FreeOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.FreeOnly = TglFreeOnly.IsChecked == true;
        SettingsStore.Save(App.Settings);
        Journal(
            App.Settings.FreeOnly ? "Включены только бесплатные модели" : "Разрешены все модели",
            Kind.Ok);
    }

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
        await CheckUpdateSilentlyAsync();
    }

    /// <summary>
    /// One quiet look at GitHub on start-up. A failed check stays quiet — the button in
    /// "Быстрые действия" is there for anyone who wants to see the error.
    /// </summary>
    private async Task CheckUpdateSilentlyAsync()
    {
        if (App.StartMinimized)
        {
            return;
        }

        var (ok, newer, info, _) = await Updater.CheckAsync();
        if (!ok || !newer || info is null)
        {
            return;
        }

        _pendingUpdate = info;
        TxtUpdateTitle.Text = $"Доступна версия {info.Version}";
        TxtUpdateDetail.Text = $"У вас {Updater.CurrentVersion}. Установщик скачается и запустится сам.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    private async void UpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        await InstallUpdateAsync(_pendingUpdate);
    }

    /// <summary>Downloads the installer and starts it; the app exits so files can be replaced.</summary>
    private async Task InstallUpdateAsync(UpdateInfo info)
    {
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

        // Only the first trip to the tray says anything — the balloon on every close/minimize
        // gets in the way once the user knows where the window went.
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;

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
                // The window is a background tool: "свернуть" tucks it into the tray, so the
                // key pool keeps running without a taskbar button in the way.
                HideToTray();
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
        PanelMemory.Visibility = tag == "memory" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // Item V7: a short fade between panels, skipped when Windows disables animations.
        UIElement? shown = tag switch
        {
            "launch" => PanelLaunch,
            "network" => PanelNetwork,
            "keys" => PanelKeys,
            "skills" => PanelSkills,
            "memory" => PanelMemory,
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

        if (tag == "memory")
        {
            App.TryWireMemory();
            RenderMemory();
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

    private void Compact_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        App.Settings.CompactMode = TglCompact.IsChecked == true;
        SettingsStore.Save(App.Settings);
        ApplyCompactMode();
    }

    /// <summary>
    /// Compact mode is spacing only: nav rows shrink and the pages lose their outer
    /// margins, so more of the card list fits without touching typography.
    /// </summary>
    private void ApplyCompactMode()
    {
        if (NavRail is null)
        {
            return;
        }

        bool compact = App.Settings.CompactMode;
        TglCompact.IsChecked = compact;

        double navHeight = compact ? 34 : 44;
        foreach (var item in NavRail.Children.OfType<RadioButton>())
        {
            item.Height = navHeight;
        }

        var margin = compact ? new Thickness(14, 4, 0, 0) : new Thickness(18, 10, 0, 0);
        foreach (var panel in new FrameworkElement[]
                 {
                     PanelHome, PanelLaunch, PanelNetwork, PanelKeys,
                     PanelSkills, PanelMemory, PanelSettings,
                 })
        {
            panel.Margin = margin;
        }
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
            TxtKeysEmpty.Text = "Ключей пока нет — добавьте первый ключ Token Harbor, и пул заработает.";
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
                CornerRadius = new CornerRadius(6),
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
            CornerRadius = new CornerRadius(6),
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
        if (key.ExhaustedUntilUtc is DateTime pausedUntil && pausedUntil > DateTime.UtcNow)
        {
            statusLine += $" · пауза до {pausedUntil.ToLocalTime():HH:mm}";
        }
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
            buttons.Children.Add(IconRowButton("\uE718", "Сделать главным", key.Id, MakePrimary_Click, key.Label));
        }

        buttons.Children.Add(IconRowButton("\uE72C", "Проверить", key.Id, CheckKey_Click, key.Label));
        buttons.Children.Add(key.Enabled
            ? IconRowButton("\uE769", "Выключить", key.Id, ToggleKey_Click, key.Label)
            : IconRowButton("\uE768", "Включить", key.Id, ToggleKey_Click, key.Label));
        buttons.Children.Add(IconRowButton("\uE74D", "Удалить", key.Id, DeleteKey_Click, key.Label, danger: true));

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
            CornerRadius = new CornerRadius(8),
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

    /// <summary>
    /// One compact icon action in a list row. The tooltip carries the action name, so the
    /// button stays 30px wide; screen readers get the same text through the name.
    /// </summary>
    private static Button IconRowButton(
        string glyph,
        string action,
        string id,
        RoutedEventHandler handler,
        string? subject = null,
        bool danger = false)
    {
        string label = string.IsNullOrWhiteSpace(subject) ? action : $"{action} — {subject}";

        var button = new Button
        {
            Content = glyph,
            Style = StyleOf(danger ? "RowIconDanger" : "RowIconButton"),
            Tag = id,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = label,
        };
        button.Click += handler;

        // Item V12: screen readers announce "Проверить — Ключ 1", not a bare glyph.
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        ToolTipService.SetInitialShowDelay(button, 300);
        ToolTipService.SetShowDuration(button, 12000);

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

            App.Settings.EnsureProxyToken();
            App.Settings.UsePool = true;
            SettingsStore.Save(App.Settings);

            string? backup = OpenCodeConfig.SetProvider(
                App.Settings.LocalBaseUrl,
                App.Settings.ProxyToken,
                App.Settings.DefaultModel);
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
        if (settings.Keys.Count == 0 && (settings.Profiles?.Count ?? 0) == 0)
        {
            Status("Нечего экспортировать — ключей и профилей нет", Kind.Fail);
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
                Profiles = (settings.Profiles ?? new List<ProjectProfile>())
                    .Select(p => new ProjectProfile { Path = p.Path, Model = p.Model })
                    .ToList(),
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
            bool hasKeys = bundle?.Keys is { Count: > 0 };
            bool hasProfiles = bundle?.Profiles is { Count: > 0 };
            if (!hasKeys && !hasProfiles)
            {
                Status("В файле нет ни ключей, ни профилей", Kind.Fail);
                return;
            }

            int added = 0;
            foreach (var record in bundle!.Keys ?? new List<KeyRecord>())
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

            int profilesAdded = 0;
            var profiles = App.Settings.Profiles ??= new List<ProjectProfile>();
            foreach (var profile in bundle.Profiles ?? new List<ProjectProfile>())
            {
                string path = (profile?.Path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar);
                if (path.Length == 0 || string.IsNullOrWhiteSpace(profile!.Model))
                {
                    continue;
                }

                if (profiles.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                profiles.Add(new ProjectProfile { Path = path, Model = profile.Model.Trim() });
                profilesAdded++;
            }

            SettingsStore.Save(App.Settings);
            RenderKeys();
            RefreshPoolState();
            RenderProfiles();

            string keysText = added > 0 ? $"Импортировано ключей: {added}" : "Новых ключей не найдено";
            string profilesText = profilesAdded > 0 ? $", профилей: {profilesAdded}" : string.Empty;

            Status(keysText + profilesText, added > 0 || profilesAdded > 0 ? Kind.Ok : Kind.Fail);
        }
        catch (Exception ex)
        {
            Status("Не удалось импортировать: " + ex.Message, Kind.Fail);
        }
    }

    // ---------------------------------------------------------------- skills

    private void OpenWizard_Click(object sender, RoutedEventArgs e) => OpenWizard();

    /// <summary>
    /// The "one button" setup: files, pool credential, Windows service (one UAC prompt)
    /// and the OpenCode config, in that order. Each step is skipped when already done.
    /// </summary>
    private async void SetupEverything_Click(object sender, RoutedEventArgs e)
    {
        if (App.Settings.Keys.Count == 0)
        {
            Status("Сначала добавьте ключ — откроется мастер", Kind.Unknown);
            OpenWizard();
            return;
        }

        Busy(true);
        Status("Настраиваю TokenHub и OpenCode…", Kind.Busy);

        try
        {
            App.Settings.EnsureProxyToken();
            App.Settings.UsePool = true;
            SettingsStore.Save(App.Settings);

            // The service keeps the pool alive with the window closed; without its
            // binaries the in-app listener does the same job for this session.
            if (PoolService.PayloadReady)
            {
                switch (PoolService.Current)
                {
                    case PoolService.State.NotInstalled:
                        await RunServiceAsync(PoolService.Install, "Устанавливаю службу", PoolService.State.Running);
                        break;
                    case PoolService.State.Stopped:
                        await RunServiceAsync(PoolService.StartService, "Запускаю службу", PoolService.State.Running);
                        break;
                    case PoolService.State.Running when PoolService.PayloadOutdated:
                        await RunServiceAsync(PoolService.Update, "Обновляю файлы службы", PoolService.State.Running);
                        break;
                }
            }

            if (PoolService.Current != PoolService.State.Running && !LocalProxy.Instance.Running)
            {
                await LocalProxy.Instance.StartAsync();
            }

            OpenCodeConfig.SetProvider(
                App.Settings.LocalBaseUrl,
                App.Settings.ProxyToken,
                App.Settings.DefaultModel);

            EventLog.Write("Одна кнопка: OpenCode подключён к пулу, служба — " + PoolService.Describe());

            RefreshAllFromSettings();
            RenderSkills();
            RefreshServiceState();
            Busy(false);

            string proxy = PoolService.Current == PoolService.State.Running
                ? "служба Windows"
                : LocalProxy.Instance.Running ? "само приложение" : "не запущен";

            Status($"Готово: OpenCode подключён, пул — {proxy}", Kind.Ok);

            MessageBox.Show(
                this,
                "Готово. Что сделано:" + Environment.NewLine +
                $"• файлы настроек: {SettingsStore.Directory}" + Environment.NewLine +
                $"• пул ключей: {proxy}" + Environment.NewLine +
                $"• OpenCode: {App.Settings.LocalBaseUrl} (модель {App.Settings.DefaultModel})" +
                Environment.NewLine + Environment.NewLine +
                (Launcher.OpenCodeRunningCount() > 0
                    ? "Перезапустите OpenCode, чтобы он прочитал новый конфиг."
                    : "Можно запускать OpenCode — он уже смотрит на пул."),
                "TokenHub",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Busy(false);
            Status("Не удалось настроить всё: " + ex.Message, Kind.Fail);
            EventLog.Write("Одна кнопка: " + ex);
        }
    }

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

    /// <summary>
    /// Draws the shipped skill collection grouped by purpose. The buckets start closed so the
    /// page is not a wall of 158 rows; opening one then opening a name reveals its description
    /// and install button. A search bypasses the folding and shows what it found.
    /// </summary>
    private void RenderSkills()
    {
        if (SkillsGroups is null)
        {
            return;
        }

        SkillsGroups.Children.Clear();

        string filter = TxtSkillSearch?.Text?.Trim() ?? string.Empty;
        bool favoriteOnly = TglSkillFav?.IsChecked == true;
        var favorites = App.Settings.FavoriteSkills ?? new List<string>();

        var visible = Skills.Catalog
            .Where(skill => filter.Length == 0
                            || skill.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                            || skill.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Where(skill => !favoriteOnly || favorites.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        _skillsShown = visible.Count;

        var byGroup = visible
            .GroupBy(Skills.GroupFor)
            .ToDictionary(group => group.Key.Name, group => group.ToList());

        foreach (var group in Skills.Groups)
        {
            if (!byGroup.TryGetValue(group.Name, out var items) || items.Count == 0)
            {
                continue;
            }

            bool open = filter.Length > 0 || _openSkillGroups.Contains(group.Name);
            SkillsGroups.Children.Add(SkillGroupSection(group, items, open));
        }

        if (_skillsShown == 0)
        {
            SkillsGroups.Children.Add(new TextBlock
            {
                Text = Skills.Catalog.Count == 0
                    ? "В этой сборке нет набора скиллов."
                    : favoriteOnly && favorites.Count == 0
                        ? "В избранном пока пусто — нажмите звёздочку у названия скилла."
                        : "Ничего не найдено.",
                Style = (Style)FindResource("Caption"),
            });
        }

        RefreshSkillsState();
        UpdateSkillsHint();
    }

    /// <summary>Shows or hides a secondary row of actions so the cards stay calm.</summary>
    private void ToggleMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag || FindName(tag) is not Panel panel)
        {
            return;
        }

        bool open = panel.Visibility != Visibility.Visible;
        panel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (FindName("Txt" + tag) is TextBlock label)
        {
            label.Text = open ? "Свернуть" : "Ещё";
        }

        if (FindName("Ico" + tag) is TextBlock icon)
        {
            icon.Text = open ? "\uE70E" : "\uE70D";
        }

        AutomationProperties.SetName(
            button, open ? "Свернуть дополнительные действия" : "Показать остальные действия");
    }

    /// <summary>One collapsible bucket of skills, folded by default.</summary>
    private FrameworkElement SkillGroupSection(SkillGroup group, List<SkillInfo> items, bool expanded)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var body = new StackPanel
        {
            Margin = new Thickness(12, 2, 0, 8),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
        };

        foreach (var skill in items)
        {
            body.Children.Add(SkillRow(skill));
        }

        // A whole bucket in one click: this is the "preset" the section already describes.
        if (!items.All(skill => Skills.IsInstalled(skill.Name)))
        {
            var bulk = new Button
            {
                Content = $"Установить раздел · {items.Count}",
                Style = (Style)FindResource("SubtleButton"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
            };
            AutomationProperties.SetName(bulk, "Установить раздел " + group.Name);
            bulk.Click += (_, _) => InstallSkills(items.Select(skill => skill.Name), group.Name);
            body.Children.Insert(0, bulk);
        }

        var title = new StackPanel();
        title.Children.Add(new TextBlock
        {
            Text = $"{group.Name} · {items.Count}",
            Style = (Style)FindResource("CardTitle"),
        });
        title.Children.Add(new TextBlock
        {
            Text = group.Description,
            Style = (Style)FindResource("Caption"),
            Margin = new Thickness(0, 4, 8, 0),
        });

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (items.All(skill => Skills.IsInstalled(skill.Name)))
        {
            trailing.Children.Add(new TextBlock
            {
                Text = "\uE73E",
                FontFamily = GlyphFont,
                FontSize = 12,
                Foreground = (Brush)FindResource("Br.Success"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }

        var chevron = NewChevron();
        trailing.Children.Add(chevron);

        var header = new Button
        {
            Style = (Style)FindResource("Disclosure"),
            Content = DisclosureContent(title, trailing),
        };
        AutomationProperties.SetName(header, $"{group.Name} ({items.Count})");

        WireDisclosure(header, chevron, body, expanded, open =>
        {
            if (open)
            {
                _openSkillGroups.Add(group.Name);
            }
            else
            {
                _openSkillGroups.Remove(group.Name);
            }
        });

        host.Children.Add(header);
        host.Children.Add(body);
        return host;
    }

    /// <summary>One skill: its name, folded; a click reveals the description and install button.</summary>
    private FrameworkElement SkillRow(SkillInfo skill)
    {
        bool installed = Skills.IsInstalled(skill.Name);
        bool expanded = _openSkills.Contains(skill.Name);

        var body = new StackPanel
        {
            Margin = new Thickness(30, 2, 12, 10),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
        };

        if (!string.IsNullOrWhiteSpace(skill.Description))
        {
            body.Children.Add(new TextBlock
            {
                Text = skill.Description,
                Style = (Style)FindResource("Caption"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            });
        }

        if (installed)
        {
            body.Children.Add(new TextBlock
            {
                Text = Skills.IsShipped(skill.Name)
                    ? "Уже установлено"
                    : "Уже установлено из общего каталога ~/.agents\\skills",
                Style = (Style)FindResource("Caption"),
                Foreground = (Brush)FindResource("Br.Success"),
                Margin = new Thickness(0, 8, 0, 0),
            });

            if (Skills.IsShipped(skill.Name))
            {
                var reinstall = new Button
                {
                    Content = "Обновить",
                    Style = (Style)FindResource("SubtleButton"),
                    Padding = new Thickness(14, 7, 14, 7),
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                AutomationProperties.SetName(reinstall, "Обновить " + skill.Name);
                reinstall.Click += (_, _) => InstallSkills(new[] { skill.Name }, skill.Name);
                body.Children.Add(reinstall);
            }
        }
        else
        {
            var install = new Button
            {
                Content = "Скачать и установить",
                Style = (Style)FindResource("SubtleButton"),
                Padding = new Thickness(14, 7, 14, 7),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0),
            };
            install.Click += (_, _) => InstallSkills(new[] { skill.Name }, skill.Name);
            body.Children.Add(install);
        }

        var label = new StackPanel();
        label.Children.Add(new TextBlock
        {
            Text = skill.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Br.Text"),
        });

        // A quiet dot marks "already installed" without opening the row.
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        bool favorite = (App.Settings.FavoriteSkills ?? new List<string>())
            .Contains(skill.Name, StringComparer.OrdinalIgnoreCase);
        var star = new Button
        {
            Content = favorite ? "\uE735" : "\uE734",
            FontFamily = GlyphFont,
            FontSize = 12,
            Foreground = favorite
                ? (Brush)FindResource("Br.Accent")
                : (Brush)FindResource("Br.TextSecondary"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = favorite ? "Убрать из избранного" : "В избранное",
        };
        AutomationProperties.SetName(
            star, (favorite ? "Убрать из избранного " : "В избранное ") + skill.Name);
        star.Click += (_, _) => ToggleFavoriteSkill(skill.Name);
        trailing.Children.Add(star);

        if (installed)
        {
            trailing.Children.Add(new TextBlock
            {
                Text = "\uE73E",
                FontFamily = GlyphFont,
                FontSize = 11,
                Foreground = (Brush)FindResource("Br.Success"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
        }

        var chevron = NewChevron();
        trailing.Children.Add(chevron);

        var header = new Button
        {
            Style = (Style)FindResource("DisclosureRow"),
            Content = DisclosureContent(label, trailing),
        };
        AutomationProperties.SetName(header, skill.Name);

        WireDisclosure(header, chevron, body, expanded, open =>
        {
            if (open)
            {
                _openSkills.Add(skill.Name);
            }
            else
            {
                _openSkills.Remove(skill.Name);
            }
        });

        var host = new StackPanel();
        host.Children.Add(header);
        host.Children.Add(body);
        return host;
    }

    private static TextBlock NewChevron() => new()
    {
        Text = "\uE70D",
        FontFamily = GlyphFont,
        FontSize = 10,
        Foreground = (Brush)Application.Current.Resources["Br.TextSecondary"],
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 1, 0, 0),
    };

    /// <summary>Header row: the label stretching across, with a trailing glyph docked right.</summary>
    private static DockPanel DisclosureContent(FrameworkElement label, FrameworkElement trailing)
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(trailing, Dock.Right);
        row.Children.Add(trailing);
        row.Children.Add(label);
        return row;
    }

    /// <summary>Folds a body away behind a header and remembers the choice.</summary>
    private static void WireDisclosure(
        Button header, TextBlock chevron, FrameworkElement body, bool open, Action<bool> remember)
    {
        chevron.Text = open ? "\uE70E" : "\uE70D";
        header.Click += (_, _) =>
        {
            open = !open;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70E" : "\uE70D";
            remember(open);
        };
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

        TxtSkillsHint.Text = Skills.Catalog.Count == 0
            ? "Скиллов пока нет. Установите OpenCode — скиллы появятся здесь автоматически."
            : $"Показано: {_skillsShown} из {Skills.Catalog.Count}. Установлено: {Skills.InstalledCount}. Каталог лежит в {Skills.Folder}";
    }

    // ---------------------------------------------------------------- memory

    private void RenderMemory()
    {
        var all = MemoryStore.All();
        bool wired = OpenCodeConfig.HasMemory();

        Dot(DotMemory, wired ? Kind.Ok : Kind.Unknown);
        TxtMemoryState.Text = wired
            ? "Подключена к OpenCode через MCP: ассистент ищет здесь контекст и сохраняет уроки сам."
            : "Память работает, но OpenCode её пока не видит. Перезапустите TokenHub — подключение добавится автоматически.";

        var tags = MemoryStore.Tags();
        string tagLine = tags.Count == 0
            ? string.Empty
            : " · " + string.Join(", ", tags.Take(6).Select(t => $"#{t.Tag}"));

        TxtMemoryStats.Text =
            $"{all.Count} {Format.Plural(all.Count, "заметка", "заметки", "заметок")}"
            + $" · сегодня {MemoryStore.TodayCount()}"
            + tagLine;

        string query = TxtMemorySearch.Text.Trim();
        var shown = query.Length == 0 ? all : MemoryStore.Search(query, 50);

        MemoryList.Children.Clear();
        foreach (var entry in shown.Take(200))
        {
            MemoryList.Children.Add(BuildMemoryCard(entry));
        }

        if (shown.Count == 0)
        {
            TxtMemoryEmpty.Text = all.Count == 0
                ? "Память пока пуста. Попросите ассистента разобраться в проекте — он сохранит важное сам, или добавьте заметку выше."
                : $"Ничего не найдено по запросу «{query}».";
            TxtMemoryEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            TxtMemoryEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private UIElement BuildMemoryCard(MemoryStore.Entry entry)
    {
        var head = new DockPanel();

        var forget = new Button
        {
            Content = "Удалить",
            Style = (Style)Application.Current.Resources["SubtleButton"],
            Tag = entry.Id,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(forget, "Удалить заметку " + entry.Id);
        forget.Click += DeleteMemory_Click;
        DockPanel.SetDock(forget, Dock.Right);
        head.Children.Add(forget);

        string scope = string.IsNullOrEmpty(entry.Scope)
            ? "общая"
            : Path.GetFileName(entry.Scope.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        head.Children.Add(new TextBlock
        {
            Text = $"{MemoryKindName(entry.Kind)} · {entry.At} · {scope}",
            FontSize = 11,
            Foreground = ResBrush("Br.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = entry.Text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResBrush("Br.Text"),
            Margin = new Thickness(0, 6, 0, 0),
        });

        if (entry.Tags.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = string.Join("  ", entry.Tags.Select(t => "#" + t)),
                FontSize = 11,
                Foreground = ResBrush("Br.Accent"),
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background = ResBrush("Br.Control"),
            BorderBrush = ResBrush("Br.ControlBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = body,
        };
    }

    private static string MemoryKindName(string kind) => kind switch
    {
        "lesson" => "урок",
        "decision" => "решение",
        "fact" => "факт",
        "snippet" => "код",
        _ => "заметка",
    };

    private void MemorySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RenderMemory();
    }

    private void SaveMemory_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtMemoryNew.Text.Trim();
        if (text.Length == 0)
        {
            Status("Заметка пустая — напишите, что запомнить.", Kind.Fail);
            return;
        }

        var entry = MemoryStore.Add(text, "note");
        TxtMemoryNew.Text = string.Empty;
        RenderMemory();
        Status($"Заметка {entry.Id} сохранена в память.", Kind.Ok);
    }

    private void DeleteMemory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string id)
        {
            return;
        }

        if (MemoryStore.Remove(id))
        {
            RenderMemory();
            Status($"Заметка {id} удалена.", Kind.Ok);
        }
    }

    private void ClearMemory_Click(object sender, RoutedEventArgs e)
    {
        if (MemoryStore.Count == 0)
        {
            Status("Память уже пуста.", Kind.Unknown);
            return;
        }

        var answer = MessageBox.Show(
            this,
            "Удалить все заметки памяти? Ассистент потеряет накопленный контекст.",
            "TokenHub",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        MemoryStore.Clear();
        RenderMemory();
        Status("Память очищена.", Kind.Ok);
    }

    private void OpenMemoryFile_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(MemoryStore.FilePath))
        {
            Launcher.RevealFile(MemoryStore.FilePath);
        }
        else
        {
            Status("Файл памяти ещё не создан — сохраните первую заметку.", Kind.Unknown);
        }
    }

    private void OpenMemoryFolder_Click(object sender, RoutedEventArgs e) =>
        Launcher.OpenFolder(SettingsStore.Directory);

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

    private void UpdateInstalledSkills_Click(object sender, RoutedEventArgs e)
    {
        var installed = Skills.Catalog
            .Where(skill => Skills.IsShipped(skill.Name) && Skills.IsInstalled(skill.Name))
            .Select(skill => skill.Name)
            .ToList();

        if (installed.Count == 0)
        {
            Status("Обновлять нечего — ни один скилл ещё не установлен.", Kind.Unknown);
            return;
        }

        InstallSkills(installed, "обновление установленных");
    }

    /// <summary>Copies a skill folder the user picked, e.g. one downloaded from GitHub.</summary>
    private void ImportSkillFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку скилла (внутри должен быть SKILL.md)",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Busy(true);
            int copied = Skills.ImportFolder(dialog.FolderName);
            Busy(false);

            if (copied == 0)
            {
                Status("В выбранной папке нет SKILL.md — это не скилл.", Kind.Fail);
                return;
            }

            Skills.Reload();
            RenderSkills();
            Journal($"Скилл добавлен: {System.IO.Path.GetFileName(dialog.FolderName)}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Busy(false);
            Status("Не удалось добавить скилл: " + ex.Message, Kind.Fail);
        }
    }

    /// <summary>Accepts a dragged folder or .zip anywhere on the skills card.</summary>
    private void SkillsImport_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SkillsImport_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        int added = 0;
        foreach (string path in paths)
        {
            try
            {
                if (System.IO.Directory.Exists(path))
                {
                    added += Skills.ImportFolder(path);
                }
                else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    added += Skills.ImportZip(path);
                }
            }
            catch
            {
                // One bad item must not cancel the rest of the drop.
            }
        }

        e.Handled = true;

        if (added == 0)
        {
            Status("В перетащенном нет скиллов — нужна папка с SKILL.md или архив.", Kind.Unknown);
            return;
        }

        Skills.Reload();
        RenderSkills();
        Journal($"Добавлено скиллов перетаскиванием: {added}", Kind.Ok);
    }

    /// <summary>Takes a .zip of one or more skills and unpacks them into the skills folder.</summary>
    private void ImportSkillZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите архив со скиллами",
            Filter = "Архив со скиллами (*.zip)|*.zip",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Busy(true);
            int copied = Skills.ImportZip(dialog.FileName);
            Busy(false);

            if (copied == 0)
            {
                Status("В архиве не нашлось ни одного SKILL.md.", Kind.Fail);
                return;
            }

            Skills.Reload();
            RenderSkills();
            Journal($"Из архива добавлено скиллов: {copied}", Kind.Ok);
        }
        catch (Exception ex)
        {
            Busy(false);
            Status("Не удалось разобрать архив: " + ex.Message, Kind.Fail);
        }
    }

    private void SkillFav_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        RenderSkills();
    }

    private void ToggleFavoriteSkill(string name)
    {
        var favorites = App.Settings.FavoriteSkills ??= new List<string>();
        int removed = favorites.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            favorites.Add(name);
        }

        SettingsStore.Save(App.Settings);
        RenderSkills();
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

    /// <summary>Project model overrides travel with the keys, so a new machine is set up in one import.</summary>
    public List<ProjectProfile> Profiles { get; set; } = new();
}
