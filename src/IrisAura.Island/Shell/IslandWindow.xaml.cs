using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IrisAura.Island.Diagnostics;
using IrisAura.Island.Interop;
using IrisAura.Island.Motion;
using Microsoft.Win32;
using static IrisAura.Island.Interop.NativeMethods;

namespace IrisAura.Island.Shell;

/// <summary>
/// The Island shell window (BLUEPRINT §8.3, §8.4):
/// <list type="bullet">
/// <item>Topmost, tool window, never activates (WS_EX_NOACTIVATE + WM_MOUSEACTIVATE → MA_NOACTIVATE).</item>
/// <item>Click-through: layered-window alpha passes clicks through empty pixels; WS_EX_TRANSPARENT is set
///   whenever the cursor is outside the pill (polled at 30 Hz), so the shadow never catches clicks.</item>
/// <item>Placed at the top-centre of the work area of the primary (or cursor's) monitor, per-monitor DPI aware.</item>
/// <item>Hides over full-screen apps; re-asserts topmost on foreground changes.</item>
/// <item>Keyboard focus mode via a global hotkey without the Windows key.</item>
/// <item>Spring morphs rendered from CompositionTarget.Rendering only while something animates.</item>
/// </list>
/// </summary>
public partial class IslandWindow : Window
{
    private const int FocusHotkeyId = 0x4941;
    private const int TimerHotkeyId = 0x4942;
    private const double PrivacyDotGap = 7;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 30);
    private static readonly TimeSpan FarPollInterval = TimeSpan.FromMilliseconds(100);
    private const double NearCursorMargin = 120;
    private static readonly TimeSpan FullscreenInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MonitorFollowDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ErrorFlashDuration = TimeSpan.FromMilliseconds(300);

    private readonly IslandOptions _options;
    private readonly MorphController _morph = new(IslandLayout.Idle);
    private readonly HoverIntent _hover = new();
    private readonly FrameStats _frames = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _fullscreenTimer;
    private readonly HashSet<nint> _ownedWindows = [];
    private readonly HashSet<IIslandActivityView> _visibleActivities = [];
    private readonly ScaleTransform _incomingScaleA = new(1, 1);
    private readonly TranslateTransform _incomingShiftA = new();
    private readonly ScaleTransform _incomingScaleB = new(1, 1);
    private readonly TranslateTransform _incomingShiftB = new();

    private nint _hwnd;
    private HwndSource? _source;
    private IslandPresentation _presentation = IslandPresentation.Idle;
    private (IIslandActivityView? Activity, PresentationMode Mode) _pillContentKey;
    private IIslandActivityView? _bubbleActivity;
    private ContentPresenter _incoming;
    private ContentPresenter _outgoing;
    private bool _renderingHooked;
    private TimeSpan _lastRenderingTime;
    private TimeSpan? _shakeStartedAt;
    private TimeSpan? _errorFlashUntil;
    private double _shakeOffset;
    private bool _clickThrough = true;
    private bool _pollingSuspended;
    private bool _focusMode;
    private nint _focusReturnWindow;
    private bool _hidden;
    private bool _fullscreenHidden;
    private bool _userHidden;
    private nint _pendingMonitor;
    private TimeSpan _pendingMonitorSince;
    private string _announcement = string.Empty;

    public IslandWindow(IslandOptions options)
    {
        _options = options;
        InitializeComponent();

        Width = IslandLayout.WindowWidth;
        Height = IslandLayout.WindowHeight;
        _incoming = ContentA;
        _outgoing = ContentB;
        ContentA.RenderTransformOrigin = new Point(0.5, 0.5);
        ContentB.RenderTransformOrigin = new Point(0.5, 0.5);
        ContentA.RenderTransform = new TransformGroup { Children = { _incomingScaleA, _incomingShiftA } };
        ContentB.RenderTransform = new TransformGroup { Children = { _incomingScaleB, _incomingShiftB } };

        _hover.EnterDelay = options.HoverDelay;
        _hover.LeaveDelay = options.LeaveDelay;
        _morph.ReducedMotion = IsReducedMotion;
        _morph.SnapTo(IslandLayout.Idle);
        ApplyGeometry();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => Poll();
        _fullscreenTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = FullscreenInterval };
        _fullscreenTimer.Tick += (_, _) => EvaluateFullscreen();

        PillContent.MouseLeftButtonUp += OnPillClicked;
        PillContent.MouseWheel += OnWheel;
        Bubble.MouseWheel += OnWheel;
        PillContent.MouseDown += OnPillMouseDown;
        PillContent.MouseRightButtonUp += OnRightClick;
        Bubble.MouseRightButtonUp += OnRightClick;
        Bubble.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            BubbleClicked?.Invoke(this, EventArgs.Empty);
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => ExitFocusMode(restoreFocus: false);
        IsKeyboardFocusWithinChanged += (_, _) => UpdateBorder();
    }

    /// <summary>The split bubble was clicked (swap request).</summary>
    public event EventHandler? BubbleClicked;

    /// <summary>The cursor entered (true) or left (false) the interactive Island.</summary>
    public event EventHandler<bool>? PointerOverChanged;

    /// <summary>An animation finished; carries its frame statistics.</summary>
    public event EventHandler<FrameSummary>? AnimationCompleted;

    /// <summary>Click on the compact (not expanded) pill. BLUEPRINT §8.5: open the source app.</summary>
    public event EventHandler? PillClicked;

    /// <summary>Mouse wheel over the pill (delta: positive = away from the user). BLUEPRINT §8.5: cycle activities.</summary>
    public event EventHandler<int>? WheelScrolled;

    /// <summary>Middle click on the pill. BLUEPRINT §8.5: play/pause media.</summary>
    public event EventHandler? MiddleClicked;

    /// <summary>Right click on the pill or bubble, with the cursor position in screen pixels.</summary>
    public event EventHandler<(int X, int Y)>? ContextMenuRequested;

    /// <summary>The timer shortcut (Ctrl+Alt+Shift+T) was pressed.</summary>
    public event EventHandler? TimerHotkeyPressed;

    /// <summary>The focus shortcut was pressed while the user had hidden the Island (show it again).</summary>
    public event EventHandler? HotkeyPressedWhileHidden;

    /// <summary>Something shown in the debug panel changed.</summary>
    public event EventHandler? StatusChanged;

    public nint Handle => _hwnd;

    public IslandVisualState VisualState { get; private set; } = IslandVisualState.Idle;

    public MonitorDescriptor CurrentMonitor { get; private set; }

    public bool IsClickThrough => _clickThrough;

    public bool IsHiddenForFullscreen => _fullscreenHidden;

    /// <summary>Hidden by the user ("Hide for 1 hour").</summary>
    public bool IsUserHidden => _userHidden;

    public bool IsFocusMode => _focusMode;

    public bool IsAnimating => _renderingHooked;

    public bool IsPointerOver => _hover.IsInside;

    public bool IsPinned => _hover.IsPinned;

    public bool HotkeyRegistered { get; private set; }

    public bool TimerHotkeyRegistered { get; private set; }

    /// <summary>Microphone dot shown (BLUEPRINT §8.6.7).</summary>
    public bool MicrophoneDotVisible => MicDot.Visibility == Visibility.Visible;

    /// <summary>Camera dot shown.</summary>
    public bool CameraDotVisible => CameraDot.Visibility == Visibility.Visible;

    public FrameSummary LastAnimation { get; private set; }

    public PillGeometry CurrentGeometry => _morph.Current;

    public PillGeometry TargetGeometry => _morph.Target;

    /// <summary>Diagnostics: number of WM_SETTINGCHANGE messages the window procedure has seen.</summary>
    internal int SettingChangeMessages { get; private set; }

    public bool IsReducedMotion => _options.ReducedMotion switch
    {
        ReducedMotionMode.On => true,
        ReducedMotionMode.Off => false,
        _ => !SystemParameters.ClientAreaAnimation,
    };

    // ---------------------------------------------------------------- lifecycle

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // Tool window (no Alt+Tab, no taskbar), never activates, click-through until the cursor is over the pill.
        Win32Shell.UpdateExtendedStyle(_hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, WS_EX_APPWINDOW);
        _clickThrough = true;

        Reposition();

        HotkeyRegistered = RegisterHotKey(_hwnd, FocusHotkeyId, _options.FocusHotkeyModifiers | MOD_NOREPEAT, _options.FocusHotkeyKey);
        if (!HotkeyRegistered)
        {
            Trace.TraceWarning($"IrisAura.Island: focus shortcut {_options.FocusHotkeyText} is in use by another app; left unbound.");
        }

        TimerHotkeyRegistered = RegisterHotKey(_hwnd, TimerHotkeyId, _options.TimerHotkeyModifiers | MOD_NOREPEAT, _options.TimerHotkeyKey);
        if (!TimerHotkeyRegistered)
        {
            Trace.TraceWarning($"IrisAura.Island: timer shortcut {_options.TimerHotkeyText} is in use by another app; left unbound.");
        }

        NativeMethods.ForegroundChanged += OnForegroundChanged;
        NativeMethods.StartForegroundHook();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        _pollTimer.Start();
        _fullscreenTimer.Start();
        EvaluateFullscreen();
    }

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();
        _fullscreenTimer.Stop();
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }

        NativeMethods.ForegroundChanged -= OnForegroundChanged;
        NativeMethods.StopForegroundHook();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        if (HotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, FocusHotkeyId);
        }

        if (TimerHotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, TimerHotkeyId);
        }

        _source?.RemoveHook(WndProc);
        foreach (var activity in _visibleActivities)
        {
            activity.SetVisible(false);
        }

        _visibleActivities.Clear();
        base.OnClosed(e);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE when !_focusMode:
                handled = true;
                return MA_NOACTIVATE;

            case WM_HOTKEY when wParam == FocusHotkeyId:
                handled = true;
                if (_userHidden)
                {
                    HotkeyPressedWhileHidden?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    ToggleFocusMode();
                }

                break;

            case WM_HOTKEY when wParam == TimerHotkeyId:
                handled = true;
                TimerHotkeyPressed?.Invoke(this, EventArgs.Empty);
                break;

            case WM_SETTINGCHANGE:
                SettingChangeMessages++;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));
                break;

            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                // Let WPF handle the message first (WM_DPICHANGED resizes the window), then re-centre.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));
                break;
        }

        return 0;
    }

    // ---------------------------------------------------------------- public API

    /// <summary>Shows <paramref name="presentation"/> (fake activities in Phase 1).</summary>
    public void SetPresentation(IslandPresentation presentation)
    {
        _presentation = presentation;
        if (!presentation.CanExpand)
        {
            _hover.Reset();
        }

        UpdateVisual();
    }

    /// <summary>
    /// Microphone / camera in use (BLUEPRINT §8.6.7): small dots just past the trailing edge of the Island, drawn on
    /// whatever it shows (also when idle). Brand colours: microphone Aura.Violet, camera Aura.Cyan.
    /// </summary>
    public void SetPrivacyIndicators(bool microphone, bool camera)
    {
        MicDot.Visibility = microphone ? Visibility.Visible : Visibility.Collapsed;
        CameraDot.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        PrivacyDots.Visibility = microphone || camera ? Visibility.Visible : Visibility.Collapsed;
        ApplyGeometry();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Failure shake (BLUEPRINT §8.4); a brief error tint instead when motion is reduced.</summary>
    public void Shake()
    {
        _errorFlashUntil = _clock.Elapsed + ErrorFlashDuration;
        if (!IsReducedMotion)
        {
            _shakeStartedAt = _clock.Elapsed;
        }

        UpdateBorder();
        EnsureRendering();
    }

    /// <summary>Pins the expanded view open, or unpins it (same as clicking the pill).</summary>
    public void TogglePin()
    {
        var change = _hover.IsPinned ? _hover.Unpin(_clock.Elapsed) : _hover.Pin(_presentation.CanExpand);
        if (change != HoverChange.None)
        {
            UpdateVisual();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-reads options (debug panel / settings).</summary>
    public void ApplyOptions()
    {
        _hover.EnterDelay = _options.HoverDelay;
        _hover.LeaveDelay = _options.LeaveDelay;
        _morph.ReducedMotion = IsReducedMotion;
        Reposition();
        EvaluateFullscreen();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Windows that belong to the Island (debug panel), ignored by the full-screen check.</summary>
    public void RegisterOwnedWindow(Window window)
    {
        _ownedWindows.Add(new WindowInteropHelper(window).EnsureHandle());
    }

    /// <summary>Moves the window to the top-centre of the target monitor's work area.</summary>
    public void Reposition()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var monitor = _options.Monitor == MonitorMode.FollowCursor ? MonitorUnderCursor() : Win32Shell.PrimaryMonitor();
        CurrentMonitor = monitor;
        var target = PlacementCalculator.Compute(monitor, IslandLayout.WindowWidth, IslandLayout.WindowHeight);
        if (Win32Shell.WindowRect(_hwnd) != target)
        {
            Win32Shell.MoveWindow(_hwnd, target);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-checks whether a full-screen app should hide the Island.</summary>
    public void EvaluateFullscreen()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var foreground = Win32Shell.DescribeForeground(GetForegroundWindow(), IsIslandWindow);
        var hide = FullscreenPolicy.ShouldHide(
            Win32Shell.QueryNotificationState(), foreground, CurrentMonitor, _options.ShowOverFullscreen);
        _fullscreenHidden = hide;
        ApplyHidden();
    }

    /// <summary>Hides or shows the Island at the user's request ("Hide for 1 hour").</summary>
    public void SetUserHidden(bool hidden)
    {
        _userHidden = hidden;
        ApplyHidden();
    }

    public void ToggleFocusMode()
    {
        if (_focusMode)
        {
            ExitFocusMode(restoreFocus: true);
        }
        else
        {
            EnterFocusMode();
        }
    }

    // ---- self-test hooks (Diagnostics/SelfTest) ----

    internal void SuspendPolling(bool suspend) => _pollingSuspended = suspend;

    internal void ForceClickThrough(bool clickThrough) => SetClickThrough(clickThrough);

    internal long ExtendedStyle => Win32Shell.GetExtendedStyle(_hwnd);

    // ---------------------------------------------------------------- polling, hover, click-through

    private void Poll()
    {
        if (_hwnd == 0 || _pollingSuspended)
        {
            return;
        }

        var now = _clock.Elapsed;
        var (x, y) = Win32Shell.CursorPosition();

        if (_options.Monitor == MonitorMode.FollowCursor)
        {
            FollowCursorMonitor(x, y, now);
        }

        if (_hidden)
        {
            SetClickThrough(true);
            UpdateHover(false, now);
            return;
        }

        var window = Win32Shell.WindowRect(_hwnd);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var point = PlacementCalculator.ScreenToWindowDip(window, scale, x, y);

        // Adaptive polling: 30 Hz near the Island (hover must feel instant), 10 Hz when the cursor is far away.
        var margin = (int)(NearCursorMargin * scale);
        var near = _focusMode || _hover.IsExpanded
            || (x >= window.Left - margin && x < window.Right + margin && y >= window.Top - margin && y < window.Bottom + margin);
        var interval = near ? PollInterval : FarPollInterval;
        if (_pollTimer.Interval != interval)
        {
            _pollTimer.Interval = interval;
        }

        // The idle pill has no interaction, so it never blocks clicks to the window underneath.
        var over = _presentation.State != IslandBaseState.Idle
            && IslandLayout.IsOverInteractiveArea(_morph.Current, point);

        SetClickThrough(!(over || _focusMode));
        UpdateHover(over, now);
    }

    private void UpdateHover(bool over, TimeSpan now)
    {
        var wasInside = _hover.IsInside;
        var change = _hover.Update(over, now, _presentation.CanExpand);
        if (wasInside != _hover.IsInside)
        {
            PointerOverChanged?.Invoke(this, _hover.IsInside);
        }

        if (change != HoverChange.None)
        {
            UpdateVisual();
        }
    }

    private void SetClickThrough(bool clickThrough)
    {
        if (_clickThrough == clickThrough || _hwnd == 0)
        {
            return;
        }

        _clickThrough = clickThrough;
        Win32Shell.UpdateExtendedStyle(
            _hwnd,
            clickThrough ? WS_EX_TRANSPARENT : 0,
            clickThrough ? 0 : WS_EX_TRANSPARENT);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        WheelScrolled?.Invoke(this, e.Delta);
    }

    private void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            MiddleClicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var (x, y) = Win32Shell.CursorPosition();
        ContextMenuRequested?.Invoke(this, (x, y));
    }

    private void OnPillClicked(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // BLUEPRINT §8.5: click → open the app, or pin it if it's already expanded.
        if (!_hover.IsExpanded && PillClicked is not null)
        {
            PillClicked.Invoke(this, EventArgs.Empty);
        }
        else
        {
            TogglePin();
        }
    }

    // ---------------------------------------------------------------- visual state

    private void UpdateVisual()
    {
        var p = _presentation;
        var expanded = _hover.IsExpanded && p.CanExpand;

        var state = p.State switch
        {
            IslandBaseState.Compact => expanded ? IslandVisualState.Expanded : IslandVisualState.Compact,
            IslandBaseState.Split => expanded ? IslandVisualState.Expanded : IslandVisualState.Split,
            IslandBaseState.Banner => IslandVisualState.Banner,
            _ => IslandVisualState.Idle,
        };

        (IIslandActivityView? Activity, PresentationMode Mode) key = state switch
        {
            IslandVisualState.Idle => (null, PresentationMode.Compact),
            IslandVisualState.Expanded => (p.Primary, PresentationMode.Expanded),
            IslandVisualState.Banner => (p.Primary, PresentationMode.Banner),
            _ => (p.Primary, PresentationMode.Compact),
        };

        var bubble = state == IslandVisualState.Split ? p.Secondary : null;
        var target = IslandLayout.For(state, p.Primary?.ExpandedKind ?? ExpandedKind.Media, p.Primary?.ExpandedHeight ?? 140);

        var contentChanged = key != _pillContentKey;
        if (contentChanged)
        {
            SwapContent(key, target);
            _pillContentKey = key;
        }

        if (!ReferenceEquals(bubble, _bubbleActivity))
        {
            _bubbleActivity = bubble;
            SetBubbleContent(bubble);
        }

        UpdateVisibleActivities(key.Activity, bubble);

        _morph.ReducedMotion = IsReducedMotion;
        if (contentChanged || target != _morph.Target)
        {
            _morph.MorphTo(target, contentChanged);
        }

        VisualState = state;
        Announce();
        EnsureRendering();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SwapContent((IIslandActivityView? Activity, PresentationMode Mode) key, PillGeometry target)
    {
        var focusWasInside = _incoming.IsKeyboardFocusWithin;

        (_incoming, _outgoing) = (_outgoing, _incoming);
        _incoming.Content = key.Activity;
        _incoming.ContentTemplate = key.Activity is null
            ? null
            : TryFindResource($"{key.Mode}.{key.Activity.GetType().Name}") as DataTemplate;
        _incoming.Width = target.Width;
        _incoming.Height = target.Height;
        _incoming.IsHitTestVisible = true;
        _outgoing.IsHitTestVisible = false;
        Panel.SetZIndex(_incoming, 1);
        Panel.SetZIndex(_outgoing, 0);

        // Build and lay out the new template now, before the animation clock starts, so template
        // instantiation does not land inside the first animation frame.
        _incoming.UpdateLayout();

        if (focusWasInside && _focusMode)
        {
            Pill.Focus();
        }
    }

    private void SetBubbleContent(IIslandActivityView? activity)
    {
        BubbleContent.Content = activity;
        BubbleContent.ContentTemplate = activity is null
            ? null
            : TryFindResource($"{PresentationMode.Bubble}.{activity.GetType().Name}") as DataTemplate;
        AutomationProperties.SetName(Bubble, activity?.AccessibleDescription ?? "Second activity");
    }

    private void UpdateVisibleActivities(IIslandActivityView? primary, IIslandActivityView? bubble)
    {
        var now = new HashSet<IIslandActivityView>();
        if (primary is not null)
        {
            now.Add(primary);
        }

        if (bubble is not null)
        {
            now.Add(bubble);
        }

        foreach (var gone in _visibleActivities.Except(now).ToList())
        {
            gone.SetVisible(false);
            _visibleActivities.Remove(gone);
        }

        foreach (var added in now.Except(_visibleActivities).ToList())
        {
            added.SetVisible(true);
            _visibleActivities.Add(added);
        }
    }

    private void Announce()
    {
        var p = _presentation;
        var text = p.State == IslandBaseState.Idle || p.Primary is null
            ? "IrisAura Island"
            : p.Primary.AccessibleDescription + (VisualState == IslandVisualState.Split && p.Secondary is not null
                ? "; also " + p.Secondary.AccessibleDescription
                : string.Empty);

        if (text == _announcement)
        {
            return;
        }

        _announcement = text;
        AutomationProperties.SetName(Pill, text);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            (UIElementAutomationPeer.FromElement(Pill) ?? UIElementAutomationPeer.CreatePeerForElement(Pill))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    // ---------------------------------------------------------------- rendering

    private void EnsureRendering()
    {
        if (_renderingHooked)
        {
            return;
        }

        _renderingHooked = true;
        _lastRenderingTime = TimeSpan.Zero;
        _frames.Reset();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var renderingTime = e is RenderingEventArgs r ? r.RenderingTime : _clock.Elapsed;
        if (_lastRenderingTime == TimeSpan.Zero)
        {
            _lastRenderingTime = renderingTime;
            return;
        }

        var dt = (renderingTime - _lastRenderingTime).TotalSeconds;
        if (dt <= 0)
        {
            return; // Rendering can fire more than once per frame.
        }

        _lastRenderingTime = renderingTime;
        _frames.Add(dt * 1000);

        var animating = _morph.Tick(dt);
        var shaking = UpdateShake();
        var flashing = UpdateErrorFlash();
        ApplyGeometry();

        if (!animating && !shaking && !flashing)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
            _outgoing.Content = null;
            _outgoing.ContentTemplate = null;
            LastAnimation = _frames.Summarize();
            AnimationCompleted?.Invoke(this, LastAnimation);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool UpdateShake()
    {
        if (_shakeStartedAt is not { } start)
        {
            _shakeOffset = 0;
            return false;
        }

        var elapsed = _clock.Elapsed - start;
        if (elapsed >= ShakeAnimation.Duration)
        {
            _shakeStartedAt = null;
            _shakeOffset = 0;
            return false;
        }

        _shakeOffset = ShakeAnimation.OffsetAt(elapsed);
        return true;
    }

    private bool UpdateErrorFlash()
    {
        if (_errorFlashUntil is not { } until)
        {
            return false;
        }

        if (_clock.Elapsed < until)
        {
            return true;
        }

        _errorFlashUntil = null;
        UpdateBorder();
        return false;
    }

    private void ApplyGeometry()
    {
        var g = _morph.Current;
        var pill = IslandLayout.PillRect(g);
        var radius = Math.Min(g.CornerRadius, Math.Min(pill.Width, pill.Height) / 2);
        var left = pill.Left + _shakeOffset;

        Canvas.SetLeft(PillShadow, left);
        Canvas.SetTop(PillShadow, pill.Top);
        PillShadow.Width = pill.Width;
        PillShadow.Height = pill.Height;
        PillShadow.CornerRadius = radius;

        Canvas.SetLeft(Pill, left);
        Canvas.SetTop(Pill, pill.Top);
        Pill.Width = pill.Width;
        Pill.Height = pill.Height;
        Pill.CornerRadius = new CornerRadius(radius);

        Canvas.SetLeft(PillContent, left);
        Canvas.SetTop(PillContent, pill.Top);
        PillContent.Width = pill.Width;
        PillContent.Height = pill.Height;
        PillClip.Rect = new Rect(0, 0, pill.Width, pill.Height);
        PillClip.RadiusX = radius;
        PillClip.RadiusY = radius;

        _incoming.Opacity = _morph.IncomingOpacity;
        _outgoing.Opacity = _morph.OutgoingOpacity;
        var (scale, shift) = ReferenceEquals(_incoming, ContentA)
            ? (_incomingScaleA, _incomingShiftA)
            : (_incomingScaleB, _incomingShiftB);
        scale.ScaleX = scale.ScaleY = _morph.IncomingScale;
        shift.Y = _morph.IncomingOffsetY;
        var (outScale, outShift) = ReferenceEquals(_outgoing, ContentA)
            ? (_incomingScaleA, _incomingShiftA)
            : (_incomingScaleB, _incomingShiftB);
        outScale.ScaleX = outScale.ScaleY = 1;
        outShift.Y = 0;

        if (IslandLayout.BubbleRect(g) is { } bubble)
        {
            Bubble.Visibility = Visibility.Visible;
            BubbleShadow.Visibility = Visibility.Visible;
            Canvas.SetLeft(Bubble, bubble.Left + _shakeOffset);
            Canvas.SetTop(Bubble, bubble.Top);
            Bubble.Width = bubble.Width;
            Bubble.Height = bubble.Height;
            Bubble.CornerRadius = new CornerRadius(bubble.Width / 2);
            Canvas.SetLeft(BubbleShadow, bubble.Left + _shakeOffset);
            Canvas.SetTop(BubbleShadow, bubble.Top);
            BubbleShadow.Width = bubble.Width;
            BubbleShadow.Height = bubble.Height;
            BubbleShadow.CornerRadius = bubble.Width / 2;
            BubbleContent.Opacity = Math.Clamp(bubble.Width / IslandLayout.BubbleSize, 0, 1);
        }
        else
        {
            Bubble.Visibility = Visibility.Collapsed;
            BubbleShadow.Visibility = Visibility.Collapsed;
        }

        if (PrivacyDots.Visibility == Visibility.Visible)
        {
            // Past the trailing edge (pill or bubble), centred on the first row so they never cover activity content.
            var trailing = (IslandLayout.BubbleRect(g) is { } b ? b.Right : pill.Right) + _shakeOffset;
            var row = Math.Min(pill.Height, IslandLayout.Compact.Height);
            Canvas.SetLeft(PrivacyDots, trailing + PrivacyDotGap);
            Canvas.SetTop(PrivacyDots, pill.Top + ((row - PrivacyDots.Height) / 2));
        }
    }

    private void UpdateBorder()
    {
        Pill.BorderBrush = _errorFlashUntil is not null
            ? (Brush)FindResource("Aura.Error")
            : _focusMode && IsKeyboardFocusWithin
                ? (Brush)FindResource("Aura.Cyan")
                : Brushes.Transparent;
    }

    // ---------------------------------------------------------------- keyboard focus mode

    private void EnterFocusMode()
    {
        if (_hidden || _hwnd == 0)
        {
            return;
        }

        _focusReturnWindow = GetForegroundWindow();
        _focusMode = true;
        Win32Shell.UpdateExtendedStyle(_hwnd, 0, WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        _clickThrough = false;

        // Receiving WM_HOTKEY counts as user input, which permits taking the foreground (BLUEPRINT §8.3 ⚠ VERIFY).
        SetForegroundWindow(_hwnd);
        Activate();
        Pill.Focus();
        Keyboard.Focus(Pill);

        if (_hover.Pin(_presentation.CanExpand) != HoverChange.None)
        {
            UpdateVisual();
        }

        UpdateBorder();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ExitFocusMode(bool restoreFocus)
    {
        if (!_focusMode)
        {
            return;
        }

        _focusMode = false;
        Win32Shell.UpdateExtendedStyle(_hwnd, WS_EX_NOACTIVATE, 0);
        if (_hover.Unpin(_clock.Elapsed) != HoverChange.None)
        {
            UpdateVisual();
        }

        if (restoreFocus && _focusReturnWindow != 0 && IsWindow(_focusReturnWindow))
        {
            SetForegroundWindow(_focusReturnWindow);
        }

        _focusReturnWindow = 0;
        UpdateBorder();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_focusMode)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            ExitFocusMode(restoreFocus: true);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Space && Pill.IsKeyboardFocused)
        {
            TogglePin();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- full screen, topmost, monitors

    private bool IsIslandWindow(nint hwnd) => hwnd == _hwnd || _ownedWindows.Contains(hwnd);

    private void ApplyHidden()
    {
        var hide = _fullscreenHidden || _userHidden;
        if (hide == _hidden)
        {
            return;
        }

        _hidden = hide;
        if (hide)
        {
            ExitFocusMode(restoreFocus: false);
            if (_hover.Reset() != HoverChange.None)
            {
                UpdateVisual();
            }

            SetClickThrough(true);
        }
        else
        {
            Win32Shell.ReassertTopmost(_hwnd);
        }

        Stage.IsHitTestVisible = !hide;
        var to = hide ? 0.0 : 1.0;
        if (IsReducedMotion)
        {
            Stage.BeginAnimation(OpacityProperty, null);
            Stage.Opacity = to;
        }
        else
        {
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(to, new Duration(FadeDuration)));
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnForegroundChanged(nint hwnd)
    {
        if (_hwnd == 0)
        {
            return;
        }

        // Some apps take topmost when they come forward; put the Island back on top (BLUEPRINT §8.3).
        if (!_hidden && hwnd != _hwnd)
        {
            Win32Shell.ReassertTopmost(_hwnd);
        }

        EvaluateFullscreen();
    }

    private MonitorDescriptor MonitorUnderCursor()
    {
        var (x, y) = Win32Shell.CursorPosition();
        return x == int.MinValue ? Win32Shell.PrimaryMonitor() : Win32Shell.MonitorAt(x, y);
    }

    private void FollowCursorMonitor(int x, int y, TimeSpan now)
    {
        if (x == int.MinValue)
        {
            return;
        }

        var monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
        if (monitor == CurrentMonitor.Handle)
        {
            _pendingMonitor = 0;
            return;
        }

        if (monitor != _pendingMonitor)
        {
            _pendingMonitor = monitor;
            _pendingMonitorSince = now;
            return;
        }

        if (now - _pendingMonitorSince >= MonitorFollowDelay)
        {
            _pendingMonitor = 0;
            Reposition();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            _morph.ReducedMotion = IsReducedMotion;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Reposition));
        }
    }
}
