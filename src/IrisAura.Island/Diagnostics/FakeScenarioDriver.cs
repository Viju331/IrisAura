using System.Windows.Threading;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Diagnostics;

/// <summary>Fake scenarios for the Phase 1 debug panel and self-test.</summary>
public enum FakeScenario
{
    Idle,
    Media,
    Timer,

    /// <summary>Timer (priority 60) in the pill, media (50) in the bubble — BLUEPRINT §8.5 defaults.</summary>
    MediaAndTimer,
}

/// <summary>
/// Drives the Island with fake activities (BLUEPRINT §10 Phase 1). The real activity engine and
/// priority arbiter replace this in Phase 2; the shell API it uses (<see cref="IslandWindow.SetPresentation"/>)
/// stays the same.
/// </summary>
public sealed class FakeScenarioDriver
{
    public static readonly TimeSpan BannerDuration = TimeSpan.FromSeconds(4);

    private readonly IslandWindow _island;
    private readonly DispatcherTimer _bannerTimer;
    private IslandPresentation _base = IslandPresentation.Idle;
    private IslandPresentation? _banner;

    public FakeScenarioDriver(IslandWindow island)
    {
        _island = island;
        _bannerTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = BannerDuration };
        _bannerTimer.Tick += (_, _) => EndBannerIfNotHovered();

        _island.BubbleClicked += (_, _) => Swap();
        _island.PointerOverChanged += (_, over) =>
        {
            // Hovering holds a banner open (BLUEPRINT §8.6.9); leaving restarts its timeout.
            if (_banner is not null && !over)
            {
                _bannerTimer.Stop();
                _bannerTimer.Start();
            }
        };
        Timer.Cancelled += (_, _) => Show(Scenario switch
        {
            FakeScenario.MediaAndTimer => FakeScenario.Media,
            _ => FakeScenario.Idle,
        });
    }

    public FakeMediaActivity Media { get; } = new();

    public FakeTimerActivity Timer { get; } = new(TimeSpan.FromMinutes(5));

    public FakeScenario Scenario { get; private set; }

    public bool IsBannerShowing => _banner is not null;

    public void Show(FakeScenario scenario)
    {
        Scenario = scenario;
        _base = scenario switch
        {
            FakeScenario.Media => new IslandPresentation(IslandBaseState.Compact, Media),
            FakeScenario.Timer => new IslandPresentation(IslandBaseState.Compact, Timer),
            FakeScenario.MediaAndTimer => new IslandPresentation(IslandBaseState.Split, Timer, Media),
            _ => IslandPresentation.Idle,
        };
        Apply();
    }

    /// <summary>Shows a transient notification banner that returns to the previous layout after 4 s.</summary>
    public void ShowNotification()
    {
        _banner = new IslandPresentation(
            IslandBaseState.Banner,
            new FakeBannerActivity("Messages", "Alex", "Are we still on for the 4 pm review?"));
        _bannerTimer.Stop();
        _bannerTimer.Start();
        Apply();
    }

    /// <summary>Swaps pill and bubble (BLUEPRINT §8.5 user override).</summary>
    public void Swap()
    {
        if (_base.State != IslandBaseState.Split)
        {
            return;
        }

        _base = _base with { Primary = _base.Secondary, Secondary = _base.Primary };
        Apply();
    }

    private void EndBannerIfNotHovered()
    {
        if (_island.IsPointerOver)
        {
            return; // Keep waiting; PointerOverChanged restarts the timer on leave.
        }

        _bannerTimer.Stop();
        _banner = null;
        Apply();
    }

    private void Apply() => _island.SetPresentation(_banner ?? _base);
}
