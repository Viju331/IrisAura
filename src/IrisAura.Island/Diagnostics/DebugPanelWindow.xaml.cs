using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using IrisAura.Island.Engine;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Diagnostics;

/// <summary>Debug panel: fake activities through the real engine, arbiter layout, provider states, performance.</summary>
public partial class DebugPanelWindow : Window
{
    private readonly IslandWindow _island;
    private readonly IslandController _controller;
    private readonly DebugActivityProvider _provider;
    private readonly ActivityProviderHost _host;
    private readonly IslandOptions _options;
    private readonly DispatcherTimer _cpuTimer;
    private readonly Process _process = Process.GetCurrentProcess();

    // Suppresses option handlers while the constructor sets initial control values.
    private readonly bool _loading = true;
    private TimeSpan _lastCpu;
    private DateTime _lastSample;
    private double _cpuPercent;

    public DebugPanelWindow(
        IslandWindow island,
        IslandController controller,
        DebugActivityProvider provider,
        ActivityProviderHost host,
        IslandOptions options)
    {
        _island = island;
        _controller = controller;
        _provider = provider;
        _host = host;
        _options = options;
        InitializeComponent();

        ReducedMotionBox.SelectedIndex = (int)options.ReducedMotion;
        FollowCursorBox.IsChecked = options.Monitor == MonitorMode.FollowCursor;
        ShowOverFullscreenBox.IsChecked = options.ShowOverFullscreen;
        QuietBox.IsChecked = controller.QuietMode;
        _loading = false;

        _cpuTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _cpuTimer.Tick += (_, _) => SampleCpu();
        _lastCpu = _process.TotalProcessorTime;
        _lastSample = DateTime.UtcNow;

        _island.StatusChanged += OnStatusChanged;
        _controller.LayoutChanged += OnLayoutChanged;
        _host.ProviderStateChanged += OnProviderChanged;
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
            _island.StatusChanged -= OnStatusChanged;
            _controller.LayoutChanged -= OnLayoutChanged;
            _host.ProviderStateChanged -= OnProviderChanged;
        };
    }

    private void OnStatusChanged(object? sender, EventArgs e) => RefreshStatus();

    private void OnLayoutChanged(object? sender, ActivityLayout e) => RefreshStatus();

    private void OnProviderChanged(object? sender, ProviderStatus e) => RefreshStatus();

    private void PlaceBottomRight()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth - 24;
        Top = Math.Max(work.Top, work.Bottom - ActualHeight - 24);
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
        HotkeyText.Text = _island.HotkeyRegistered
            ? $"Keyboard: {_options.FocusHotkeyText} focuses the Island (or shows it when hidden); Esc returns. Right-click the pill or the tray icon for the menu."
            : $"Keyboard: {_options.FocusHotkeyText} is in use by another app — shortcut left unbound.";

        static string Name(IslandActivity? a) => a is null ? "—" : $"{a.Id} ({a.Priority}{(a.PausedSince is null ? string.Empty : ", paused")})";

        var layout = _controller.Layout;
        var m = _island.CurrentMonitor;
        var sb = new StringBuilder();
        sb.AppendLine($"Transient    {Name(layout.Transient)}{(layout.WaitingTransients.Count > 0 ? $" (+{layout.WaitingTransients.Count} waiting)" : string.Empty)}");
        sb.AppendLine($"Main         {Name(layout.Main)}");
        sb.AppendLine($"Bubble       {Name(layout.Bubble)}");
        sb.AppendLine($"Queue        {(layout.Queue.Count == 0 ? "—" : string.Join(", ", layout.Queue.Select(Name)))}");
        if (layout.Suppressed.Count > 0)
        {
            sb.AppendLine($"Suppressed   {string.Join(", ", layout.Suppressed.Select(a => a.Id))} (quiet)");
        }

        sb.AppendLine($"Preference   {(_controller.Preference is { } p ? $"{p.PreferredMainId}{(p.PartnerId is null ? string.Empty : " ⇄ " + p.PartnerId)}" : "—")}");
        sb.AppendLine($"Quiet        user {_controller.QuietMode}, Windows quiet time {_controller.ShellQuietTime}");
        foreach (var provider in _host.Providers)
        {
            sb.AppendLine($"Provider     {provider.Id}: {provider.State}{(provider.LastError is null ? string.Empty : " — " + provider.LastError)}");
        }

        sb.AppendLine();
        sb.AppendLine($"State        {_island.VisualState}{(_island.IsPinned ? " (pinned)" : string.Empty)}");
        sb.AppendLine($"Monitor      {(m.IsPrimary ? "primary" : "secondary")} {m.Bounds}, work {m.WorkArea}, scale {m.Scale * 100:0}%");
        sb.AppendLine($"Hidden       full screen {_island.IsHiddenForFullscreen}, by user {_island.IsUserHidden}   tray {_controller.TrayIconVisible}");
        sb.AppendLine($"Click-thru   {_island.IsClickThrough}   pointer over {_island.IsPointerOver}   focus mode {_island.IsFocusMode}");
        sb.AppendLine($"Last anim.   {_island.LastAnimation}");
        sb.AppendLine($"Process      CPU {_cpuPercent:0.00}%   working set {_process.WorkingSet64 / (1024.0 * 1024):0} MB");
        StatusText.Text = sb.ToString();
    }

    private void OnMedia(object sender, RoutedEventArgs e) => _provider.StartMedia();

    private void OnPlayPause(object sender, RoutedEventArgs e) => _provider.Media.TogglePlayPause();

    private void OnTimer(object sender, RoutedEventArgs e) => _provider.StartTimer(TimeSpan.FromMinutes(5));

    private void OnShortTimer(object sender, RoutedEventArgs e) => _provider.StartTimer(TimeSpan.FromSeconds(12));

    private void OnCall(object sender, RoutedEventArgs e) => _provider.StartCall();

    private void OnDownload(object sender, RoutedEventArgs e) => _provider.StartDownload();

    private void OnCustom(object sender, RoutedEventArgs e) => _provider.StartCustom(35);

    private void OnEndAll(object sender, RoutedEventArgs e) => _provider.EndPersistent();

    private void OnBanner(object sender, RoutedEventArgs e) => _provider.Notify();

    private void OnVolumeUp(object sender, RoutedEventArgs e) => _provider.VolumeStep(0.1);

    private void OnVolumeDown(object sender, RoutedEventArgs e) => _provider.VolumeStep(-0.1);

    private void OnCharging(object sender, RoutedEventArgs e) => _provider.Charging();

    private void OnSwap(object sender, RoutedEventArgs e) => _controller.Swap();

    private void OnCycle(object sender, RoutedEventArgs e) => _controller.Cycle(1);

    private void OnPin(object sender, RoutedEventArgs e) => _island.TogglePin();

    private void OnShake(object sender, RoutedEventArgs e) => _island.Shake();

    private void OnHide(object sender, RoutedEventArgs e) => _controller.HideForAnHour();

    private void OnShow(object sender, RoutedEventArgs e) => _controller.ShowAgain();

    private void OnFail(object sender, RoutedEventArgs e) => _provider.Fail();

    private void OnRestart(object sender, RoutedEventArgs e) => _host.Start(DebugActivityProvider.ProviderId);

    private void OnQuietChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _controller.SetQuietMode(QuietBox.IsChecked == true);
        }
    }

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
