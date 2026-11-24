using System.Windows;
using FluentAssertions;
using IrisAura.Island.Diagnostics;
using IrisAura.Island.Shell;

namespace IrisAura.Island.Tests;

/// <summary>Layout, hover intent, placement, full-screen policy and frame stats (BLUEPRINT §8.2, §8.3, §8.8).</summary>
public sealed class ShellLogicTests
{
    // ---------------------------------------------------------------- layout (§8.2 table)

    [Fact]
    public void Visual_state_sizes_match_blueprint_table()
    {
        IslandLayout.For(IslandVisualState.Idle).Should().Be(new PillGeometry(120, 32, 16));
        IslandLayout.For(IslandVisualState.Compact).Width.Should().BeInRange(260, 320);
        IslandLayout.For(IslandVisualState.Compact).Height.Should().Be(36);
        IslandLayout.For(IslandVisualState.Compact).CornerRadius.Should().Be(18);
        IslandLayout.For(IslandVisualState.Split).Should().Be(new PillGeometry(260, 36, 18, 36, 8));
        IslandLayout.For(IslandVisualState.Banner).Should().Be(new PillGeometry(380, 72, 28));
        IslandLayout.For(IslandVisualState.Expanded, ExpandedKind.Media).Should().Be(new PillGeometry(400, 180, 36));
        IslandLayout.For(IslandVisualState.Expanded, ExpandedKind.Generic, 140).Should().Be(new PillGeometry(400, 140, 32));
        IslandLayout.ExpandedGeneric(50).Height.Should().Be(120);
        IslandLayout.ExpandedGeneric(500).Height.Should().Be(200);
    }

    [Theory]
    [InlineData(IslandVisualState.Idle)]
    [InlineData(IslandVisualState.Compact)]
    [InlineData(IslandVisualState.Split)]
    [InlineData(IslandVisualState.Banner)]
    [InlineData(IslandVisualState.Expanded)]
    public void Every_state_fits_the_fixed_window_with_overshoot_room(IslandVisualState state)
    {
        var g = IslandLayout.For(state, ExpandedKind.Generic, IslandLayout.GenericExpandedMax);
        var overshoot = 1.08; // expand spring overshoots < 8 %

        (g.TotalWidth * overshoot).Should().BeLessThanOrEqualTo(IslandLayout.WindowWidth);
        (IslandLayout.TopInset + (g.Height * overshoot)).Should().BeLessThanOrEqualTo(IslandLayout.WindowHeight);
    }

    [Fact]
    public void Pill_and_bubble_are_centred_together()
    {
        var split = IslandLayout.Split;
        var pill = IslandLayout.PillRect(split);
        var bubble = IslandLayout.BubbleRect(split)!.Value;

        var left = pill.Left;
        var right = bubble.Right;
        (left + right).Should().BeApproximately(IslandLayout.WindowWidth, 0.001, "group is horizontally centred");
        (bubble.Left - pill.Right).Should().Be(IslandLayout.SplitGap);
        (bubble.Top + (bubble.Height / 2)).Should().Be(pill.Top + (pill.Height / 2), "bubble is vertically centred on the pill");
        IslandLayout.BubbleRect(IslandLayout.Compact).Should().BeNull();
    }

    [Fact]
    public void Hot_zone_above_the_pill_counts_as_over_the_island()
    {
        var g = IslandLayout.Compact;
        var pill = IslandLayout.PillRect(g);
        var centreX = pill.Left + (pill.Width / 2);

        IslandLayout.IsOverInteractiveArea(g, new Point(centreX, 0)).Should().BeTrue("top edge of the screen reaches the pill");
        IslandLayout.IsOverInteractiveArea(g, new Point(centreX, pill.Top + 10)).Should().BeTrue();
        IslandLayout.IsOverInteractiveArea(g, new Point(centreX, pill.Bottom + 2)).Should().BeFalse("shadow is not interactive");
        IslandLayout.IsOverInteractiveArea(g, new Point(pill.Left - 2, pill.Top + 10)).Should().BeFalse();
        IslandLayout.IsOverInteractiveArea(IslandLayout.Split, new Point(IslandLayout.BubbleRect(IslandLayout.Split)!.Value.Left + 18, 24))
            .Should().BeTrue("the bubble is interactive");
    }

    // ---------------------------------------------------------------- hover intent (§8.1, §8.5)

