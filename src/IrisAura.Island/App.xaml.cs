using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using IrisAura.Island.Diagnostics;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Calls;
using IrisAura.Island.Providers.Devices;
using IrisAura.Island.Providers.Downloads;
using IrisAura.Island.Providers.Hud;
using IrisAura.Island.Providers.Media;
using IrisAura.Island.Providers.Power;
using IrisAura.Island.Providers.Privacy;
using IrisAura.Island.Providers.Sdk;
using IrisAura.Island.Providers.Timers;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;
using IrisAura.Island.Views.Panel;
using Serilog;

namespace IrisAura.Island;

/// <summary>
/// IrisAura Island entry point. One instance per user session.
/// <list type="bullet">
/// <item><c>--debug</c>: show the debug panel (always shown in Debug builds).</item>
/// <item><c>--quit</c>: ask the running instance to exit.</item>
/// <item><c>--selftest &lt;report.json&gt;</c> / <c>--perf &lt;report.json&gt;</c>: integration self-test, then exit (0 = all passed).</item>
/// </list>
/// A second launch shows a hidden Island again (and its debug panel when started with <c>--debug</c>).
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\IrisAura.Island.Instance";
    private const string QuitEventName = @"Local\IrisAura.Island.Quit";
    private const string ShowEventName = @"Local\IrisAura.Island.Show";
    private const string ShowDebugEventName = @"Local\IrisAura.Island.ShowDebug";

    private readonly List<RegisteredWaitHandle> _waits = [];
    private readonly List<EventWaitHandle> _events = [];
    private Mutex? _instanceMutex;
    private IslandWindow? _island;
    private IslandOptions? _options;
    private ActivityManager? _manager;
    private ActivityProviderHost? _host;
    private IslandController? _controller;
    private DebugActivityProvider? _debugProvider;
    private DebugPanelWindow? _debugPanel;
    private CoreAudioLevelMeter? _levels;
    private CapabilityUsageMonitor? _capabilities;
    private TimerProvider? _timers;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var quit = args.Contains("--quit", StringComparer.OrdinalIgnoreCase);
        var debug = args.Contains("--debug", StringComparer.OrdinalIgnoreCase);
        var perfOnly = args.Contains("--perf", StringComparer.OrdinalIgnoreCase);
        var selfTestIndex = Array.FindIndex(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)
            || a.Equals("--perf", StringComparison.OrdinalIgnoreCase));
        var selfTestPath = selfTestIndex >= 0
            ? (selfTestIndex + 1 < args.Length ? args[selfTestIndex + 1] : "island-selftest.json")
            : null;

        var probeIndex = Array.FindIndex(args, a => a.Equals("--media-probe", StringComparison.OrdinalIgnoreCase));
        if (probeIndex >= 0)
        {
            // Diagnostic only: no window, no single-instance lock, no persisted state.
            var probePath = probeIndex + 1 < args.Length ? args[probeIndex + 1] : "media-probe.json";
            var artIndex = Array.FindIndex(args, a => a.Equals("--save-artwork", StringComparison.OrdinalIgnoreCase));
            var artDir = artIndex >= 0 && artIndex + 1 < args.Length ? args[artIndex + 1] : null;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var code = 1;
                try
                {
                    code = await MediaProbe.RunAsync(probePath, artDir);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(probePath, $"{{ \"error\": \"{ex.GetType().Name}\" }}");
                }

                Shutdown(code);
            }));
            return;
        }

        var systemProbeIndex = Array.FindIndex(args, a => a.Equals("--system-probe", StringComparison.OrdinalIgnoreCase));
        if (systemProbeIndex >= 0)
        {
            // Diagnostic only (Phase 4 evidence): no window, no single-instance lock, no persisted state.
            var probePath = systemProbeIndex + 1 < args.Length ? args[systemProbeIndex + 1] : "system-probe.json";
            bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                var code = 1;
                try
                {
                    code = await SystemProbe.RunAsync(probePath, Has("--nudge-volume"), Has("--nudge-brightness"), Has("--probe-mic"));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(probePath, $"{{ \"error\": \"{ex.GetType().Name}\" }}");
                }

                Shutdown(code);
            }));
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance owns the session: forward the request and exit.
            Signal(quit ? QuitEventName : debug ? ShowDebugEventName : ShowEventName);
            if (selfTestPath is not null)
            {
                File.WriteAllText(selfTestPath, "{ \"error\": \"IrisAura Island is already running; run 'IrisAura.Island.exe --quit' first.\" }");
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown(selfTestPath is null ? 0 : 2);
            return;
        }

        if (quit)
        {
            Shutdown(0);
            return;
        }

