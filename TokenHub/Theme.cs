using System.Windows;
using System.Windows.Media;
using TokenHub.Services;

namespace TokenHub;

/// <summary>Applies the dark/light palette and the accent colour at runtime.</summary>
internal static class Theme
{
    public static bool IsDark(string theme)
    {
        return theme switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemUsesDark(),
        };
    }

    private static bool SystemUsesDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return true;
        }
    }

    public static void Apply(string theme, string accentChoice)
    {
        bool dark = IsDark(theme);
        string accent = ResolveAccent(accentChoice, dark);

        foreach (var (key, hex) in Palette.For(dark, accent))
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
            }
        }
    }

    private static string ResolveAccent(string choice, bool dark)
    {
        if (string.Equals(choice, "system", StringComparison.OrdinalIgnoreCase))
        {
            var system = Native.SystemAccentHex();
            if (system is not null)
            {
                return system;
            }

            return dark ? "#FF4CC2FF" : "#FF0067C0";
        }

        return choice.StartsWith('#') ? choice : "#FF4CC2FF";
    }
}