    [Fact]
    public void Hover_expands_after_150_ms_not_before()
    {
        var hover = new HoverIntent();
        hover.Update(true, Ms(0), canExpand: true).Should().Be(HoverChange.None);
        hover.Update(true, Ms(140), canExpand: true).Should().Be(HoverChange.None);
        hover.Update(true, Ms(150), canExpand: true).Should().Be(HoverChange.Expand);
        hover.IsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Leaving_collapses_after_400_ms_and_a_quick_return_cancels_it()
    {
        var hover = Expanded();
        hover.Update(false, Ms(1000), true).Should().Be(HoverChange.None);
        hover.Update(false, Ms(1300), true).Should().Be(HoverChange.None);
        hover.Update(true, Ms(1350), true).Should().Be(HoverChange.None, "came back within 400 ms");
        hover.Update(false, Ms(1400), true).Should().Be(HoverChange.None);
        hover.Update(false, Ms(1799), true).Should().Be(HoverChange.None);
        hover.Update(false, Ms(1800), true).Should().Be(HoverChange.Collapse);
    }

    [Fact]
    public void Pinned_island_does_not_collapse_on_leave()
    {
        var hover = new HoverIntent();
        hover.Pin(canExpand: true).Should().Be(HoverChange.Expand);
        hover.Update(false, Ms(0), true).Should().Be(HoverChange.None);
        hover.Update(false, Ms(5000), true).Should().Be(HoverChange.None);
        hover.Unpin(Ms(5000)).Should().Be(HoverChange.Collapse, "cursor is away when unpinned");
    }

    [Fact]
    public void Unpin_while_hovering_keeps_it_open_until_the_cursor_leaves()
    {
        var hover = new HoverIntent();
        hover.Update(true, Ms(0), true);
        hover.Pin(true);
        hover.Unpin(Ms(100)).Should().Be(HoverChange.None);
        hover.Update(false, Ms(200), true).Should().Be(HoverChange.None);
        hover.Update(false, Ms(600), true).Should().Be(HoverChange.Collapse);
    }

    [Fact]
    public void Content_that_cannot_expand_never_expands_and_resets_pin()
    {
        var hover = Expanded();
        hover.Pin(true);
        hover.Update(true, Ms(2000), canExpand: false).Should().Be(HoverChange.Collapse);
        hover.IsPinned.Should().BeFalse();
        hover.Update(true, Ms(5000), canExpand: false).Should().Be(HoverChange.None);
        hover.Pin(canExpand: false).Should().Be(HoverChange.None);
    }

    // ---------------------------------------------------------------- placement (§8.3)

    [Fact]
    public void Placed_top_centre_of_work_area_at_100_percent()
    {
        var monitor = Monitor(bounds: new PixelRect(0, 0, 1920, 1080), work: new PixelRect(0, 0, 1920, 1032), scale: 1.0);
        PlacementCalculator.Compute(monitor, 448, 210).Should().Be(new PixelRect(736, 0, 448, 210));
    }

    [Fact]
    public void Placed_below_a_top_taskbar_and_scaled_at_150_percent()
    {
        var monitor = Monitor(bounds: new PixelRect(0, 0, 2880, 1800), work: new PixelRect(0, 72, 2880, 1728), scale: 1.5);
        PlacementCalculator.Compute(monitor, 448, 210).Should().Be(new PixelRect(1104, 72, 672, 315));
    }

    [Fact]
    public void Placed_on_a_secondary_monitor_left_of_primary_at_200_percent()
    {
        var monitor = Monitor(bounds: new PixelRect(-3840, -200, 3840, 2160), work: new PixelRect(-3840, -200, 3840, 2100), scale: 2.0);
        var rect = PlacementCalculator.Compute(monitor, 448, 210);
        rect.Should().Be(new PixelRect(-2368, -200, 896, 420));
        ((rect.Left + rect.Right) / 2).Should().Be(-1920, "centred on that monitor");
    }

    [Fact]
    public void Screen_and_window_dip_conversions_round_trip()
    {
        var window = new PixelRect(1104, 72, 672, 315);
        var point = PlacementCalculator.ScreenToWindowDip(window, 1.5, 1104 + 300, 72 + 30);
        point.Should().Be(new Point(200, 20));

        var rect = PlacementCalculator.WindowDipToScreen(window, 1.5, new Rect(74, 6, 300, 36));
        rect.Should().Be(new PixelRect(1104 + 111, 72 + 9, 450, 54));
    }

    // ---------------------------------------------------------------- full-screen policy (§8.3)

    [Theory]
    [InlineData(UserNotificationState.Busy, true)]
    [InlineData(UserNotificationState.RunningD3DFullScreen, true)]
    [InlineData(UserNotificationState.PresentationMode, true)]
    [InlineData(UserNotificationState.AcceptsNotifications, false)]
    [InlineData(UserNotificationState.QuietTime, false)]
    [InlineData(UserNotificationState.Unknown, false)]
    public void Shell_notification_state_drives_hiding(UserNotificationState state, bool hide)
    {
        FullscreenPolicy.ShouldHide(state, null, PrimaryMonitor, showOverFullscreen: false).Should().Be(hide);
    }

    [Fact]
    public void Borderless_window_covering_the_monitor_hides_the_island()
    {
        var fg = Foreground(new PixelRect(0, 0, 1920, 1080));
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, fg, PrimaryMonitor, false).Should().BeTrue();
    }

