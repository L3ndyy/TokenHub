using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TokenHub;

/// <summary>
/// Ctrl+K command palette (item V10). Kept deliberately small: a filter box and a
/// list of named actions, all of which already exist as buttons somewhere in the app.
/// </summary>
public partial class PaletteWindow : Window
{
    private sealed record Command(string Id, string Title, string[] Words);

    private static readonly Command[] All =
    {
        new("checkAll", "Проверить все ключи", new[] { "проверить", "ключи", "check" }),
        new("apply", "Применить пул ключей в OpenCode", new[] { "применить", "пул", "opencode" }),
        new("keys", "Раздел «Ключи»", new[] { "ключи", "пул", "раздел" }),
        new("network", "Раздел «Сеть»", new[] { "сеть", "прокси", "адрес" }),
        new("settings", "Раздел «Настройки»", new[] { "настройки", "тема", "автозапуск" }),
        new("journal", "Журнал событий", new[] { "журнал", "лог", "события" }),
        new("diagnostics", "Диагностика (ZIP-отчёт)", new[] { "диагностика", "отчёт", "zip", "поддержка" }),
        new("config", "Открыть конфиг OpenCode", new[] { "конфиг", "opencode", "файл" }),
        new("hub", "Папка TokenHub", new[] { "папка", "настройки", "файлы" }),
        new("opencode", "Запустить OpenCode", new[] { "запустить", "opencode", "приложение" }),
        new("terminal", "Открыть терминал", new[] { "терминал", "консоль", "cmd" }),
    };

    public string? Chosen { get; private set; }

    public PaletteWindow()
    {
        InitializeComponent();
        Fill(All);
        Loaded += (_, _) => TxtFilter.Focus();
    }

    private void Fill(IEnumerable<Command> commands)
    {
        LstCommands.Items.Clear();
        foreach (var command in commands)
        {
            LstCommands.Items.Add(new ListBoxItem
            {
                Content = command.Title,
                Tag = command.Id,
            });
        }

        if (LstCommands.Items.Count > 0)
        {
            LstCommands.SelectedIndex = 0;
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        string query = TxtFilter.Text.Trim();
        if (query.Length == 0)
        {
            Fill(All);
            return;
        }

        Fill(All.Where(c =>
            c.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || c.Words.Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)
                                || w.Contains(query, StringComparison.CurrentCultureIgnoreCase))));
    }

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Enter:
                Choose();
                break;

            case Key.Down:
                Move(1);
                e.Handled = true;
                break;

            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (LstCommands.Items.Count == 0)
        {
            return;
        }

        int index = LstCommands.SelectedIndex + delta;
        LstCommands.SelectedIndex = (index + LstCommands.Items.Count) % LstCommands.Items.Count;
        LstCommands.ScrollIntoView(LstCommands.SelectedItem);
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    private void Choose()
    {
        if (LstCommands.SelectedItem is ListBoxItem item && item.Tag is string id)
        {
            Chosen = id;
        }

        Close();
    }
}
