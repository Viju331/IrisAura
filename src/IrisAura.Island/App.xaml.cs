using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using IrisAura.Island.Diagnostics;
using IrisAura.Island.Shell;

namespace IrisAura.Island;

/// <summary>
/// IrisAura Island entry point. One instance per user session.
/// <list type="bullet">
/// <item><c>--debug</c>: show the Phase 1 debug panel (always shown in Debug builds).</item>
/// <item><c>--quit</c>: ask the running instance to exit.</item>
/// <item><c>--selftest &lt;report.json&gt;</c>: run the integration self-test, write the report, exit (0 = all passed).</item>
/// </list>
/// A second launch without arguments opens the debug panel of the running instance.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\IrisAura.Island.Instance";
    private const string QuitEventName = @"Local\IrisAura.Island.Quit";
    private const string ShowDebugEventName = @"Local\IrisAura.Island.ShowDebug";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _quitEvent;
    private EventWaitHandle? _showDebugEvent;
    private RegisteredWaitHandle? _quitWait;
    private RegisteredWaitHandle? _showDebugWait;
    private IslandWindow? _island;
    private FakeScenarioDriver? _driver;
    private IslandOptions? _options;
    private DebugPanelWindow? _debugPanel;

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

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance owns the session: forward the request and exit.
            Signal(quit ? QuitEventName : ShowDebugEventName);
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

        _options = new IslandOptions();
        _island = new IslandWindow(_options);
        _driver = new FakeScenarioDriver(_island);

        var processStart = Process.GetCurrentProcess().StartTime;
        var startupMs = 0.0;
        _island.ContentRendered += (_, _) =>
        {
            startupMs = (DateTime.Now - processStart).TotalMilliseconds;
            Trace.TraceInformation($"IrisAura.Island first frame {startupMs:0} ms after process start.");
            if (selfTestPath is not null)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
                {
                    var test = new SelfTest(_island, _driver, _options, selfTestPath, startupMs);
                    var exitCode = perfOnly ? await test.RunPerfAsync() : await test.RunAsync();
                    Shutdown(exitCode);
                }));
            }
        };

        _island.Show();

        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _showDebugEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowDebugEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Dispatcher.BeginInvoke(() => Shutdown(0)), null, Timeout.Infinite, false);
        _showDebugWait = ThreadPool.RegisterWaitForSingleObject(_showDebugEvent, (_, _) => Dispatcher.BeginInvoke(ShowDebugPanel), null, Timeout.Infinite, false);

#if DEBUG
        debug = true;
#endif
        if (debug && selfTestPath is null)
        {
            ShowDebugPanel();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitWait?.Unregister(null);
        _showDebugWait?.Unregister(null);
        _quitEvent?.Dispose();
        _showDebugEvent?.Dispose();
        _island?.Close();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void ShowDebugPanel()
    {
        if (_island is null || _driver is null || _options is null)
        {
            return;
        }

        if (_debugPanel is null)
        {
            _debugPanel = new DebugPanelWindow(_island, _driver, _options);
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
