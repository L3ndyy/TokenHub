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

    // Window messages and hit-test codes that make the custom maximize button behave like
    // the system one: Windows 11 draws the snap-layouts flyout only for HTMAXBUTTON.
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtMaxButton = 9;

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

    // ---------------------------------------------------------------- tray

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest && OverCaptionButton(BtnMax, lParam))
        {
            handled = true;
            return (IntPtr)HtMaxButton;
        }

        if (msg == WmNcLButtonDown && wParam == (IntPtr)HtMaxButton)
        {
            // The hit test above turns the press into a non-client click, so the button's
            // own Click never fires; the toggle happens here instead.
            handled = true;
            Dispatcher.BeginInvoke(new Action(ToggleMaximize));
            return IntPtr.Zero;
        }

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
                ToggleMaximize();
                break;
            case "close":
                Close();
                break;
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        BtnMax.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    /// <summary>True when a non-client hit test point lands on the given caption button.</summary>
    private bool OverCaptionButton(Button button, IntPtr lParam)
    {
        // lParam carries screen pixels, WPF works in device-independent units.
        long packed = lParam.ToInt64();
        var screen = new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        var corner = button.TranslatePoint(new Point(0, 0), this);
        return new Rect(corner, new Size(button.ActualWidth, button.ActualHeight))
            .Contains(PointFromScreen(screen));
    }

    /// <summary>Opens the "Ещё" dropdown as a real menu instead of an inline pile of buttons.</summary>
    private void MoreMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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

    /// <summary>
    /// Rail badges: how many keys are not working and how many skills from the set are
    /// still missing. Zero hides the badge, so the rail stays quiet when all is well.
    /// </summary>
    private void RefreshNavBadges()
    {
        int broken = App.Settings.Keys.Count(k => !k.Available);
        SetBadge(BadgeKeys, BadgeKeysText, broken, $"Недоступных ключей: {broken}");

        int missing = Math.Max(0, Skills.Catalog.Count - Skills.InstalledCount);
        SetBadge(BadgeSkills, BadgeSkillsText, missing, $"Не установлено скиллов: {missing}");
    }

    private static void SetBadge(Border badge, TextBlock text, int count, string toolTip)
    {
        bool show = count > 0;
        badge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        text.Text = count.ToString();
        badge.ToolTip = toolTip;
    }

    /// <summary>
    /// The "nothing here" block: a glyph, a headline, one line saying what to do next and,
    /// when the list is empty rather than filtered, the actions that fill it. Used by the
    /// key list and the skill catalogue so both say the same kind of thing.
    /// </summary>
    private FrameworkElement EmptyState(string glyph, string title, string hint, params UIElement[] actions)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 6) };

        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = GlyphFont,
            FontSize = 26,
            Foreground = ResBrush("Br.Muted"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResBrush("Br.Text"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        });

        panel.Children.Add(new TextBlock
        {
            Text = hint,
            Style = (Style)FindResource("Caption"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 430,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        if (actions.Length > 0)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0),
            };

            foreach (var action in actions)
            {
                row.Children.Add(action);
            }

            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>A button for an empty state, wired to an existing click handler.</summary>
    private Button EmptyStateButton(string text, string style, RoutedEventHandler click, double left = 0)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(style),
            Margin = new Thickness(left, 0, 0, 0),
        };

        button.Click += click;
        AutomationProperties.SetName(button, text);
        return button;
    }

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
}
