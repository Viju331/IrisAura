using System.Windows.Threading;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;
using Serilog;

namespace IrisAura.Island.Engine;

/// <summary>
/// Connects the activity engine to the Island window (BLUEPRINT §8.5): arranges activities with the
/// <see cref="PriorityArbiter"/>, turns the layout into an <see cref="IslandPresentation"/>, re-arranges
/// when time alone changes the layout (expiry, paused-media grace), and handles gestures
/// (bubble swap, wheel cycle, middle click, right-click / tray menu, quiet mode, hide for 1 hour).
/// </summary>
public sealed class IslandController : IDisposable
{
    /// <summary>Action id sent to a provider on middle click (media: play/pause).</summary>
    public const string PrimaryAction = "primary";

    public static readonly TimeSpan HideDuration = TimeSpan.FromHours(1);

    private const int MenuQuiet = 1;
    private const int MenuHide = 2;
    private const int MenuShow = 3;
    private const int MenuSettings = 4;
    private const int MenuDebug = 5;
    private const int MenuQuit = 6;
    private const int MenuTimerBase = 100;
    private static readonly int[] TimerMinutes = [1, 5, 10, 25];

    private readonly IslandWindow _window;
    private readonly ActivityManager _manager;
    private readonly ActivityProviderHost _host;
    private readonly IslandStateStore _store;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, GenericActivityView> _genericViews = new(StringComparer.Ordinal);
    private TrayIcon? _tray;
    private IslandState _state;
    private UserPreference? _preference;

    public IslandController(IslandWindow window, ActivityManager manager, ActivityProviderHost host, IslandStateStore store)
    {
        _window = window;
        _manager = manager;
        _host = host;
        _store = store;
        _time = manager.Time;
        _state = store.Load();

        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Refresh();

        _manager.Changed += OnActivityChanged;
        _window.BubbleClicked += (_, _) => Swap();
        _window.WheelScrolled += (_, delta) => Cycle(delta > 0 ? -1 : 1);
        _window.MiddleClicked += (_, _) => InvokePrimaryAction();
        _window.ContextMenuRequested += (_, point) => ShowMenu(_window.Handle, point.X, point.Y, openUpwards: false);
        _window.HotkeyPressedWhileHidden += (_, _) => ShowAgain();
    }

    /// <summary>The user chose Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The user chose the debug panel (debug builds / --debug).</summary>
    public event EventHandler? DebugPanelRequested;

    /// <summary>The user chose Start timer (handled by the timer provider; the debug timer until Phase 4).</summary>
    public event EventHandler<TimeSpan>? StartTimerRequested;

    /// <summary>The layout changed (debug panel).</summary>
    public event EventHandler<ActivityLayout>? LayoutChanged;

    public ActivityLayout Layout { get; private set; } = ActivityLayout.Empty;

    public UserPreference? Preference => _preference;

    public bool QuietMode => _state.QuietMode;

    public bool ShellQuietTime { get; private set; }

    public bool IsHiddenByUser => _state.HiddenUntil is { } until && until > _time.GetUtcNow();

    /// <summary>Show the "Debug panel…" menu item.</summary>
    public bool DebugMenuEnabled { get; set; }

    public bool TrayIconVisible => _tray?.IsAdded == true;

    /// <summary>Applies persisted state and shows the current layout. Call once the window exists.</summary>
    public void Start()
    {
        _window.SetUserHidden(IsHiddenByUser);
        Refresh();
    }

    /// <summary>Adds the notification-area icon. Called after the first frame so it does not delay startup.</summary>
    public void ShowTrayIcon()
    {
        if (_tray is not null)
        {
            return;
        }

        _tray = new TrayIcon();
        _tray.MenuRequested += (_, point) => ShowMenu(_tray.OwnerHandle, point.X, point.Y, openUpwards: true);
    }

    public void Dispose()
    {
        _timer.Stop();
        _manager.Changed -= OnActivityChanged;
        _tray?.Dispose();
    }

    // ---------------------------------------------------------------- arranging

    /// <summary>Expires due activities, re-arranges and updates the window.</summary>
    public void Refresh()
    {
        _timer.Stop();
        _manager.ExpireDue();

        var now = _time.GetUtcNow();
        if (_state.HiddenUntil is { } until && until <= now)
        {
            ShowAgain();
        }

        ShellQuietTime = Win32Shell.QueryNotificationState() == UserNotificationState.QuietTime;
        Layout = PriorityArbiter.Arrange(new ArbiterInput(_manager.Activities, now, QuietMode || ShellQuietTime, _preference));
        _window.SetPresentation(ToPresentation(Layout));
        LayoutChanged?.Invoke(this, Layout);

        var next = Min(Layout.NextChangeAt, _state.HiddenUntil);
        if (next is { } at)
        {
            var delay = at - now;
            _timer.Interval = delay > TimeSpan.Zero ? delay + TimeSpan.FromMilliseconds(15) : TimeSpan.FromMilliseconds(15);
            _timer.Start();
        }
    }

