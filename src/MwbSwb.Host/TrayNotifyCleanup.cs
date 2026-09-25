using System.Runtime.InteropServices;

namespace MwbSwb.Host;

/// <summary>
/// Force Explorer to drop orphaned NotifyIcon glyphs left by Kill/crash.
/// Ghost icons remain until the notification area is "poked".
/// </summary>
internal static class TrayNotifyCleanup
{
    private const uint WmMousemove = 0x0200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    public static void RefreshNotificationArea()
    {
        try
        {
            PokeTray(FindWindow("Shell_TrayWnd", null));
            PokeTray(FindWindow("NotifyIconOverflowWindow", null));
        }
        catch
        {
            /* best-effort */
        }
    }

    private static void PokeTray(IntPtr tray)
    {
        if (tray == IntPtr.Zero) return;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) notify = tray;
        var pager = FindWindowEx(notify, IntPtr.Zero, "SysPager", null);
        var toolbar = FindWindowEx(pager != IntPtr.Zero ? pager : notify, IntPtr.Zero, "ToolbarWindow32", null);
        if (toolbar == IntPtr.Zero)
            toolbar = FindWindowEx(notify, IntPtr.Zero, "ToolbarWindow32", null);
        if (toolbar == IntPtr.Zero) return;

        if (!GetClientRect(toolbar, out var rect)) return;
        for (var y = 0; y < Math.Max(rect.Bottom, 16); y += 8)
        {
            for (var x = 0; x < Math.Max(rect.Right, 16); x += 8)
            {
                var lp = (IntPtr)((y << 16) | (x & 0xFFFF));
                SendMessage(toolbar, WmMousemove, IntPtr.Zero, lp);
            }
        }
    }
}