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

        Toast(message, kind);
    }

    // ---------------------------------------------------------------- toasts

    private const int ToastMax = 3;
    private static readonly TimeSpan ToastLife = TimeSpan.FromSeconds(6);
    private readonly List<(Border Card, DispatcherTimer Timer)> _toasts = new();

    /// <summary>
    /// A notice in the corner. The status bar line is easy to miss when the window sits in
    /// the background, and a message box would be far too loud for "импорт прошёл". Clicking
    /// a toast opens the journal; a repeated message refreshes instead of piling up.
    /// </summary>
    private void Toast(string message, Kind kind)
    {
        if (ToastHost is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var shown = _toasts.FirstOrDefault(t => (string?)t.Card.Tag == message);
        if (shown.Card is not null)
        {
            shown.Timer.Stop();
            shown.Timer.Start();
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brush(kind),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResBrush("Br.Text"),
            MaxWidth = 380,
        });

        var card = new Border
        {
            Tag = message,
            Child = row,
            Background = ResBrush("Br.Popup"),
            BorderBrush = ResBrush("Br.CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 8, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Открыть журнал",
            Opacity = AnimationsEnabled ? 0 : 1,
        };
        card.MouseLeftButtonUp += (_, _) => OpenJournal();

        while (_toasts.Count >= ToastMax)
        {
            RemoveToast(_toasts[0].Card);
        }

        ToastHost.Children.Add(card);

        var timer = new DispatcherTimer { Interval = ToastLife };
        timer.Tick += (_, _) => RemoveToast(card);
        timer.Start();
        _toasts.Add((card, timer));

        if (AnimationsEnabled)
        {
            card.BeginAnimation(
                OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void RemoveToast(Border card)
    {
        int index = _toasts.FindIndex(t => t.Card == card);
        if (index >= 0)
        {
            _toasts[index].Timer.Stop();
            _toasts.RemoveAt(index);
        }

        ToastHost?.Children.Remove(card);
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
        RefreshNavBadges();
        var proxy = LocalProxy.Instance;

        // One number first: what the week would have cost on the paid equivalent of the
        // current model. It is an estimate, so the caption says so.
        // A month is loaded because the sparkline needs it; the newest seven days are the
        // week on screen, the seven before them are the yardstick for "vs last week".
        var month = UsageStore.Recent(30);
        int split = Math.Max(0, month.Count - 7);
        var before = month.Skip(Math.Max(0, split - 7)).Take(7).ToList();
        var week = month.Skip(split).ToList();
        long prompt = week.Sum(d => d.Prompt);
        long completion = week.Sum(d => d.Completion);
        long tokens = prompt + completion;
        int answers = week.Sum(d => d.Responses);
        long switches = proxy.Running ? proxy.Failovers : _serviceFailovers;

        if (TxtSaved is not null)
        {
            TxtSaved.Text = Format.Rub(Pricing.Rub(prompt, completion, App.Settings.DefaultModel));
        }

        if (TxtSavedDelta is not null)
        {
            long beforeTokens = before.Sum(d => d.Prompt + d.Completion);
            string? delta = Format.WeekDelta(tokens, beforeTokens);
            TxtSavedDelta.Text = delta ?? string.Empty;
            TxtSavedDelta.Visibility = delta is null ? Visibility.Collapsed : Visibility.Visible;
            TxtSavedDelta.Foreground = ResBrush(tokens >= beforeTokens ? "Br.Success" : "Br.Muted");
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

        DrawSparkline(month);
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
        const double peakLabelHeight = 14;
        double plotHeight = height - labelHeight;
        double slot = width / week.Count;
        double barWidth = Math.Max(4, Math.Min(26, slot - 8));
        double peak = Math.Max(1, week.Max(d => d.Prompt + d.Completion));
        var accent = ResBrush("Br.Accent");
        var idle = ResBrush("Br.Muted");
        var labelBrush = ResBrush("Br.TextSecondary");

        // A hairline under the bars: the chart no longer floats in its card.
        var baseline = new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = 1,
            Fill = ResBrush("Br.ControlBorder"),
        };
        Canvas.SetLeft(baseline, 0);
        Canvas.SetTop(baseline, plotHeight);
        HistoryCanvas.Children.Add(baseline);

        // The week's average as a dashed line, so a bar is read against something.
        double mean = week.Average(d => (double)(d.Prompt + d.Completion));
        double plotRange = plotHeight - 8 - peakLabelHeight;
        if (mean > 0)
        {
            double meanY = plotHeight - Math.Min(plotRange, mean / peak * plotRange);
            HistoryCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0,
                X2 = width,
                Y1 = meanY,
                Y2 = meanY,
                Stroke = labelBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                Opacity = 0.45,
                ToolTip = $"Среднее за неделю: {Format.Compact((long)mean)} токенов в день",
            });
        }

        for (int i = 0; i < week.Count; i++)
        {
            var day = week[i];
            long value = day.Prompt + day.Completion;
            bool isToday = day.Day.Date == DateTime.Today;
            double barHeight = value <= 0
                ? 2
                : Math.Max(3, value / peak * plotRange);
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

            // Name the tallest day: the one point that matters should not need a hover.
            if (value > 0 && value == peak)
            {
                var peakLabel = new TextBlock
                {
                    Text = Format.Compact(value),
                    FontSize = 10,
                    Foreground = accent,
                    Width = slot,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(peakLabel, slot * i);
                Canvas.SetTop(peakLabel, Math.Max(0, plotHeight - barHeight - peakLabelHeight));
                HistoryCanvas.Children.Add(peakLabel);
            }

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

    /// <summary>
    /// A month of tokens as a single line above the week of bars. The bars answer "how is
    /// this week going", the line answers "where is this heading".
    /// </summary>
    private void DrawSparkline(IReadOnlyList<(DateTime Day, long Prompt, long Completion, int Responses)> month)
    {
        if (SparkCanvas is null)
        {
            return;
        }

        SparkCanvas.Children.Clear();

        double width = SparkCanvas.ActualWidth;
        double height = SparkCanvas.ActualHeight;
        if (width <= 20 || height <= 12 || month.Count < 2)
        {
            return;
        }

        double peak = Math.Max(1, month.Max(d => d.Prompt + d.Completion));
        double plot = height - 4;
        double step = width / (month.Count - 1);
        var accent = ResBrush("Br.Accent");

        var line = new System.Windows.Shapes.Polyline
        {
            Stroke = accent,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            ToolTip = $"Последние {month.Count} дней, максимум {Format.Compact((long)peak)} токенов в день",
        };
        var area = new System.Windows.Shapes.Polygon { Fill = accent, Opacity = 0.16 };

        for (int i = 0; i < month.Count; i++)
        {
            long value = month[i].Prompt + month[i].Completion;
            double x = i * step;
            double y = height - 2 - Math.Min(plot, value / peak * plot);
            line.Points.Add(new Point(x, y));
            area.Points.Add(new Point(x, y));
        }

        // Close the polygon along the bottom so the month reads as an area, not a wire.
        area.Points.Add(new Point(width, height));
        area.Points.Add(new Point(0, height));

        SparkCanvas.Children.Add(area);
        SparkCanvas.Children.Add(line);

        // Today is the one point that must be findable at a glance.
        Point today = line.Points[^1];
        var dot = new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = accent };
        Canvas.SetLeft(dot, today.X - 2.5);
        Canvas.SetTop(dot, today.Y - 2.5);
        SparkCanvas.Children.Add(dot);

        if (TxtSparkPeak is not null)
        {
            TxtSparkPeak.Text = peak <= 1 ? string.Empty : $"макс {Format.Compact((long)peak)} в день";
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
}
