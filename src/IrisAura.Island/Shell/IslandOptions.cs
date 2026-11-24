namespace IrisAura.Island.Shell;

/// <summary>Reduced-motion source: follow Windows, or force on/off (debug).</summary>
public enum ReducedMotionMode
{
    FollowWindows,
    On,
    Off,
}

/// <summary>
/// Shell options (BLUEPRINT §8.3, §8.7). Phase 1 keeps them in memory with the blueprint defaults;
/// the Island settings window and persistence arrive with §8.7.
/// </summary>
public sealed class IslandOptions
{
    public MonitorMode Monitor { get; set; } = MonitorMode.Primary;

    /// <summary>"Show over full-screen video" — off by default.</summary>
    public bool ShowOverFullscreen { get; set; }

    public ReducedMotionMode ReducedMotion { get; set; } = ReducedMotionMode.FollowWindows;

    /// <summary>Hover delay before expanding (100–500 ms in settings; 150 ms default).</summary>
    public TimeSpan HoverDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Delay before collapsing after the cursor leaves.</summary>
    public TimeSpan LeaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Keyboard-focus hotkey: Ctrl+Alt+Shift+I (never uses the Windows key).</summary>
    public uint FocusHotkeyModifiers { get; set; } =
        Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT | Interop.NativeMethods.MOD_SHIFT;

    public uint FocusHotkeyKey { get; set; } = 0x49; // 'I'

    public string FocusHotkeyText => "Ctrl+Alt+Shift+I";
}
