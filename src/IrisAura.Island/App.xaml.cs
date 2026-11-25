using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using IrisAura.Island.Diagnostics;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Media;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;
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
        _debugProvider = new DebugActivityProvider();
        _host.Register(_debugProvider);
        _host.Register(new MediaProvider(() => new GsmtcMediaSessionSource()));
        _levels = new CoreAudioLevelMeter();
        var visuals = new AppVisualsResolver();

        // The self-test must not change the user's real persisted state.
        var statePath = selfTestPath is null
            ? IslandStateStore.DefaultPath
            : Path.Combine(Path.GetTempPath(), $"irisaura-selftest-state-{Environment.ProcessId}.json");
        var levels = _levels;
        _controller = new IslandController(
            _island,
            _manager,
            _host,
            new IslandStateStore(statePath),
            activity => activity.View is MediaActivityState
                ? new MediaActivityView(activity, levels, visuals, (id, action) => _controller?.InvokeAction(id, action), id => _controller?.OpenApp(id))
                : null)
        { DebugMenuEnabled = debug };
        _controller.QuitRequested += (_, _) => Shutdown(0);
        _controller.DebugPanelRequested += (_, _) => ShowDebugPanel();
        _controller.StartTimerRequested += (_, duration) => _debugProvider.StartTimer(duration); // real timers: Phase 4

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
                    var test = new SelfTest(_island, _controller, _debugProvider, _host, _options, selfTestPath, startupMs);
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