    private IslandPresentation ToPresentation(ActivityLayout layout)
    {
        if (layout.Transient is { } transient)
        {
            return new IslandPresentation(IslandBaseState.Banner, ViewFor(transient));
        }

        if (layout.Main is null)
        {
            return IslandPresentation.Idle;
        }

        return layout.Bubble is { } bubble
            ? new IslandPresentation(IslandBaseState.Split, ViewFor(layout.Main), ViewFor(bubble))
            : new IslandPresentation(IslandBaseState.Compact, ViewFor(layout.Main));
    }

    private IIslandActivityView ViewFor(IslandActivity activity)
    {
        if (activity.View is IIslandActivityView custom)
        {
            return custom;
        }

        if (_genericViews.TryGetValue(activity.Id, out var view))
        {
            view.Apply(activity);
            return view;
        }

        view = new GenericActivityView(activity, InvokeAction);
        _genericViews[activity.Id] = view;
        return view;
    }

    private void OnActivityChanged(object? sender, ActivityChangedEventArgs e)
    {
        var a = e.Activity;
        if (e.Change == ActivityChange.Ended)
        {
            _genericViews.Remove(a.Id);
            if (_preference is { } p && (p.PreferredMainId == a.Id || p.PartnerId == a.Id))
            {
                _preference = null; // remembered only until either activity ends
            }
        }
        else if (e.Change == ActivityChange.Started && _preference is { } p && !a.IsTransient && a.Priority > p.Priority)
        {
            _preference = null; // something more important started (e.g. a call)
        }

        Refresh();
    }

    // ---------------------------------------------------------------- gestures

    /// <summary>Bubble click: swap pill and bubble (BLUEPRINT §8.5 rule 4).</summary>
    public void Swap()
    {
        if (Layout is { Main: { } main, Bubble: { } bubble })
        {
            _preference = new UserPreference(bubble.Id, main.Id, bubble.Priority);
            Refresh();
        }
    }

    /// <summary>Mouse wheel: show the next (+1) or previous (-1) persistent activity in the pill.</summary>
    public void Cycle(int direction)
    {
        var ordered = Layout.PersistentInOrder.ToList();
        if (ordered.Count < 2)
        {
            return;
        }

        var next = ordered[(direction >= 0 ? 1 : ordered.Count - 1) % ordered.Count];
        _preference = new UserPreference(next.Id, null, next.Priority);
        Refresh();
    }

    public void InvokePrimaryAction()
    {
        if (Layout.Main is { } main)
        {
            _host.DispatchAction(main, PrimaryAction);
        }
    }

    public void InvokeAction(string activityId, string actionId)
    {
        if (_manager.Find(activityId) is { } activity)
        {
            _host.DispatchAction(activity, actionId);
        }
    }

    public void SetQuietMode(bool quiet)
    {
        _state = _state with { QuietMode = quiet };
        _store.Save(_state);
        Log.Information("Island quiet mode {Quiet}", quiet);
        Refresh();
    }

    public void HideForAnHour()
    {
        _state = _state with { HiddenUntil = _time.GetUtcNow() + HideDuration };
        _store.Save(_state);
        _window.SetUserHidden(true);
        Refresh();
    }

    public void ShowAgain()
    {
        if (_state.HiddenUntil is null)
        {
            return;
        }

        _state = _state with { HiddenUntil = null };
        _store.Save(_state);
        _window.SetUserHidden(false);
    }

    private void ShowMenu(nint owner, int x, int y, bool openUpwards)
    {
        var hidden = IsHiddenByUser;
        var items = new List<MenuItemSpec>
        {
            new(MenuQuiet, "Quiet mode (hide notification banners)", Checked: QuietMode),
            hidden ? new MenuItemSpec(MenuShow, "Show IrisAura Island") : new MenuItemSpec(MenuHide, "Hide for 1 hour"),
            new(0, "Start timer", Children: TimerMinutes.Select(m => new MenuItemSpec(MenuTimerBase + m, $"{m} min")).ToList()),
            MenuItemSpec.Separator,
            new(MenuSettings, "Settings (coming in a later update)", Enabled: false),
        };
        if (DebugMenuEnabled)
        {
            items.Add(new MenuItemSpec(MenuDebug, "Debug panel…"));
        }

        items.Add(MenuItemSpec.Separator);
        items.Add(new MenuItemSpec(MenuQuit, "Quit IrisAura Island"));

        var (command, previous) = NativeMenu.Show(owner, x, y, items, openUpwards);
        if (command != MenuDebug)
        {
            NativeMenu.RestoreForeground(previous);
        }

        switch (command)
        {
            case MenuQuiet:
                SetQuietMode(!QuietMode);
                break;
            case MenuHide:
                HideForAnHour();
                break;
            case MenuShow:
                ShowAgain();
                break;
            case MenuDebug:
                DebugPanelRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuQuit:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case > MenuTimerBase:
                StartTimerRequested?.Invoke(this, TimeSpan.FromMinutes(command - MenuTimerBase));
                break;
        }
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
