using IrisAura.Island.Interop;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Shell;

/// <summary>Thin, testable-at-the-edges wrappers over the Win32 calls the shell needs.</summary>
internal static class Win32Shell
{
    public static PixelRect ToPixelRect(RECT r) => PixelRect.FromLtrb(r.Left, r.Top, r.Right, r.Bottom);

    public static MonitorDescriptor Describe(nint monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("GetMonitorInfo failed.");
        }

        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return new MonitorDescriptor(
            monitor,
            ToPixelRect(info.rcMonitor),
            ToPixelRect(info.rcWork),
            scale,
            (info.dwFlags & MONITORINFOF_PRIMARY) != 0);
    }

    public static MonitorDescriptor PrimaryMonitor() =>
        Describe(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));

    public static MonitorDescriptor MonitorAt(int x, int y) =>
        Describe(MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST));

    public static (int X, int Y) CursorPosition() =>
        GetCursorPos(out var p) ? (p.X, p.Y) : (int.MinValue, int.MinValue);

    public static PixelRect WindowRect(nint hwnd) =>
        GetWindowRect(hwnd, out var r) ? ToPixelRect(r) : default;

    public static UserNotificationState QueryNotificationState() =>
        SHQueryUserNotificationState(out var state) == 0 ? (UserNotificationState)state : UserNotificationState.Unknown;

    public static ForegroundWindowInfo? DescribeForeground(nint hwnd, Func<nint, bool> isIslandWindow)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
        {
            return null;
        }

        var root = GetAncestor(hwnd, GA_ROOT);
        if (root != 0)
        {
            hwnd = root;
        }

        var style = GetWindowLong(hwnd, GWL_STYLE);
        return new ForegroundWindowInfo(
            WindowRect(hwnd),
            MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST),
            FullscreenPolicy.ShellWindowClasses.Contains(NativeMethods.GetClassName(hwnd)),
            isIslandWindow(hwnd),
            IsZoomed(hwnd) && (style & WS_CAPTION) == WS_CAPTION);
    }

    /// <summary>
    /// Identifies the app owning the foreground window, or <see langword="null"/> for none / the Island itself.
    /// Reads only the process name and (for packaged apps) its AUMID; nothing about the window's content.
    /// </summary>
    public static unsafe Engine.ForegroundApp? DescribeForegroundApp(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var root = GetAncestor(hwnd, GA_ROOT);
        GetWindowThreadProcessId(root != 0 ? root : hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return null;
        }

        string name;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch (Exception)
        {
            return null; // exited
        }

        string? aumid = null;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != 0)
        {
            try
            {
                var buffer = stackalloc char[256];
                uint length = 256;
                if (GetApplicationUserModelId(handle, ref length, buffer) == 0 && length > 1)
                {
                    aumid = new string(buffer, 0, (int)length - 1);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return new Engine.ForegroundApp(name, aumid);
    }

    public static long GetExtendedStyle(nint hwnd) => GetWindowLong(hwnd, GWL_EXSTYLE);

    /// <summary>Adds and removes extended window styles; no-op when nothing changes.</summary>
    public static void UpdateExtendedStyle(nint hwnd, long add, long remove)
    {
        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        var updated = (current | add) & ~remove;
        if (updated != current)
        {
            SetWindowLong(hwnd, GWL_EXSTYLE, updated);
        }
    }

    public static void ReassertTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    public static void MoveWindow(nint hwnd, PixelRect rect) =>
        SetWindowPos(hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    /// <summary>Root window under a screen point (honours click-through and layered-window transparency).</summary>
    public static nint RootWindowAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new POINT { X = x, Y = y });
        if (hwnd == 0)
        {
            return 0;
        }

        var root = GetAncestor(hwnd, GA_ROOT);
        return root != 0 ? root : hwnd;
    }
}
