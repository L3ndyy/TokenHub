using System.Windows;
using System.Windows.Controls;
using TokenHub.Services;

namespace TokenHub;

/// <summary>In-app viewer for <see cref="EventLog"/> (item T9).</summary>
public partial class LogWindow : Window
{
    public LogWindow()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        string text = EventLog.Read();
        TxtLog.Text = string.IsNullOrEmpty(text) ? "Журнал пуст." : text;
        TxtLog.ScrollToEnd();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TxtLog.Text);
        }
        catch
        {
            // Clipboard busy: not worth interrupting.
        }
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Launcher.RevealFile(EventLog.FilePath);
        }
        catch
        {
            // Explorer refused; the in-app view still works.
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Очистить журнал?", "TokenHub", MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        EventLog.Clear();
        Reload();
    }
}
