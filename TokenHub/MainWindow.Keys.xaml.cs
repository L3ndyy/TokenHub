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
            KeysList.Children.Add(EmptyState(
                "\uE72E",
                "Ключей пока нет",
                "Добавьте первый ключ Token Harbor — пул заработает сразу. Ключи лежат на этом компьютере в зашифрованном виде.",
                EmptyStateButton("Добавить ключ", "AccentButton", AddKey_Click),
                EmptyStateButton("Импорт ключей", "SubtleButton", ImportKeys_Click, 8)));
        }
        else if (visible.Count == 0)
        {
            KeysList.Children.Add(EmptyState(
                "\uE721",
                "Ничего не найдено",
                $"По запросу «{query}» ключей нет. Проверьте написание или очистите поиск."));
        }

        TxtKeysHint.Visibility = settings.Keys.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Placeholder rows for the key list while probes are in flight. The wait is real — every
    /// check is a network round-trip — so the list admits it instead of looking frozen on stale
    /// data. Replaced by <see cref="RenderKeys"/> as soon as the sweep finishes.
    /// </summary>
    private void ShowKeysSkeleton()
    {
        if (KeysList is null)
        {
            return;
        }

        KeysList.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            var column = new StackPanel();
            column.Children.Add(SkeletonLine(150, 12));
            column.Children.Add(SkeletonLine(260, 9));

            KeysList.Children.Add(new Border
            {
                Background = ResBrush("Br.Control"),
                BorderBrush = ResBrush("Br.ControlBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = column,
            });
        }
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
        ShowKeysSkeleton();
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
        ShowKeysSkeleton();

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
}
