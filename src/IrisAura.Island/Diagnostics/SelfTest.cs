using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Media;
using IrisAura.Island.Shell;
using ActivityKind = IrisAura.Island.Activities.ActivityKind;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Diagnostics;

/// <summary>
/// In-process integration self-test for Phase 1 acceptance evidence (BLUEPRINT §8.8, §8.12, §10 Phase 1).
/// Run with <c>IrisAura.Island.exe --selftest report.json</c>. Measures startup, idle and media CPU,
/// memory and per-transition frame statistics; checks focus is never taken, click-through, placement,
/// display-change handling, full-screen hiding, reduced motion and keyboard focus mode.
/// It briefly shows two test windows (full-screen and maximised). No personal data is recorded.
/// </summary>
internal sealed class SelfTest(
    IslandWindow island,
    IslandController controller,
    DebugActivityProvider driver,
    ActivityProviderHost host,
    IslandOptions options,
    string outputPath,
    double startupMs)
{
    private static readonly TimeSpan BannerDuration = ActivityDefaults.Ttl(ActivityKind.Notification)!.Value;

    private const double IdleCpuTargetPercent = 0.3;
    private const double MediaCpuTargetPercent = 1.5;
    private const double MemoryTargetMb = 120;
    private const double StartupTargetMs = 800;
    private static readonly TimeSpan CpuWindow = TimeSpan.FromSeconds(10);

    private readonly List<CheckResult> _checks = [];
    private readonly Dictionary<string, object?> _metrics = [];
    private nint _baselineForeground;

    public sealed record CheckResult(string Name, string Outcome, string Detail);

    public async Task<int> RunAsync()
    {
        try
        {
            await RunCoreAsync();
        }
        catch (Exception ex)
        {
            Add("self-test ran without exceptions", false, ex.ToString());
        }
        finally
        {
            RestoreForeground();
        }

        Write();
        return _checks.Any(c => c.Outcome == "fail") ? 1 : 0;
    }

    private async Task RunCoreAsync()
    {
        var handle = island.Handle;
        _baselineForeground = GetForegroundWindow();
        await Task.Delay(2500);

        // Real media (Phase 3): recorded for information, then stopped so the checks below are deterministic.
        var real = controller.Layout.PersistentInOrder.Concat([controller.Layout.Transient]).OfType<IslandActivity>()
            .FirstOrDefault(a => a.ProviderId == MediaProvider.ProviderId);
        Info("real media session (Windows media controls)", real is null
            ? $"none playing or paused (provider {host.Status(MediaProvider.ProviderId)?.State})"
            : $"app {real.SourceAppId}, {(real.PausedSince is null ? "playing" : "paused")}");
        // System providers (Phase 4): states recorded, none may have failed; then every real provider is stopped
        // so the checks below see only the fake activities.
        foreach (var provider in host.Providers.Where(p => p.Id != DebugActivityProvider.ProviderId))
        {
            Info($"provider {provider.Id}", provider.IsEnabled
                ? $"{provider.State}{(provider.LastError is null ? string.Empty : " (" + provider.LastError + ")")}"
                : "switched off");
        }

        // Idle CPU with every real provider running (event-driven watchers, the 1 s microphone session check).
        if (controller.Layout.Main is null && controller.Layout.Transient is null)
        {
            var withProviders = await MeasureCpuMedianAsync(TimeSpan.FromSeconds(3), samples: 3);
            _metrics["idleCpuWithSystemProviders"] = withProviders;
            Add($"idle CPU with system providers running < {IdleCpuTargetPercent}%", withProviders.MedianPercent < IdleCpuTargetPercent, withProviders.ToString());
        }
        else
        {
            Info("idle CPU with system providers running", "skipped: a real activity is showing");
        }

        var faulted = host.Providers.Where(p => p.State == ProviderState.Faulted).Select(p => p.Id).ToList();
        Add("no provider failed at startup", faulted.Count == 0, faulted.Count == 0 ? "none faulted" : string.Join(", ", faulted));
        foreach (var provider in host.Providers.Where(p => p.Id != DebugActivityProvider.ProviderId))
        {
            host.Stop(provider.Id);
        }

        await SettleAsync();

        // ---- startup, styles, placement
        _metrics["startupToFirstFrameMs"] = Math.Round(startupMs, 1);
        Add($"startup to first frame ≤ {StartupTargetMs} ms", startupMs <= StartupTargetMs, $"{startupMs:0} ms");

        var style = island.ExtendedStyle;
        Add("window is a tool window (no Alt+Tab/taskbar)", (style & WS_EX_TOOLWINDOW) != 0, $"exstyle 0x{style:X}");
        Add("window never activates (WS_EX_NOACTIVATE)", (style & WS_EX_NOACTIVATE) != 0, $"exstyle 0x{style:X}");
        Add("window is layered (per-pixel alpha)", (style & WS_EX_LAYERED) != 0, $"exstyle 0x{style:X}");
        Add("window is topmost", (style & WS_EX_TOPMOST) != 0, $"exstyle 0x{style:X}");
        Add("idle pill is click-through", island.IsClickThrough && (style & WS_EX_TRANSPARENT) != 0, $"exstyle 0x{style:X}");
        Add("focus shortcut registered (Ctrl+Alt+Shift+I)", island.HotkeyRegistered, island.HotkeyRegistered ? "registered" : "in use by another app");
        Add("timer shortcut registered (Ctrl+Alt+Shift+T)", island.TimerHotkeyRegistered, island.TimerHotkeyRegistered ? "registered" : "in use by another app");

        var monitor = island.CurrentMonitor;
        var expected = PlacementCalculator.Compute(monitor, IslandLayout.WindowWidth, IslandLayout.WindowHeight);
        var actual = Win32Shell.WindowRect(handle);
        Add("placed at top-centre of the work area", actual == expected, $"expected {expected}, actual {actual}, work area {monitor.WorkArea}, scale {monitor.Scale:0.##}");

        // ---- idle CPU (after warm-up, median of samples) and memory
        var idle = await MeasureCpuMedianAsync(TimeSpan.FromSeconds(5), samples: 3);
        _metrics["idleCpu"] = idle;
        Add($"idle CPU < {IdleCpuTargetPercent}%", idle.MedianPercent < IdleCpuTargetPercent, idle.ToString());

        var (workingSet, privateBytes) = MeasureMemory();
        _metrics["workingSetMb"] = workingSet;
        _metrics["privateMb"] = privateBytes;
        Add($"working set < {MemoryTargetMb} MB", workingSet < MemoryTargetMb, $"{workingSet:0.0} MB working set, {privateBytes:0.0} MB private");

        // ---- transitions: frame stats + focus never taken
        var loadStart = SystemTimes();
        await TransitionAsync("idle→compact (media)", () => driver.Show(FakeScenario.Media));
        await TransitionAsync("compact→expanded (pin)", island.TogglePin);
        await TransitionAsync("expanded→compact (unpin)", island.TogglePin);
        await TransitionAsync("compact→split (timer + media)", () => driver.Show(FakeScenario.MediaAndTimer));
        await TransitionAsync("split→expanded generic (pin)", island.TogglePin);
        await TransitionAsync("expanded→split (unpin)", island.TogglePin);
        Add("arbiter: timer (60) in the pill, media (50) in the bubble",
            controller.Layout.Main?.Id == DebugActivityProvider.TimerId && controller.Layout.Bubble?.Id == DebugActivityProvider.MediaId,
            $"main {controller.Layout.Main?.Id}, bubble {controller.Layout.Bubble?.Id}");
        await TransitionAsync("split→banner", driver.Notify);
        Add("transient notification takes the pill", island.VisualState == IslandVisualState.Banner, $"state {island.VisualState}");
        await Task.Delay(BannerDuration + TimeSpan.FromMilliseconds(300));
        await WaitForAnimationAsync();
        Add("banner expires and the previous layout returns", island.VisualState == IslandVisualState.Split, $"state {island.VisualState}");
        await TransitionAsync("swap (bubble click)", controller.Swap);
        Add("swap puts the bubble activity in the pill",
            controller.Layout.Main?.Id == DebugActivityProvider.MediaId && controller.Layout.Bubble?.Id == DebugActivityProvider.TimerId,
            $"main {controller.Layout.Main?.Id}, bubble {controller.Layout.Bubble?.Id}");
        await TransitionAsync("shake", island.Shake);
        await TransitionAsync("split→idle", () => driver.Show(FakeScenario.Idle));
        _metrics["machineLoadDuringTransitionsPercent"] = LoadSince(loadStart);

        // ---- media + waveform CPU (after warm-up, median of samples)
        driver.Show(FakeScenario.Media);
        await SettleAsync();
        var media = await MeasureCpuMedianAsync(TimeSpan.FromSeconds(5), samples: 3);
        _metrics["mediaWaveformCpu"] = media;
        Add($"CPU with media + waveform < {MediaCpuTargetPercent}%", media.MedianPercent < MediaCpuTargetPercent, media + " (fake 30 Hz waveform)");

        // ---- click-through probes (compact media pill is showing)
        await ClickThroughProbesAsync(handle);

        // ---- display change handling
        await DisplayChangeAsync(handle, expected);

        // ---- reduced motion
        options.ReducedMotion = ReducedMotionMode.On;
        island.ApplyOptions();
        driver.Show(FakeScenario.Timer);
        await SettleAsync();
        island.TogglePin(); // compact → expanded: a real size change, applied synchronously
        var snapped = island.CurrentGeometry == island.TargetGeometry && island.TargetGeometry != IslandLayout.Compact;
        var reduced = await WaitForAnimationAsync();
        _metrics["frames: reduced-motion cross-fade"] = reduced;
        Add("reduced motion: size snaps, content cross-fades", snapped && reduced.Frames * reduced.AverageMs <= 400,
            $"geometry snapped {snapped} ({island.TargetGeometry.Width:0}×{island.TargetGeometry.Height:0}); cross-fade {reduced}");
        island.TogglePin();
        await WaitForAnimationAsync();
        options.ReducedMotion = ReducedMotionMode.FollowWindows;
        island.ApplyOptions();

        // ---- activity engine integration
        await EngineAsync();

        // ---- privacy dots (Phase 4): drawn beside whatever is shown, never in the pill
        driver.Show(FakeScenario.Media);
        await SettleAsync();
        driver.PrivacyIndicator(microphone: true, camera: false);
        await SettleAsync();
        Add("mic indicator: dot drawn, pill keeps its activity",
            island.MicrophoneDotVisible && !island.CameraDotVisible && controller.Layout.Main?.Id == DebugActivityProvider.MediaId,
            $"mic dot {island.MicrophoneDotVisible}, camera dot {island.CameraDotVisible}, main {controller.Layout.Main?.Id}");
        driver.PrivacyIndicator(microphone: false, camera: false);
        await SettleAsync();
        Add("indicator ended: dot removed", !island.MicrophoneDotVisible && !island.CameraDotVisible,
            $"mic dot {island.MicrophoneDotVisible}, camera dot {island.CameraDotVisible}");

        // ---- full screen
        await FullscreenAsync(monitor);

        // ---- keyboard focus mode (programmatic; the real trigger is WM_HOTKEY)
        await FocusModeAsync(handle);

        driver.Show(FakeScenario.Idle);
        await WaitForAnimationAsync();
    }

    /// <summary>
    /// Quick performance-only run (<c>--perf report.json</c>): idle CPU, media + waveform CPU (3 samples)
    /// and a few transitions. Used to compare rendering changes; ~45 s.
    /// </summary>
    public async Task<int> RunPerfAsync()
    {
        try
        {
            _metrics["startupToFirstFrameMs"] = Math.Round(startupMs, 1);
            await Task.Delay(1000);
            host.Stop(MediaProvider.ProviderId); // measure the fake waveform only
            _metrics["idleCpu"] = await MeasureCpuMedianAsync(TimeSpan.FromSeconds(5), samples: 2);
            driver.Show(FakeScenario.Media);
            await WaitForAnimationAsync();
            _metrics["mediaWaveformCpu"] = await MeasureCpuMedianAsync(TimeSpan.FromSeconds(5), samples: 3);
            await TransitionAsync("compact→expanded (pin)", island.TogglePin);
            await TransitionAsync("expanded→compact (unpin)", island.TogglePin);
            await TransitionAsync("compact→split (timer + media)", () => driver.Show(FakeScenario.MediaAndTimer));
            await TransitionAsync("split→idle", () => driver.Show(FakeScenario.Idle));
        }
        catch (Exception ex)
        {
            Add("perf run without exceptions", false, ex.ToString());
        }

        Write();
        return 0;
    }

    private async Task EngineAsync()
    {
        Add("tray icon added to the notification area", controller.TrayIconVisible, controller.TrayIconVisible ? "Shell_NotifyIcon NIM_ADD succeeded" : "not added");

        // Quiet mode suppresses notification banners only.
        driver.Show(FakeScenario.Media);
        await SettleAsync();
        controller.SetQuietMode(true);
        driver.Notify();
        await SettleAsync();
        var suppressed = island.VisualState != IslandVisualState.Banner && controller.Layout.Suppressed.Count == 1;
        driver.Charging();
        await SettleAsync();
        var chargingShown = island.VisualState == IslandVisualState.Banner;
        controller.SetQuietMode(false);
        Add("quiet mode suppresses notification banners only", suppressed && chargingShown,
            $"notification suppressed {suppressed}; charging HUD still shown {chargingShown}");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await WaitForAnimationAsync();

        // Provider failure is isolated: its activities disappear, the Island keeps running, restart works.
        driver.Show(FakeScenario.MediaAndTimer);
        await SettleAsync();
        driver.Fail();
        await SettleAsync();
        var status = host.Status(DebugActivityProvider.ProviderId);
        Add("a failing provider is disabled and its activities removed",
            status?.State == ProviderState.Faulted && controller.Layout.Main is null && island.VisualState == IslandVisualState.Idle,
            $"state {status?.State}, error \"{status?.LastError}\", island {island.VisualState}");

        host.Start(DebugActivityProvider.ProviderId);
        driver.Show(FakeScenario.Media);
        await SettleAsync();
        Add("a failed provider can be restarted", host.Status(DebugActivityProvider.ProviderId)?.State == ProviderState.Running
            && controller.Layout.Main?.Id == DebugActivityProvider.MediaId, $"main {controller.Layout.Main?.Id}");
    }

    /// <summary>Lets posted provider operations apply and the resulting animation finish.</summary>
    private async Task SettleAsync()
    {
        await Task.Delay(80);
        await WaitForAnimationAsync();
    }

    private async Task TransitionAsync(string name, Action action)
    {
        var foregroundBefore = GetForegroundWindow();
        action();
        await Task.Delay(60); // provider operations are posted to the dispatcher
        var summary = await WaitForAnimationAsync();
        _metrics["frames: " + name] = summary;

        var foregroundAfter = GetForegroundWindow();
        var style = island.ExtendedStyle;
        Add($"no focus taken: {name}",
            foregroundAfter == foregroundBefore && foregroundAfter != island.Handle && (style & WS_EX_NOACTIVATE) != 0,
            $"foreground unchanged {foregroundAfter == foregroundBefore}");
        Add($"no dropped frames (60 fps): {name}", summary.Frames > 0 && summary.Dropped == 0, summary.ToString());
    }

    private async Task ClickThroughProbesAsync(nint handle)
    {
        island.SuspendPolling(true);
        try
        {
            var window = Win32Shell.WindowRect(handle);
            var scale = VisualTreeHelper.GetDpi(island).DpiScaleX;
            var pill = IslandLayout.PillRect(island.CurrentGeometry);
            (int X, int Y) ToScreen(double x, double y) =>
                (window.Left + (int)Math.Round(x * scale), window.Top + (int)Math.Round(y * scale));

            var centre = ToScreen(pill.Left + (pill.Width / 2), pill.Top + (pill.Height / 2));
            var empty = ToScreen(12, IslandLayout.WindowHeight - 12);
            var shadow = ToScreen(pill.Left + (pill.Width / 2), pill.Bottom + 5);

            island.ForceClickThrough(true);
            await Task.Delay(100);
            Add("cursor elsewhere: clicks on the pill area pass through to the window below",
                Win32Shell.RootWindowAt(centre.X, centre.Y) != handle, "WS_EX_TRANSPARENT set");

            island.ForceClickThrough(false);
            await Task.Delay(100);
            Add("cursor over pill: the pill receives clicks",
                Win32Shell.RootWindowAt(centre.X, centre.Y) == handle, "WS_EX_TRANSPARENT cleared");
            Add("empty (alpha 0) window area passes clicks even without WS_EX_TRANSPARENT",
                Win32Shell.RootWindowAt(empty.X, empty.Y) != handle, $"probe at {empty}");
            var shadowCatches = Win32Shell.RootWindowAt(shadow.X, shadow.Y) == handle;
            Info("shadow pixels without WS_EX_TRANSPARENT",
                shadowCatches ? "shadow catches clicks — the WS_EX_TRANSPARENT toggle is required (as designed)" : "shadow passes clicks");
        }
        finally
        {
            island.ForceClickThrough(true);
            island.SuspendPolling(false);
        }
    }

    private async Task DisplayChangeAsync(nint handle, PixelRect expected)
    {
        Win32Shell.MoveWindow(handle, expected with { Left = expected.Left + 137, Top = expected.Top + 91 });
        await Task.Delay(150);
        PostMessage(handle, WM_DISPLAYCHANGE, 32, 0);
        await Task.Delay(500);
        var afterDisplay = Win32Shell.WindowRect(handle);
        Add("re-placed after WM_DISPLAYCHANGE", afterDisplay == expected, $"expected {expected}, actual {afterDisplay}");

        Win32Shell.MoveWindow(handle, expected with { Left = expected.Left - 53 });
        await Task.Delay(150);
        var seenBefore = island.SettingChangeMessages;
        // WM_SETTINGCHANGE cannot be posted (Windows rejects it); it is always sent.
        SendMessage(handle, WM_SETTINGCHANGE, 0x2F /* SPI_SETWORKAREA */, 0);
        await Task.Delay(500);
        var afterWorkArea = Win32Shell.WindowRect(handle);
        Add("re-placed after work-area change (WM_SETTINGCHANGE)", afterWorkArea == expected,
            $"expected {expected}, actual {afterWorkArea}; window procedure saw {island.SettingChangeMessages - seenBefore} WM_SETTINGCHANGE");
    }

    private async Task FullscreenAsync(MonitorDescriptor monitor)
    {
        // Borderless window covering the monitor (like a full-screen video or borderless game).
        var fullscreen = CreateProbeWindow("IrisAura self-test — full-screen check (closes automatically)", WindowStyle.None);
        fullscreen.Show();
        var fsHandle = new WindowInteropHelper(fullscreen).Handle;
        SetWindowPos(fsHandle, 0, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height, SWP_NOOWNERZORDER);
        fullscreen.Activate();
        SetForegroundWindow(fsHandle);
        await Task.Delay(1500);

        // Independent of who has the foreground: does the policy classify this window correctly?
        Add("policy classifies a borderless monitor-covering window as full screen",
            PolicyVerdict(fsHandle, monitor), Describe(fsHandle));

        if (GetForegroundWindow() == fsHandle)
        {
            Add("hides when a borderless window covers the monitor", island.IsHiddenForFullscreen, "full-screen probe in foreground");
        }
        else
        {
            Inconclusive("hides when a borderless window covers the monitor", "Windows did not give the probe window the foreground");
        }

        fullscreen.Close();
        await Task.Delay(1500);
        Add("visible again after the full-screen window closes", !island.IsHiddenForFullscreen, $"hidden {island.IsHiddenForFullscreen}");

        // A normal maximised window (with title bar) is not full screen.
        // Must be resizable: a maximised window without a resize frame covers the taskbar and Windows
        // itself reports it as full screen (QUNS_BUSY).
        var maximised = CreateProbeWindow("IrisAura self-test — maximised check (closes automatically)", WindowStyle.SingleBorderWindow);
        maximised.ResizeMode = ResizeMode.CanResize;
        maximised.WindowState = WindowState.Maximized;
        maximised.Show();
        var mxHandle = new WindowInteropHelper(maximised).Handle;
        maximised.Activate();
        SetForegroundWindow(mxHandle);
        await Task.Delay(1500);

        Add("policy classifies a maximised window with a title bar as not full screen",
            !PolicyVerdict(mxHandle, monitor), Describe(mxHandle));

        if (GetForegroundWindow() == mxHandle)
        {
            Add("stays visible over a normal maximised window", !island.IsHiddenForFullscreen, $"hidden {island.IsHiddenForFullscreen}");
        }
        else
        {
            Inconclusive("stays visible over a normal maximised window", "Windows did not give the probe window the foreground");
        }

        maximised.Close();
        await Task.Delay(800);
        RestoreForeground();
        await Task.Delay(800);
    }

    private async Task FocusModeAsync(nint handle)
    {
        var before = GetForegroundWindow();
        island.ToggleFocusMode();
        await Task.Delay(300);
        var activatable = (island.ExtendedStyle & WS_EX_NOACTIVATE) == 0;
        Add("focus mode makes the Island activatable", island.IsFocusMode && activatable, $"focus mode {island.IsFocusMode}");
        Info("focus mode took the foreground (programmatic call)", GetForegroundWindow() == handle
            ? "yes"
            : "no — Windows refused SetForegroundWindow without user input; the real trigger (WM_HOTKEY) must be checked by hand");

        island.ToggleFocusMode();
        await Task.Delay(300);
        Add("leaving focus mode restores no-activate", !island.IsFocusMode && (island.ExtendedStyle & WS_EX_NOACTIVATE) != 0, $"focus mode {island.IsFocusMode}");
        Info("focus returned to the previous window", GetForegroundWindow() == before ? "yes" : "no");
    }

    /// <summary>Full-screen verdict for a window as if it were in the foreground (shell state ignored).</summary>
    private bool PolicyVerdict(nint hwnd, MonitorDescriptor monitor) =>
        FullscreenPolicy.ShouldHide(
            UserNotificationState.AcceptsNotifications,
            Win32Shell.DescribeForeground(hwnd, h => h == island.Handle),
            monitor,
            showOverFullscreen: false);

    private string Describe(nint hwnd) =>
        Win32Shell.DescribeForeground(hwnd, h => h == island.Handle) is { } info
            ? $"rect {info.Rect}, maximised-with-caption {info.IsMaximizedWithCaption}, shell state {Win32Shell.QueryNotificationState()}"
            : "window not visible";

    private Window CreateProbeWindow(string text, WindowStyle style) => new()
    {
        Title = text,
        WindowStyle = style,
        ResizeMode = ResizeMode.NoResize,
        ShowActivated = true,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Width = 640,
        Height = 360,
        Background = (Brush)island.FindResource("Aura.Surface"),
        Content = new TextBlock
        {
            Text = text,
            FontSize = 20,
            Foreground = (Brush)island.FindResource("Aura.Frost"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private async Task<FrameSummary> WaitForAnimationAsync()
    {
        var timeout = Stopwatch.StartNew();
        while (island.IsAnimating && timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(16);
        }

        await Task.Delay(120);
        return island.LastAnimation;
    }

    /// <summary>
    /// Process CPU as a share of all logical processors (Task Manager convention), measured after a
    /// warm-up so JIT/tiered compilation is excluded, reported as the median of several samples together
    /// with the machine's overall load (other apps) during the samples.
    /// </summary>
    private static async Task<CpuMeasurement> MeasureCpuMedianAsync(TimeSpan warmUp, int samples)
    {
        await Task.Delay(warmUp);
        var values = new List<double>();
        var loads = new List<double>();
        for (var i = 0; i < samples; i++)
        {
            var (process, machine) = await MeasureCpuAsync();
            values.Add(Math.Round(process, 3));
            loads.Add(machine);
        }

        var sorted = values.Order().ToList();
        return new CpuMeasurement(sorted[sorted.Count / 2], values, Math.Round(loads.Average(), 1), warmUp.TotalSeconds, CpuWindow.TotalSeconds);
    }

    private static async Task<(double ProcessPercent, double MachinePercent)> MeasureCpuAsync()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var systemBefore = SystemTimes();
        var wall = Stopwatch.StartNew();
        await Task.Delay(CpuWindow);
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        return (cpu / (wall.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100, LoadSince(systemBefore));
    }

    private static (long Idle, long Total) SystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel + user) : (0, 0);

    /// <summary>Whole-machine CPU load (all processes) since <paramref name="start"/>, in percent.</summary>
    private static double LoadSince((long Idle, long Total) start)
    {
        var now = SystemTimes();
        var total = now.Total - start.Total;
        return total > 0 ? Math.Round((1 - ((double)(now.Idle - start.Idle) / total)) * 100, 1) : 0;
    }

    public sealed record CpuMeasurement(
        double MedianPercent,
        IReadOnlyList<double> SamplesPercent,
        double MachineLoadPercent,
        double WarmUpSeconds,
        double SampleSeconds)
    {
        public override string ToString() =>
            $"median {MedianPercent:0.000}% (samples {string.Join(" / ", SamplesPercent.Select(s => s.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)))}, " +
            $"{SampleSeconds:0} s each after {WarmUpSeconds:0} s warm-up); whole-machine CPU load {MachineLoadPercent:0}%";
    }

    private static (double WorkingSetMb, double PrivateMb) MeasureMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return (Math.Round(process.WorkingSet64 / (1024.0 * 1024), 1), Math.Round(process.PrivateMemorySize64 / (1024.0 * 1024), 1));
    }

    private void RestoreForeground()
    {
        if (_baselineForeground != 0 && IsWindow(_baselineForeground))
        {
            SetForegroundWindow(_baselineForeground);
        }
    }

    private void Add(string name, bool passed, string detail) => _checks.Add(new CheckResult(name, passed ? "pass" : "fail", detail));

    private void Info(string name, string detail) => _checks.Add(new CheckResult(name, "info", detail));

    private void Inconclusive(string name, string detail) => _checks.Add(new CheckResult(name, "inconclusive", detail));

    private void Write()
    {
        var primary = Win32Shell.PrimaryMonitor();
        var report = new
        {
            tool = "IrisAura.Island --selftest",
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            configuration =
#if DEBUG
                "Debug",
#else
                "Release",
#endif
            timestampUtc = DateTime.UtcNow,
            machine = new
            {
                os = Environment.OSVersion.VersionString,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processors = Environment.ProcessorCount,
                monitors = GetSystemMetrics(SM_CMONITORS),
                primaryScalePercent = Math.Round(primary.Scale * 100),
                primaryBounds = primary.Bounds.ToString(),
                primaryWorkArea = primary.WorkArea.ToString(),
                windowsAnimationsEnabled = SystemParameters.ClientAreaAnimation,
            },
            summary = new
            {
                passed = _checks.Count(c => c.Outcome == "pass"),
                failed = _checks.Count(c => c.Outcome == "fail"),
                inconclusive = _checks.Count(c => c.Outcome == "inconclusive"),
                info = _checks.Count(c => c.Outcome == "info"),
            },
            metrics = _metrics,
            checks = _checks,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
