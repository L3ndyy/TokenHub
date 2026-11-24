using System.Windows;

namespace TokenHub;

/// <summary>
/// The short tour shown once, before the setup wizard. It can be skipped, and the
/// "Обзор программы" button on the dashboard opens it again at any time.
/// </summary>
public partial class IntroWindow : Window
{
    public IntroWindow()
    {
        InitializeComponent();
    }

    /// <summary>True when the user chose to continue to the setup wizard.</summary>
    public bool GoFurther { get; private set; }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        GoFurther = true;
        Close();
    }
}