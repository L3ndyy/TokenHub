using System.Windows;
using System.Windows.Media;

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

        var palette = new Dictionary<string, string>
        {
            ["Br.Layer"] = dark ? "#59000000" : "#F2F6F6F6",
            ["Br.Popup"] = dark ? "#FF1C1C1C" : "#FFFFFFFF",
            ["Br.Card"] = dark ? "#0FFFFFFF" : "#B3FFFFFF",
            ["Br.CardBorder"] = dark ? "#17FFFFFF" : "#12000000",
            ["Br.Text"] = dark ? "#FFFFFFFF" : "#E4000000",
            ["Br.TextSecondary"] = dark ? "#B0FFFFFF" : "#99000000",
            ["Br.Control"] = dark ? "#12FFFFFF" : "#B3FFFFFF",
            ["Br.ControlHover"] = dark ? "#1FFFFFFF" : "#FFFFFF",
            ["Br.ControlBorder"] = dark ? "#1FFFFFFF" : "#14000000",
            ["Br.Accent"] = accent,
            ["Br.OnAccent"] = dark ? "#FF0A0A0A" : "#FFFFFFFF",
            ["Br.AccentBorder"] = dark ? "#33FFFFFF" : "#1A000000",
            ["Br.Success"] = dark ? "#FF6CCB5F" : "#FF0F7B0F",
            ["Br.Danger"] = dark ? "#FFFF99A4" : "#FFC42B1C",
            ["Br.Muted"] = dark ? "#8AFFFFFF" : "#73000000",
        };

        foreach (var (key, hex) in palette)
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
