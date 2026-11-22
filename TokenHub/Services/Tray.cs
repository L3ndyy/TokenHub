using System.Runtime.InteropServices;

namespace TokenHub.Services;

/// <summary>
/// Notification-area icon and balloons, straight through <c>shell32</c> so the app
/// needs no WinForms dependency. The proxy lives as long as this process, so the
/// window hides to the tray instead of closing (item chosen by the user).
/// </summary>
internal static class Tray
{
    /// <summary>Callback message the shell posts to the window. WM_APP + 1.</summary>
    public const int CallbackMessage = 0x0400 + 1;

    public const int CmdOpen = 1;
    public const int CmdCheckKeys = 2;
    public const int CmdKeysPage = 3;
    public const int CmdExit = 4;
    public const int CmdStatus = 5;

    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmNull = 0x0000;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;

    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;

    private const uint NiifInfo = 0x00000001;
    private const uint NiifWarning = 0x00000002;

    private const uint TrackReturnCmd = 0x0100;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackNotify = 0x0080;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfGrayed = 0x00000001;

    private static IntPtr _icon;
    private static bool _added;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>True when the shell message means "the icon was clicked".</summary>
    public static bool IsLeftClick(IntPtr lParam) => ((int)lParam & 0xFFFF) == WmLButtonUp;

    public static bool IsRightClick(IntPtr lParam) => ((int)lParam & 0xFFFF) == WmRButtonUp;

    public static bool Install(IntPtr window, string tooltip)
    {
        if (_added)
        {
            return true;
        }

        _icon = LoadAppIcon();

        var data = NewData(window, tooltip);
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.hIcon = _icon;

        _added = ShellNotifyIcon(NimAdd, ref data);
        return _added;
    }

    public static void Remove(IntPtr window)
    {
        if (!_added)
        {
            return;
        }

        var data = NewData(window, string.Empty);
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;

        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    /// <summary>Shows a balloon. Silently does nothing if the icon is gone.</summary>
    public static void Notify(IntPtr window, string title, string text, bool warning = false)
    {
        if (!_added)
        {
            return;
        }

        var data = NewData(window, string.Empty);
        data.uFlags = NifInfo | NifIcon | NifTip;
        data.hIcon = _icon;
        data.dwInfoFlags = warning ? NiifWarning : NiifInfo;
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(text, 255);

        ShellNotifyIcon(NimModify, ref data);
    }

    /// <summary>Pops the context menu at the cursor and returns the chosen command, or 0.</summary>
    public static int ShowMenu(IntPtr window, bool proxyRunning)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            AppendMenu(menu, MfString, new IntPtr(CmdOpen), "Открыть TokenHub");
            AppendMenu(menu, MfString, new IntPtr(CmdStatus), "Быстрый статус");
            AppendMenu(menu, MfString, new IntPtr(CmdKeysPage), "Ключи и пул");
            AppendMenu(menu, MfString, new IntPtr(CmdCheckKeys), "Проверить ключи");
            AppendMenu(menu, MfSeparator, IntPtr.Zero, string.Empty);
            // Exiting is always allowed: when no proxy is running there is nothing to
            // warn about, but the item must still be clickable (it used to be grayed out).
            AppendMenu(
                menu,
                MfString,
                new IntPtr(CmdExit),
                proxyRunning ? "Выход (прокси остановится)" : "Выход");

            GetCursorPos(out var cursor);
            SetForegroundWindow(window);

            int command = TrackPopupMenu(
                menu,
                TrackReturnCmd | TrackRightButton | TrackNotify,
                cursor.X,
                cursor.Y,
                0,
                window,
                IntPtr.Zero);

            // Required by the shell after TrackPopupMenu, otherwise the menu can stick.
            PostMessage(window, WmNull, IntPtr.Zero, IntPtr.Zero);
            return command;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static NotifyIconData NewData(IntPtr window, string tooltip) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = window,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        szTip = Clip(tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static IntPtr LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)
                && ExtractIconEx(exe, 0, out IntPtr large, out IntPtr small, 1) > 0)
            {
                if (large != IntPtr.Zero)
                {
                    DestroyIcon(large);
                }

                if (small != IntPtr.Zero)
                {
                    return small;
                }
            }
        }
        catch
        {
            // Fall through to the generic icon below.
        }

        // IDI_APPLICATION.
        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    private static string Clip(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
