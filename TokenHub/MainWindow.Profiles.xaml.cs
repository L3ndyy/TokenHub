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
}
