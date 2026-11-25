using System.Windows.Threading;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;
using IrisAura.Island.Views.Panel;
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
    public static readonly TimeSpan HideDuration = TimeSpan.FromHours(1);

    private const int MenuQuiet = 1;
    private const int MenuHide = 2;
    private const int MenuShow = 3;
    private const int MenuSettings = 4;
    private const int MenuDebug = 5;
    private const int MenuQuit = 6;
    private const int MenuPanel = 7;
    private const int MenuTimerBase = 100;
    private const int MenuProviderBase = 200;
    private const int MenuSdkAppBase = 300;
    private static readonly int[] TimerMinutes = [1, 5, 10, 25];

    private readonly IslandWindow _window;
    private readonly ActivityManager _manager;
    private readonly ActivityProviderHost _host;
    private readonly IslandStateStore _store;
    private readonly IslandSettingsStore? _settingsStore;
    private readonly TimeProvider _time;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, IActivityViewModel> _views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Type?> _viewPayloads = new(StringComparer.Ordinal);
    private readonly Func<IslandActivity, IActivityViewModel?>? _viewFactory;
    private TrayIcon? _tray;
    private IslandState _state;
    private IslandSettings _settings;
    private UserPreference? _preference;
    private ForegroundApp? _foreground;
    private DashboardView? _dashboard;

    /// <param name="window">The Island window.</param>
    /// <param name="manager">Activity engine.</param>
    /// <param name="host">Provider host (actions are routed through it).</param>
    /// <param name="store">Persisted state.</param>
    /// <param name="viewFactory">Creates rich view models for data payloads (e.g. media); <see langword="null"/> result = generic view.</param>
    /// <param name="settingsStore">Provider on/off settings (island-settings.json); <see langword="null"/> = not persisted.</param>
    public IslandController(
        IslandWindow window,
        ActivityManager manager,
        ActivityProviderHost host,
        IslandStateStore store,
        Func<IslandActivity, IActivityViewModel?>? viewFactory = null,
        IslandSettingsStore? settingsStore = null)
    {
        _window = window;
        _manager = manager;
        _host = host;
        _store = store;
        _settingsStore = settingsStore;
        _viewFactory = viewFactory;
        _time = manager.Time;
        _state = store.Load();
        _settings = settingsStore?.Load() ?? new IslandSettings();

        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Refresh();

        _manager.Changed += OnActivityChanged;
        _window.BubbleClicked += (_, _) => Swap();
        _window.PillClicked += (_, _) => OnPillClicked();
        _window.WheelScrolled += (_, delta) => Cycle(delta > 0 ? -1 : 1);
        _window.MiddleClicked += (_, _) => InvokePrimaryAction();
        _window.ContextMenuRequested += (_, point) => ShowMenu(_window.Handle, point.X, point.Y, openUpwards: false);
        _window.HotkeyPressedWhileHidden += (_, _) => ShowAgain();
        Interop.NativeMethods.ForegroundChanged += OnForegroundChanged;
    }

    /// <summary>The user chose Quit.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>The user chose the debug panel (debug builds / --debug).</summary>
    public event EventHandler? DebugPanelRequested;

    /// <summary>The user chose Start timer (handled by the timer provider).</summary>
    public event EventHandler<TimeSpan>? StartTimerRequested;

    /// <summary>The layout changed (debug panel).</summary>
    public event EventHandler<ActivityLayout>? LayoutChanged;

    public ActivityLayout Layout { get; private set; } = ActivityLayout.Empty;

    public UserPreference? Preference => _preference;

    public bool QuietMode => _state.QuietMode;

    public bool ShellQuietTime { get; private set; }

    public bool IsHiddenByUser => _state.HiddenUntil is { } until && until > _time.GetUtcNow();

    /// <summary>
    /// Hide an activity while its own app is in front (owner request 2026-11-25; default on). It appears again
    /// when the app is minimised or another app comes to the front.
    /// </summary>
    public bool HideWhenSourceAppInFront { get; set; } = true;

    /// <summary>The app currently in front (diagnostics).</summary>
    public ForegroundApp? ForegroundApp => _foreground;

    /// <summary>Show the "Debug panel…" menu item.</summary>
    public bool DebugMenuEnabled { get; set; }

    /// <summary>Provider that handles "Start timer"; the item is disabled while it is not running.</summary>
    public string TimerProviderId { get; set; } = "timers";

    /// <summary>Apps that used the SDK (External apps menu); <see langword="null"/> hides the menu.</summary>
    public Func<IReadOnlyList<Providers.Sdk.SdkAppInfo>>? SdkApps { get; set; }

    /// <summary>Blocks (true) or unblocks an SDK app by key.</summary>
    public Action<string, bool>? SetSdkAppBlocked { get; set; }

    /// <summary>Providers the user does not switch in the Activities menu (the debug provider).</summary>
    public ISet<string> HiddenProviders { get; } = new HashSet<string>(StringComparer.Ordinal);

    public IslandSettings Settings => _settings;

    /// <summary>
    /// The dashboard panel (Phase 4B, §8.13A). Setting it makes hover open the panel, shows the main activity in its
    /// now card, routes dropped files to the tray and persists the camera-mirror switch.
    /// </summary>
    public DashboardView? Dashboard
    {
        get => _dashboard;
        set
        {
            if (_dashboard is not null || value is null)
            {
                throw new InvalidOperationException("The dashboard is set once.");
            }

            _dashboard = value;
            _window.Dashboard = value;
            value.Mirror.CameraActiveChanged += (_, _) => Refresh();
            value.Mirror.EnabledChanged += (_, enabled) =>
            {
                _settings = _settings with { CameraMirror = enabled };
                _settingsStore?.Save(_settings);
                Log.Information("Camera mirror switched {State}", enabled ? "on" : "off");
            };
            _window.FileDragEntered += (_, _) => value.SelectedTab = PanelTab.Tray;
            _window.FilesDropped += (_, paths) =>
            {
                value.Shelf.Add(paths);
                value.SelectedTab = PanelTab.Tray;
            };
        }
    }

    public bool TrayIconVisible => _tray?.IsAdded == true;

    /// <summary>Applies persisted state and shows the current layout. Call once the window exists.</summary>
    public void Start()
    {
        _foreground = Win32Shell.DescribeForegroundApp(Interop.NativeMethods.GetForegroundWindow());
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
        Interop.NativeMethods.ForegroundChanged -= OnForegroundChanged;
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
        Layout = PriorityArbiter.Arrange(new ArbiterInput(
            _manager.Activities, now, QuietMode || ShellQuietTime, _preference, HideWhenSourceAppInFront ? _foreground : null));
        var presentation = ToPresentation(Layout);
        if (_dashboard is not null)
        {
            _dashboard.Now = Layout.Main is { } main ? ViewFor(main) : null;
        }

        _window.SetPresentation(presentation);

        // The Island's own camera mirror counts as camera use (§8.13A: the privacy indicator is on while it shows).
        _window.SetPrivacyIndicators(
            Layout.PrivacyIndicators.Any(a => a.Compact.LeadingGlyph == "Aura.Glyph.Mic"),
            Layout.PrivacyIndicators.Any(a => a.Compact.LeadingGlyph == "Aura.Glyph.Camera")
                || _dashboard?.Mirror.IsCameraActive == true);
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

        // A data payload (e.g. media state) gets a rich view from the factory; everything else (including payloads the
        // factory does not know, such as SDK visuals) the generic view. The view is kept and updated in place while the
        // payload type stays the same, so updates never rebuild it (no cross-fade per update).
        var payload = activity.View?.GetType();
        if (_views.TryGetValue(activity.Id, out var view) && _viewPayloads.GetValueOrDefault(activity.Id) == payload)
        {
            view.Apply(activity);
            return view;
        }

        view = (activity.View is not null ? _viewFactory?.Invoke(activity) : null) ?? new GenericActivityView(activity, InvokeAction);
        _views[activity.Id] = view;
        _viewPayloads[activity.Id] = payload;
        return view;
    }

    /// <summary>
    /// Brings the activity's source app to the front (BLUEPRINT §8.6.1); if that is not possible, asks the
    /// owning provider to handle "open".
    /// </summary>
    /// <returns><see langword="true"/> when something handled it.</returns>
    public bool OpenApp(string activityId)
    {
        if (_manager.Find(activityId) is not { } activity)
        {
            return false;
        }

        return AppActivator.TryActivate(activity.SourceAppId) || _host.DispatchAction(activity, ActivityActions.Open);
    }

    /// <summary>Click on the compact pill: open the source app, or pin the Island open when there is no app.</summary>
    private void OnPillClicked()
    {
        var activity = Layout.Transient ?? Layout.Main;
        if (activity is null || !OpenApp(activity.Id))
        {
            _window.TogglePin();
        }
    }

    /// <summary>The app in front changed: its own activity hides; the previous app's activity may reappear.</summary>
    private void OnForegroundChanged(nint hwnd)
    {
        // The Island's own menus, debug panel and focus mode do not count as "another app in front".
        Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId)
        {
            return;
        }

        var app = Win32Shell.DescribeForegroundApp(hwnd);
        if (app == _foreground)
        {
            return;
        }

        _foreground = app;
        if (HideWhenSourceAppInFront)
        {
            Refresh();
        }
    }

    private void OnActivityChanged(object? sender, ActivityChangedEventArgs e)
    {
        var a = e.Activity;
        if (e.Change == ActivityChange.Ended)
        {
            _viewPayloads.Remove(a.Id);
            if (_views.Remove(a.Id, out var removed) && removed is IDisposable disposable)
            {
                disposable.Dispose();
            }

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
            _host.DispatchAction(main, ActivityActions.Primary);
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
        var providers = _host.Providers;
        var sdkApps = SdkApps?.Invoke() ?? [];
        var timersRunning = _host.Status(TimerProviderId)?.State == ProviderState.Running;
        var items = new List<MenuItemSpec>();
        var privacy = PrivacyMenuItems().ToList();
        if (privacy.Count > 0)
        {
            items.AddRange(privacy);
            items.Add(MenuItemSpec.Separator);
        }

        items.AddRange(
        [
            new(MenuQuiet, "Quiet mode (hide notification banners)", Checked: QuietMode),
            new(MenuPanel, "Open panel", Enabled: _dashboard is not null && !hidden),
            hidden ? new MenuItemSpec(MenuShow, "Show IrisAura Island") : new MenuItemSpec(MenuHide, "Hide for 1 hour"),
            new(0, "Start timer", Enabled: timersRunning,
                Children: TimerMinutes.Select(m => new MenuItemSpec(MenuTimerBase + m, $"{m} min")).ToList()),
            new(0, "Activities", Children: ProviderMenuItems(providers)),
            new(0, "External apps", Enabled: sdkApps.Count > 0, Children: sdkApps.Count == 0
                ? [new MenuItemSpec(0, "No apps yet", Enabled: false)]
                : sdkApps.Select((a, i) => new MenuItemSpec(
                    MenuSdkAppBase + i,
                    $"{a.Display}{(a.Connected ? " · connected" : string.Empty)}{(a.Activities > 0 ? $" · {a.Activities} shown" : string.Empty)}",
                    Checked: !a.Blocked)).ToList()),
            MenuItemSpec.Separator,
            new(MenuSettings, "Settings (coming in a later update)", Enabled: false),
        ]);
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
            case MenuPanel:
                _window.OpenPanel(); // stays open until clicked or the cursor has been over it and left
                break;
            case MenuDebug:
                DebugPanelRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuQuit:
                QuitRequested?.Invoke(this, EventArgs.Empty);
                break;
            case >= MenuSdkAppBase when command - MenuSdkAppBase < sdkApps.Count:
                var sdkApp = sdkApps[command - MenuSdkAppBase];
                SetSdkAppBlocked?.Invoke(sdkApp.Key, !sdkApp.Blocked); // ticked = allowed
                break;
            case >= MenuProviderBase when command - MenuProviderBase < providers.Count:
                var provider = providers[command - MenuProviderBase];
                var retry = provider.IsEnabled && provider.State is ProviderState.Faulted or ProviderState.Unavailable;
                SetProviderEnabled(provider.Id, retry || !provider.IsEnabled);
                break;
            case > MenuTimerBase and < MenuProviderBase:
                StartTimerRequested?.Invoke(this, TimeSpan.FromMinutes(command - MenuTimerBase));
                break;
        }
    }

    /// <summary>Changes and saves settings (used by the App for options owned by providers).</summary>
    public void SaveSettings(Func<IslandSettings, IslandSettings> change)
    {
        _settings = change(_settings);
        _settingsStore?.Save(_settings);
    }

    /// <summary>
    /// Turns a provider on or off (Activities menu; BLUEPRINT §10 Phase 4 "each provider can be independently
    /// disabled/restarted") and saves the choice. Turning on a provider that failed or was unavailable retries it.
    /// </summary>
    public void SetProviderEnabled(string providerId, bool enabled)
    {
        _host.SetEnabled(providerId, enabled);
        _settings = _settings.WithProvider(providerId, enabled);
        _settingsStore?.Save(_settings);
        Log.Information("Island provider {Provider} switched {State}", providerId, enabled ? "on" : "off");
    }

    /// <summary>Menu lines listing the apps using the microphone/camera (BLUEPRINT §8.6.7 "lists which apps").</summary>
    private IEnumerable<MenuItemSpec> PrivacyMenuItems() =>
        Layout.PrivacyIndicators
            .Where(a => a.Expanded is not null)
            .Select(a => new MenuItemSpec(0, $"{a.Expanded!.Title}: {a.Expanded.Subtitle}", Enabled: false));

    private List<MenuItemSpec> ProviderMenuItems(IReadOnlyList<ProviderStatus> providers)
    {
        var items = new List<MenuItemSpec>();
        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            if (HiddenProviders.Contains(p.Id))
            {
                continue;
            }

            var label = p.State switch
            {
                ProviderState.Unavailable when p.IsEnabled => $"{p.DisplayName} (not available on this device)",
                ProviderState.Faulted when p.IsEnabled => $"{p.DisplayName} (stopped after an error; click to restart)",
                _ => p.DisplayName,
            };
            items.Add(new MenuItemSpec(MenuProviderBase + i, label, Checked: p.IsEnabled));
        }

        return items;
    }

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;
}
