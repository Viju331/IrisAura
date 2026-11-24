using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Diagnostics;

/// <summary>Phase 1 debug panel: drive fake activities, toggle options, watch status and performance.</summary>
public partial class DebugPanelWindow : Window
{
    private readonly IslandWindow _island;
    private readonly FakeScenarioDriver _driver;
    private readonly IslandOptions _options;
    private readonly DispatcherTimer _cpuTimer;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private double _cpuPercent;
    // Suppresses option handlers while the constructor sets initial control values.
    private readonly bool _loading = true;

    public DebugPanelWindow(IslandWindow island, FakeScenarioDriver driver, IslandOptions options)
    {
        _island = island;
        _driver = driver;
        _options = options;
        InitializeComponent();

        ReducedMotionBox.SelectedIndex = (int)options.ReducedMotion;
        FollowCursorBox.IsChecked = options.Monitor == MonitorMode.FollowCursor;
        ShowOverFullscreenBox.IsChecked = options.ShowOverFullscreen;
        _loading = false;

        _cpuTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _cpuTimer.Tick += (_, _) => SampleCpu();
        _lastCpu = _process.TotalProcessorTime;
        _lastSample = DateTime.UtcNow;

        _island.StatusChanged += OnIslandStatusChanged;
        Loaded += (_, _) =>
        {
            _island.RegisterOwnedWindow(this);
            PlaceBottomRight();
            _cpuTimer.Start();
            RefreshStatus();
        };
        Closed += (_, _) =>
        {
            _cpuTimer.Stop();
            _island.StatusChanged -= OnIslandStatusChanged;
        };
    }

    private void OnIslandStatusChanged(object? sender, EventArgs e) => RefreshStatus();

    private void PlaceBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 24;
        Top = work.Bottom - ActualHeight - 24;
    }

    private void SampleCpu()
    {
        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpu = _process.TotalProcessorTime;
        var wall = (now - _lastSample).TotalMilliseconds;
        if (wall > 0)
        {
            _cpuPercent = (cpu - _lastCpu).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100;
        }

        _lastCpu = cpu;
        _lastSample = now;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var m = _island.CurrentMonitor;
        HotkeyText.Text = _island.HotkeyRegistered
            ? $"Keyboard: {_options.FocusHotkeyText} focuses the Island; Esc returns."
            : $"Keyboard: {_options.FocusHotkeyText} is in use by another app — shortcut left unbound.";

        var sb = new StringBuilder();
        sb.AppendLine($"State        {_island.VisualState}{(_island.IsPinned ? " (pinned)" : string.Empty)}");
        sb.AppendLine($"Geometry     {_island.CurrentGeometry.Width:0}×{_island.CurrentGeometry.Height:0} r{_island.CurrentGeometry.CornerRadius:0}");
        sb.AppendLine($"Monitor      {(m.IsPrimary ? "primary" : "secondary")} {m.Bounds}, work {m.WorkArea}, scale {m.Scale * 100:0}%");
        sb.AppendLine($"Click-thru   {_island.IsClickThrough}   pointer over {_island.IsPointerOver}");
        sb.AppendLine($"Hidden (FS)  {_island.IsHiddenForFullscreen}   focus mode {_island.IsFocusMode}");
        sb.AppendLine($"Reduced mo.  {_island.IsReducedMotion}");
        sb.AppendLine($"Last anim.   {_island.LastAnimation}");
        sb.AppendLine($"Process      CPU {_cpuPercent:0.00}%   working set {_process.WorkingSet64 / (1024.0 * 1024):0} MB");
        StatusText.Text = sb.ToString();
    }

    private void OnIdle(object sender, RoutedEventArgs e) => _driver.Show(FakeScenario.Idle);

    private void OnMedia(object sender, RoutedEventArgs e) => _driver.Show(FakeScenario.Media);

    private void OnTimer(object sender, RoutedEventArgs e) => _driver.Show(FakeScenario.Timer);

    private void OnSplit(object sender, RoutedEventArgs e) => _driver.Show(FakeScenario.MediaAndTimer);

    private void OnBanner(object sender, RoutedEventArgs e) => _driver.ShowNotification();

    private void OnPin(object sender, RoutedEventArgs e) => _island.TogglePin();

    private void OnShake(object sender, RoutedEventArgs e) => _island.Shake();

    private void OnSwap(object sender, RoutedEventArgs e) => _driver.Swap();

    private void OnReposition(object sender, RoutedEventArgs e) => _island.Reposition();

    private void OnReducedMotionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _options.ReducedMotion = (ReducedMotionMode)ReducedMotionBox.SelectedIndex;
        _island.ApplyOptions();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _options.Monitor = FollowCursorBox.IsChecked == true ? MonitorMode.FollowCursor : MonitorMode.Primary;
        _options.ShowOverFullscreen = ShowOverFullscreenBox.IsChecked == true;
        _island.ApplyOptions();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
