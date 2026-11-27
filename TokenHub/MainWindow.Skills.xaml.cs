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
            SkillsGroups.Children.Add(SkillsEmptyState(favoriteOnly, favorites.Count));
        }

        RefreshSkillsState();
        UpdateSkillsHint();
    }

    /// <summary>Why the catalogue is empty: nothing installed, nothing starred, or a filter that matches nothing.</summary>
    private FrameworkElement SkillsEmptyState(bool favoriteOnly, int favorites)
    {
        if (Skills.Catalog.Count == 0)
        {
            return EmptyState(
                "\uE8F1",
                "Набор скиллов не найден",
                "Каталог приходит вместе с OpenCode. Установите или запустите его — список подхватится сам, перезапускать TokenHub не нужно.");
        }

        if (favoriteOnly && favorites == 0)
        {
            return EmptyState(
                "\uE734",
                "В избранном пусто",
                "Нажмите звёздочку у названия скилла — он останется здесь, даже когда фильтр выключен.");
        }

        return EmptyState(
            "\uE721",
            "Ничего не найдено",
            $"По запросу «{TxtSkillSearch?.Text?.Trim()}» скиллов нет. Попробуйте другое слово или очистите поиск.");
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

        // Rows are built when the bucket is first opened. The catalogue carries a few
        // hundred skills and only the open buckets deserve real visuals.
        bool filled = false;
        void Fill()
        {
            if (filled)
            {
                return;
            }

            filled = true;

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
                body.Children.Add(bulk);
            }

            foreach (var skill in items)
            {
                body.Children.Add(SkillRow(skill));
            }
        }

        if (expanded)
        {
            Fill();
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
                Fill();
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
        RefreshNavBadges();
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
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(files);
        e.Handled = true;
    }

    private void SkillsImport_DragLeave(object sender, DragEventArgs e) => SetDropHighlight(false);

    /// <summary>
    /// Lights the import card up while a folder or archive hovers over it, so it is obvious
    /// that letting go here does something. The values are cleared instead of set back by
    /// hand, so the card returns to whatever its style says.
    /// </summary>
    private void SetDropHighlight(bool on)
    {
        if (CardSkillsImport is null)
        {
            return;
        }

        if (on)
        {
            CardSkillsImport.Background = ResBrush("Br.AccentSoft");
            CardSkillsImport.BorderBrush = ResBrush("Br.Accent");
        }
        else
        {
            CardSkillsImport.ClearValue(Border.BackgroundProperty);
            CardSkillsImport.ClearValue(Border.BorderBrushProperty);
        }
    }

    private void SkillsImport_Drop(object sender, DragEventArgs e)
    {
        SetDropHighlight(false);

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
}
