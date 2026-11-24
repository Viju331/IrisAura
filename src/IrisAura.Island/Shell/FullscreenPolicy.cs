namespace IrisAura.Island.Shell;

/// <summary>Values of <c>QUERY_USER_NOTIFICATION_STATE</c> (SHQueryUserNotificationState).</summary>
public enum UserNotificationState
{
    Unknown = 0,
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7,
}

/// <summary>What the full-screen check needs to know about the foreground window.</summary>
/// <param name="Rect">Window rectangle in screen pixels.</param>
/// <param name="Monitor">Monitor the window is mostly on.</param>
/// <param name="IsShellOrDesktop">Desktop, taskbar or other shell surface (never counts as full screen).</param>
/// <param name="IsIslandWindow">One of the Island's own windows.</param>
/// <param name="IsMaximizedWithCaption">A normal maximised window with a title bar (not full screen, even if an
/// auto-hiding taskbar lets it cover the whole monitor).</param>
public readonly record struct ForegroundWindowInfo(
    PixelRect Rect,
    nint Monitor,
    bool IsShellOrDesktop,
    bool IsIslandWindow,
    bool IsMaximizedWithCaption);

/// <summary>
/// Decides whether the Island hides because something is full screen (BLUEPRINT §8.3). Pure.
/// </summary>
public static class FullscreenPolicy
{
    /// <summary>Window classes of the desktop and shell; never treated as a full-screen app.</summary>
    public static readonly IReadOnlySet<string> ShellWindowClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    public static bool ShouldHide(
        UserNotificationState state,
        ForegroundWindowInfo? foreground,
        MonitorDescriptor islandMonitor,
        bool showOverFullscreen)
    {
        if (showOverFullscreen)
        {
            return false;
        }

        // Session-wide signals from the shell: full-screen app, D3D exclusive full screen, presentation mode.
        if (state is UserNotificationState.Busy
            or UserNotificationState.RunningD3DFullScreen
            or UserNotificationState.PresentationMode)
        {
            return true;
        }

        if (foreground is not { } fg || fg.IsShellOrDesktop || fg.IsIslandWindow || fg.IsMaximizedWithCaption)
        {
            return false;
        }

        // Only a window on the Island's own monitor that covers that whole monitor (borderless full screen).
        return fg.Monitor == islandMonitor.Handle && fg.Rect.Covers(islandMonitor.Bounds);
    }
}
