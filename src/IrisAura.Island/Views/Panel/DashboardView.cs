using System.Globalization;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views.Panel;

/// <summary>Tabs of the panel.</summary>
public enum PanelTab
{
    Island,
    Tray,
}

/// <summary>
/// The dashboard panel shown when the Island expands (Phase 4B, BLUEPRINT §8.13A). Island tab: the "now" card (the
/// main activity's expanded view: media card, timer, call, download…), the calendar strip and the camera mirror. Tray
/// tab: the file tray. Children are made visible only while the panel and their tab show, so their timers and the
/// camera run only then.
/// </summary>
public sealed class DashboardView : ObservableObject, IIslandActivityView
{
    private readonly Func<DateTime> _today;
    private IIslandActivityView? _now;
    private PanelTab _tab = PanelTab.Island;
    private bool _visible;
    private CalendarStrip _calendar;

    public DashboardView(FileShelfView shelf, CameraMirrorView mirror, Func<DateTime>? today = null)
    {
        Shelf = shelf;
        Mirror = mirror;
        _today = today ?? (() => DateTime.Now);
        _calendar = CalendarStrip.Build(_today(), CultureInfo.CurrentCulture);
        ShowIslandCommand = new RelayCommand(() => SelectedTab = PanelTab.Island);
        ShowTrayCommand = new RelayCommand(() => SelectedTab = PanelTab.Tray);
        Shelf.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileShelfView.Summary))
            {
                OnPropertyChanged(nameof(TrayTabText));
            }
        };
    }

    public FileShelfView Shelf { get; }

    public CameraMirrorView Mirror { get; }

    public CalendarStrip Calendar => _calendar;

    public RelayCommand ShowIslandCommand { get; }

    public RelayCommand ShowTrayCommand { get; }

    public string TrayTabText => Shelf.Items.Count == 0 ? "Tray" : $"Tray · {Shelf.Items.Count}";

    /// <summary>The main activity's view, or <see langword="null"/> when nothing is happening.</summary>
    public IIslandActivityView? Now
    {
        get => _now;
        set
        {
            if (ReferenceEquals(_now, value))
            {
                return;
            }

            var showing = _visible && _tab == PanelTab.Island;
            if (showing)
            {
                Show(_now, false);
            }

            _now = value;
            if (showing)
            {
                Show(_now, true);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNow));
            OnPropertyChanged(nameof(AccessibleDescription));
        }
    }

    public bool HasNow => _now is not null;

    public PanelTab SelectedTab
    {
        get => _tab;
        set
        {
            if (_tab == value)
            {
                return;
            }

            _tab = value;
            if (_visible)
            {
                ApplyChildVisibility(previousTab: value == PanelTab.Island ? PanelTab.Tray : PanelTab.Island);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIslandTab));
            OnPropertyChanged(nameof(IsTrayTab));
        }
    }

    public bool IsIslandTab => _tab == PanelTab.Island;

    public bool IsTrayTab => _tab == PanelTab.Tray;

    public bool IsVisible => _visible;

    public string AccessibleDescription => _now is null
        ? "IrisAura panel"
        : "IrisAura panel. " + _now.AccessibleDescription;

    public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Generic;

    public double ExpandedHeight => IslandLayout.Dashboard.Height;

    public void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _visible = visible;
        if (visible)
        {
            // Dates are rebuilt when the panel opens (no midnight timer needed).
            _calendar = CalendarStrip.Build(_today(), CultureInfo.CurrentCulture);
            OnPropertyChanged(nameof(Calendar));
        }

        var island = visible && _tab == PanelTab.Island;
        Show(_now, island);
        Mirror.SetShown(island);
        OnPropertyChanged(nameof(IsVisible));
    }

    private static void Show(IIslandActivityView? view, bool shown)
    {
        if (view is null)
        {
            return;
        }

        (view as IPanelAwareView)?.SetInPanel(shown);
        view.SetVisible(shown);
    }

    private void ApplyChildVisibility(PanelTab previousTab)
    {
        var island = _tab == PanelTab.Island;
        if (previousTab == PanelTab.Island || island)
        {
            Show(_now, island);
        }

        Mirror.SetShown(island);
    }
}
