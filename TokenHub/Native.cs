using System.Runtime.InteropServices;

namespace TokenHub;

/// <summary>
/// Win32 helpers for the Windows 11 look: Mica backdrop, rounded corners,
/// immersive dark mode and system accent colour.
/// </summary>
internal static class Native
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmsbtMainWindow = 2; // Mica
    private const int DwmcpRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    /// <summary>Private message a second launch posts so the running window shows itself through WPF.</summary>
    public static readonly uint ShowMessage = RegisterWindowMessage("TokenHub.ShowWindow");

    /// <summary>
    /// Asks the already-running TokenHub to come to the front (item T3: a second
    /// launch should reuse the existing instance instead of starting a rival one).
    /// A raw ShowWindow is not enough: when the window is hidden in the tray WPF
    /// still believes it is hidden, and the restored window stays blank.
    /// </summary>
    public static bool RequestShow(string title)
    {
        IntPtr hwnd = FindWindow(null, title);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return PostMessage(hwnd, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Enables the Mica backdrop. Returns true when Windows accepted it (Windows 11 22H2+).</summary>
    public static bool TryEnableMica(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // WPF paints its composition target with the window background by default,
        // which would hide the backdrop. Force it transparent first.
        try
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget is not null)
            {
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            }
        }
        catch
        {
            // Not fatal: without it the window simply keeps its solid background.
        }

        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        int corner = DwmcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));

        int backdrop = DwmsbtMainWindow;
        int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // Let the DWM paint the client area so the backdrop becomes visible.
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (result != 0)
        {
            return false;
        }

        return DwmGetWindowAttribute(hwnd, DwmwaSystemBackdropType, out int current, sizeof(int)) == 0
               && current == DwmsbtMainWindow;
    }

    public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
    }

    /// <summary>Windows accent colour as an RRGGBB string, or null when the user kept the default.</summary>
    public static string? SystemAccentHex()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int raw)
            {
                return null;
            }

            // Stored as ABGR.
            int r = raw & 0xFF;
            int g = (raw >> 8) & 0xFF;
            int b = (raw >> 16) & 0xFF;
            if (r == 0 && g == 0 && b == 0)
            {
                return null;
            }

            return $"#{r:X2}{g:X2}{b:X2}";
        }
        catch
        {
            return null;
        }
    }
}
