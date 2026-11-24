using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IrisAura.Island.Interop;

/// <summary>
/// Documented Win32 APIs used by the Island shell (BLUEPRINT §8.3). Hand-written
/// <see cref="LibraryImportAttribute"/> signatures; no undocumented APIs.
/// </summary>
internal static unsafe partial class NativeMethods
{
    // Window long indexes
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    // Window styles
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;

    // SetWindowPos
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    // Messages
    public const int WM_ACTIVATE = 0x0006;
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DPICHANGED = 0x02E0;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int MA_NOACTIVATE = 3;

    // Hotkeys
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_NOREPEAT = 0x4000;

    // Monitors
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;
    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITORINFOF_PRIMARY = 0x00000001;
    public const int MDT_EFFECTIVE_DPI = 0;

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // GetAncestor
    public const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr64(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static partial int GetWindowLong32(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr64(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static partial int SetWindowLong32(nint hwnd, int index, int value);

    public static long GetWindowLong(nint hwnd, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : GetWindowLong32(hwnd, index);

    public static void SetWindowLong(nint hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, index, (nint)value);
        }
        else
        {
            SetWindowLong32(hwnd, index, unchecked((int)value));
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("shell32.dll")]
    public static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    public static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint module,
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback,
        uint processId,
        uint threadId,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    public const int SM_CMONITORS = 80;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    /// <summary>For messages Windows will not let you post (e.g. WM_SETTINGCHANGE).</summary>
    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial int GetSystemMetrics(int index);

    /// <summary>System-wide idle/kernel/user time (FILETIME ticks); kernel time includes idle time.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>Returns the window class name, or an empty string.</summary>
    public static string GetClassName(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = GetClassName(hwnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    /// <summary>Raised on the UI thread when the foreground window changes (out-of-context WinEvent).</summary>
    public static event Action<nint>? ForegroundChanged;

    private static nint s_foregroundHook;

    public static void StartForegroundHook()
    {
        if (s_foregroundHook != 0)
        {
            return;
        }

        s_foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, &OnWinEvent, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public static void StopForegroundHook()
    {
        if (s_foregroundHook != 0)
        {
            UnhookWinEvent(s_foregroundHook);
            s_foregroundHook = 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint threadId, uint time)
    {
        // Never let an exception cross the native callback boundary.
        try
        {
            if (eventType == EVENT_SYSTEM_FOREGROUND && objectId == 0)
            {
                ForegroundChanged?.Invoke(hwnd);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("IrisAura.Island foreground hook: " + ex);
        }
    }
}