#if DEBUG
        debug = true;
#endif
        ConfigureLogging();
        DispatcherUnhandledException += (_, ex) =>
            Log.Error("Unhandled UI exception: {Type}: {Message}", ex.Exception.GetType().Name, ex.Exception.Message);

        _options = new IslandOptions();
        _island = new IslandWindow(_options);
        _manager = new ActivityManager(TimeProvider.System);
        _host = new ActivityProviderHost(_manager, SynchronizationContext.Current);
        _levels = new CoreAudioLevelMeter();
        var visuals = new AppVisualsResolver();

        // The self-test must not change the user's real persisted state, settings or timer.
        string StatePath(string defaultPath, string name) => selfTestPath is null
            ? defaultPath
            : Path.Combine(Path.GetTempPath(), $"irisaura-selftest-{name}-{Environment.ProcessId}.json");
        var statePath = StatePath(IslandStateStore.DefaultPath, "state");
        var settingsStore = new IslandSettingsStore(StatePath(IslandSettingsStore.DefaultPath, "settings"));
        var settings = settingsStore.Load();

        // Providers (BLUEPRINT §8.6). Each can be switched off in the Activities menu (island-settings.json).
        _debugProvider = new DebugActivityProvider();
        _capabilities = new CapabilityUsageMonitor();
        _timers = new TimerProvider(new TimerStore(StatePath(TimerStore.DefaultPath, "timer")));
        void Add(IActivityProvider provider, bool enabledByDefault = true) =>
            _host.Register(provider, settings.IsEnabled(provider.Id, enabledByDefault));
        Add(_debugProvider);
        Add(new MediaProvider(() => new GsmtcMediaSessionSource()));
        Add(new VolumeProvider());
        Add(new BrightnessProvider());
        Add(new PowerProvider());
        Add(new BluetoothProvider());
        Add(new PrivacyProvider(_capabilities));
        Add(_timers);
        Add(new DownloadsProvider());
        Add(new CallProvider(_capabilities, new AudioSessionActivity()), enabledByDefault: false); // beta (§8.6.8)

        // Activity SDK (§8.6.14): the per-user pipe. The self-test uses its own pipe name so it never collides with
        // (or is mistaken for) a running Island.
        var sdk = new SdkActivityProvider(
            settings.SdkBlockedApps,
            selfTestPath is null ? null : () => $"IrisAura.Island.SelfTest.{Environment.ProcessId}");
        Add(sdk);
        var levels = _levels;
        _controller = new IslandController(
            _island,
            _manager,
            _host,
            new IslandStateStore(statePath),
            activity => activity.View switch
            {
                MediaActivityState => new MediaActivityView(activity, levels, visuals, (id, action) => _controller?.InvokeAction(id, action), id => _controller?.OpenApp(id)),
                TimerState => new TimerActivityView(activity, (id, action) => _controller?.InvokeAction(id, action)),
                BluetoothDeviceInfo => new DeviceActivityView(activity),
                _ => null,
            },
            settingsStore)
        { DebugMenuEnabled = debug, TimerProviderId = TimerProvider.ProviderId };
        _controller.HiddenProviders.Add(DebugActivityProvider.ProviderId);
        _controller.SdkApps = () => sdk.Apps;
        _controller.SetSdkAppBlocked = (key, blocked) =>
        {
            sdk.SetBlocked(key, blocked);
            _controller.SaveSettings(s => s with { SdkBlockedApps = [.. sdk.BlockedApps] });
        };

        // Dashboard panel (Phase 4B): file tray persisted next to the other state; camera mirror off unless switched on.
        _controller.Dashboard = new DashboardView(
            new FileShelfView(new FileShelfStore(StatePath(FileShelfStore.DefaultPath, "shelf"))),
            new CameraMirrorView(enabled: settings.CameraMirror && selfTestPath is null));
        _controller.QuitRequested += (_, _) => Shutdown(0);
        _controller.DebugPanelRequested += (_, _) => ShowDebugPanel();
        var timers = _timers;
        _controller.StartTimerRequested += (_, duration) => timers.StartTimer(duration);
        _island.TimerHotkeyPressed += (_, _) => timers.StartOrToggle();
        timers.Finished += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            System.Media.SystemSounds.Asterisk.Play(); // §8.6.10: banner, sound and shake
            _island.Shake();
        }));

        var processStart = Process.GetCurrentProcess().StartTime;
        _island.ContentRendered += (_, _) =>
        {
            var startupMs = (DateTime.Now - processStart).TotalMilliseconds;
            Log.Information("IrisAura Island first frame {StartupMs:0} ms after process start", startupMs);

            // After first paint: tray icon and providers (keeps time-to-first-frame short).
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _controller.ShowTrayIcon();
                _host.StartAll();
            }));

            if (selfTestPath is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    var snapshotIndex = Array.FindIndex(args, a => a.Equals("--snapshots", StringComparison.OrdinalIgnoreCase));
                    var test = new SelfTest(_island, _controller, _debugProvider, _host, _options, selfTestPath, startupMs)
                    {
                        SnapshotDirectory = snapshotIndex >= 0 && snapshotIndex + 1 < args.Length ? args[snapshotIndex + 1] : null,
                        TestCamera = args.Contains("--test-camera", StringComparer.OrdinalIgnoreCase),
                    };
                    var exitCode = perfOnly ? await test.RunPerfAsync() : await test.RunAsync();
                    Shutdown(exitCode);
                }));
            }
        };

        _island.Show();
        _controller.Start();

        Listen(QuitEventName, () => Shutdown(0));
        Listen(ShowEventName, () => _controller.ShowAgain());
        Listen(ShowDebugEventName, () =>
        {
            _controller.ShowAgain();
            ShowDebugPanel();
        });

        if (debug && selfTestPath is null)
        {
            ShowDebugPanel();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var wait in _waits)
        {
            wait.Unregister(null);
        }

        foreach (var handle in _events)
        {
            handle.Dispose();
        }

        _host?.StopAll();
        _controller?.Dispose();
        _levels?.Dispose();
        _capabilities?.Dispose();
        _island?.Close();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        Log.Information("IrisAura Island exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Logs to %LocalAppData%\IrisAura\logs (BLUEPRINT §0.2, §4): 7 daily files, 5 MB cap each.
    /// Never logs notification content, media titles, file names or other user content.
    /// </summary>
    private static void ConfigureLogging()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisAura", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(directory, "island-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5_000_000,
                rollOnFileSizeLimit: true)
            .CreateLogger();
        Log.Information("IrisAura Island starting, version {Version}", typeof(App).Assembly.GetName().Version);
    }

    private void Listen(string eventName, Action action)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        _events.Add(handle);
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => Dispatcher.BeginInvoke(action), null, Timeout.Infinite, false));
    }

    private void ShowDebugPanel()
    {
        if (_island is null || _controller is null || _debugProvider is null || _host is null || _options is null)
        {
            return;
        }

        if (_debugPanel is null)
        {
            _debugPanel = new DebugPanelWindow(_island, _controller, _debugProvider, _host, _options);
            _debugPanel.Closed += (_, _) => _debugPanel = null;
            _debugPanel.Show();
        }
        else
        {
            _debugPanel.Activate();
        }
    }

    private static void Signal(string eventName)
    {
        if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }
}
