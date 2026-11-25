using IrisAura.Island.Engine;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views;

/// <summary>A view model the controller keeps per activity id and updates in place (no cross-fade on updates).</summary>
public interface IActivityViewModel : IIslandActivityView
{
    void Apply(IslandActivity activity);
}

/// <summary>
/// A view that animates more slowly inside the dashboard panel. The panel's window is about 1.8× larger, and every
/// redraw of a layered window copies the whole surface, so a 30 Hz waveform there would break the §8.8 CPU budget
/// (measured 4.4 %); 15 Hz keeps it within budget and still looks smooth at that bar size.
/// </summary>
public interface IPanelAwareView
{
    void SetInPanel(bool inPanel);
}
