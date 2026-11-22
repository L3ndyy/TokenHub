using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TokenHub.Services;

namespace TokenHub;

/// <summary>
/// Compact flyout next to the clock (item V13). It shows the pool at a glance and
/// hands one requested action back to <see cref="MainWindow"/>.
/// </summary>
public partial class QuickWindow : Window
{
    /// <summary>"open", "check", "keys" or "journal"; null when the user just closed it.</summary>
    public string? Action { get; private set; }

    private bool _chosen;

    public QuickWindow()
    {
        InitializeComponent();
        Fill();
        PlaceNearTray();
        Loaded += (_, _) =>
        {
            PlaceNearTray();
            Activate();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        Deactivated += OnDeactivated;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_chosen)
        {
            Close();
        }
    }

    private void PlaceNearTray()
    {
        Rect area = SystemParameters.WorkArea;
        double height = ActualHeight > 0 ? ActualHeight : 260;
        Left = area.Right - Width - 12;
        Top = area.Bottom - height - 12;
    }

    private void Fill()
    {
        var proxy = LocalProxy.Instance;
        bool service = PoolService.Current == PoolService.State.Running;
        bool running = proxy.Running || service;
        int available = App.Settings.Keys.Count(k => k.Available);

        TrayDot.Fill = (Brush)Application.Current.Resources[running ? "Br.Success" : "Br.Danger"];
        TxtHead.Text = running ? "Пул работает" : "Пул остановлен";

        int port = service ? App.Settings.LocalPort : proxy.Port;
        TxtSummary.Text = running
            ? $"http://127.0.0.1:{port}/v1 · доступно ключей: {available} из {App.Settings.Keys.Count}"
            : "Локальный прокси не запущен. Откройте TokenHub и запустите его.";

        KeyList.Children.Clear();
        foreach (var key in App.Settings.Keys)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)Application.Current.Resources[
                    key.Invalid || key.ExhaustedUntilUtc > DateTime.UtcNow ? "Br.Danger"
                    : key.Available ? "Br.Success" : "Br.Muted"],
            });

            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(key.Label) ? key.Masked : key.Label,
                Foreground = (Brush)Application.Current.Resources["Br.Text"],
                VerticalAlignment = VerticalAlignment.Center,
            });

            KeyList.Children.Add(row);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Finish("open");

    private void Check_Click(object sender, RoutedEventArgs e) => Finish("check");

    private void Keys_Click(object sender, RoutedEventArgs e) => Finish("keys");

    private void Journal_Click(object sender, RoutedEventArgs e) => Finish("journal");

    private void Finish(string action)
    {
        _chosen = true;
        Action = action;
        Close();
    }
}