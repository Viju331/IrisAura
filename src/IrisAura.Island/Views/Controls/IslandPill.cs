using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace IrisAura.Island.Views.Controls;

/// <summary>
/// The pill shape. A <see cref="Border"/> with its own UI Automation peer so Narrator can read the
/// current activity (BLUEPRINT §8.3 accessibility).
/// </summary>
public sealed class IslandPill : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new IslandPillAutomationPeer(this);

    private sealed class IslandPillAutomationPeer(IslandPill owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "IrisAuraIslandPill";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override bool IsKeyboardFocusableCore() => Owner.Focusable;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}
