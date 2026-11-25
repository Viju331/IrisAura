using IrisAura.Island.Engine;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Views;

/// <summary>A view model the controller keeps per activity id and updates in place (no cross-fade on updates).</summary>
public interface IActivityViewModel : IIslandActivityView
{
    void Apply(IslandActivity activity);
}