    [Fact]
    public void Maximised_window_with_title_bar_does_not_hide_it_even_with_auto_hide_taskbar()
    {
        // Maximised windows extend ~8 px past the monitor because of invisible resize borders.
        var fg = Foreground(new PixelRect(-8, -8, 1936, 1096)) with { IsMaximizedWithCaption = true };
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, fg, PrimaryMonitor, false).Should().BeFalse();
    }

    [Fact]
    public void Window_covering_only_the_work_area_does_not_hide_it()
    {
        var fg = Foreground(new PixelRect(0, 0, 1920, 1032));
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, fg, PrimaryMonitor, false).Should().BeFalse();
    }

    [Fact]
    public void Full_screen_window_on_another_monitor_does_not_hide_it()
    {
        var fg = Foreground(new PixelRect(1920, 0, 2560, 1440)) with { Monitor = 2 };
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, fg, PrimaryMonitor, false).Should().BeFalse();
    }

    [Fact]
    public void Desktop_shell_and_island_windows_are_ignored()
    {
        var cover = new PixelRect(0, 0, 1920, 1080);
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, Foreground(cover) with { IsShellOrDesktop = true }, PrimaryMonitor, false)
            .Should().BeFalse();
        FullscreenPolicy.ShouldHide(UserNotificationState.AcceptsNotifications, Foreground(cover) with { IsIslandWindow = true }, PrimaryMonitor, false)
            .Should().BeFalse();
        FullscreenPolicy.ShellWindowClasses.Should().Contain(["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"]);
    }

    [Fact]
    public void Show_over_full_screen_setting_disables_hiding()
    {
        var fg = Foreground(new PixelRect(0, 0, 1920, 1080));
        FullscreenPolicy.ShouldHide(UserNotificationState.RunningD3DFullScreen, fg, PrimaryMonitor, showOverFullscreen: true)
            .Should().BeFalse();
    }

    // ---------------------------------------------------------------- frame stats (§8.8)

    [Fact]
    public void Frame_stats_count_frames_over_25_ms_as_dropped()
    {
        var stats = new FrameStats();
        foreach (var ms in new[] { 16.6, 16.7, 16.6, 33.4, 16.7, 0, 16.6, 50.1 })
        {
            stats.Add(ms);
        }

        var summary = stats.Summarize();
        summary.Frames.Should().Be(7, "zero intervals are ignored");
        summary.Dropped.Should().Be(2);
        summary.MaxMs.Should().Be(50.1);
        summary.AverageMs.Should().BeApproximately(166.7 / 7, 0.0001);
        new FrameStats().Summarize().Frames.Should().Be(0);
    }

    // ---------------------------------------------------------------- presentation

    [Fact]
    public void Only_compact_and_split_with_expandable_primary_can_expand()
    {
        var media = new FakeActivityStub(ExpandedKind.Media);
        var banner = new FakeActivityStub(null);

        new IslandPresentation(IslandBaseState.Compact, media).CanExpand.Should().BeTrue();
        new IslandPresentation(IslandBaseState.Split, media, banner).CanExpand.Should().BeTrue();
        new IslandPresentation(IslandBaseState.Banner, media).CanExpand.Should().BeFalse();
        new IslandPresentation(IslandBaseState.Compact, banner).CanExpand.Should().BeFalse();
        IslandPresentation.Idle.CanExpand.Should().BeFalse();
    }

    private static readonly MonitorDescriptor PrimaryMonitor =
        new(1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1.0, true);

    private static MonitorDescriptor Monitor(PixelRect bounds, PixelRect work, double scale) => new(1, bounds, work, scale, true);

    private static ForegroundWindowInfo Foreground(PixelRect rect) => new(rect, 1, false, false, false);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static HoverIntent Expanded()
    {
        var hover = new HoverIntent();
        hover.Update(true, Ms(0), true);
        hover.Update(true, Ms(200), true);
        return hover;
    }

    private sealed class FakeActivityStub(ExpandedKind? kind) : IIslandActivityView
    {
        public string AccessibleDescription => "stub";

        public ExpandedKind? ExpandedKind => kind;

        public double ExpandedHeight => 140;

        public void SetVisible(bool visible)
        {
        }
    }
}
