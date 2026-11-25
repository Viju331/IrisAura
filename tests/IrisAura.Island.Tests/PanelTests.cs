using System.Globalization;
using System.IO;
using FluentAssertions;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Devices;
using IrisAura.Island.Shell;
using IrisAura.Island.Views;
using IrisAura.Island.Views.Panel;

namespace IrisAura.Island.Tests;

/// <summary>Dashboard panel (Phase 4B, BLUEPRINT §8.13A): layout, file tray, calendar strip, panel visibility, device view.</summary>
public sealed class PanelTests
{
    // ---------------------------------------------------------------- layout

    [Fact]
    public void Panel_fits_its_window_with_overshoot_room()
    {
        var g = IslandLayout.Dashboard;
        (g.TotalWidth * 1.08).Should().BeLessThanOrEqualTo(IslandLayout.PanelWindowWidth);
        (IslandLayout.TopInset + (g.Height * 1.08)).Should().BeLessThanOrEqualTo(IslandLayout.PanelWindowHeight);
        IslandLayout.PanelWindowWidth.Should().BeGreaterThan(IslandLayout.WindowWidth, "the window grows only for the panel");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Pill_stays_at_the_same_screen_position_when_the_window_grows(double scale)
    {
        var monitor = new MonitorDescriptor(1, new PixelRect(0, 0, 2560, 1440), new PixelRect(0, 0, 2560, 1400), scale, true);
        var small = PlacementCalculator.Compute(monitor, IslandLayout.WindowWidth, IslandLayout.WindowHeight);
        var large = PlacementCalculator.Compute(monitor, IslandLayout.PanelWindowWidth, IslandLayout.PanelWindowHeight);

        var g = IslandLayout.Compact;
        var inSmall = small.Left + (IslandLayout.PillRect(g, IslandLayout.WindowWidth).Left * scale);
        var inLarge = large.Left + (IslandLayout.PillRect(g, IslandLayout.PanelWindowWidth).Left * scale);

        inLarge.Should().BeApproximately(inSmall, 1.0, "the window is resized around the same top-centre point");
        large.Top.Should().Be(small.Top);
    }

    // ---------------------------------------------------------------- file tray

    [Fact]
    public void Tray_keeps_newest_first_without_duplicates_and_skips_missing_files()
    {
        var shelf = new FileShelf();
        var exists = (string p) => !p.Contains("missing", StringComparison.Ordinal);

        shelf.Add([@"C:\a.txt", @"C:\b.txt", @"C:\missing.txt"], exists).Should().BeTrue();
        shelf.Items.Should().Equal(@"C:\b.txt", @"C:\a.txt");

        shelf.Add([@"c:\A.TXT"], exists).Should().BeTrue("an existing item moves to the front");
        shelf.Items.Should().HaveCount(2);
        shelf.Items[0].Should().BeEquivalentTo(@"c:\A.TXT");

        shelf.Add([@"c:\a.txt"], exists).Should().BeFalse("already first");
        shelf.Add([@"D:\Projects\"], exists);
        shelf.Items[0].Should().Be(@"D:\Projects", "trailing separators are trimmed");
        shelf.Add([@"E:\"], exists);
        shelf.Items[0].Should().Be(@"E:\", "a drive root keeps its separator");

        shelf.Remove(@"C:\B.txt").Should().BeTrue();
        shelf.Items.Should().NotContain(@"C:\b.txt");
        shelf.Clear().Should().BeTrue();
        shelf.Items.Should().BeEmpty();
        shelf.Clear().Should().BeFalse();
    }

    [Fact]
    public void Tray_is_capped_and_prunes_moved_files()
    {
        var shelf = new FileShelf();
        shelf.Add(Enumerable.Range(0, 40).Select(i => $@"C:\f{i}.txt"), _ => true);
        shelf.Items.Should().HaveCount(FileShelf.MaxItems);
        shelf.Items[0].Should().Be(@"C:\f39.txt", "newest first; the oldest dropped off");

        shelf.PruneMissing(p => !p.EndsWith("f39.txt", StringComparison.Ordinal)).Should().BeTrue();
        shelf.Items.Should().NotContain(@"C:\f39.txt");
    }

    [Fact]
    public void Tray_list_round_trips_in_order_and_survives_a_corrupt_file()
    {
        var dir = Directory.CreateTempSubdirectory("irisaura-shelf-").FullName;
        try
        {
            var store = new FileShelfStore(Path.Combine(dir, "island-shelf.json"));
            store.Load().Should().BeEmpty();
            var shelf = new FileShelf();
            shelf.Add([@"C:\1.txt", @"C:\2.txt", @"C:\3.txt"], _ => true);
            store.Save(shelf.Items);

            new FileShelf(store.Load()).Items.Should().Equal(shelf.Items, "order is kept across restarts");

            File.WriteAllText(store.FilePath, "{ nope");
            store.Load().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- calendar strip

    [Fact]
    public void Calendar_strip_centres_today_and_crosses_month_ends()
    {
        var strip = CalendarStrip.Build(new DateTime(2026, 12, 31), CultureInfo.GetCultureInfo("en-GB"));

        strip.Month.Should().Be("December");
        strip.Year.Should().Be("2026");
        strip.Days.Select(d => d.Number).Should().Equal(29, 30, 31, 1, 2);
        strip.Days.Single(d => d.IsToday).Number.Should().Be(31);
        strip.Days[2].AccessibleName.Should().Be("Thursday 31 December, today");
        strip.Days[0].Initial.Should().Be(CultureInfo.GetCultureInfo("en-GB").DateTimeFormat.GetShortestDayName(DayOfWeek.Tuesday));
    }

    [Fact]
    public void Calendar_strip_uses_the_users_language()
    {
        var strip = CalendarStrip.Build(new DateTime(2026, 11, 25), CultureInfo.GetCultureInfo("hi-IN"));
        strip.Month.Should().NotBe("November");
        strip.Days.Should().HaveCount(5);
    }

    // ---------------------------------------------------------------- panel visibility

    [Fact]
    public void Panel_shows_the_now_card_only_while_open_on_the_Island_tab_and_never_starts_a_switched_off_camera()
    {
        StaThread.Run(() =>
        {
            var mirror = new CameraMirrorView(enabled: false);
            var panel = new DashboardView(new FileShelfView(null, _ => true), mirror, () => new DateTime(2026, 11, 25));
            var media = new RecordingView();
            var timer = new RecordingView();

            panel.Now = media;
            media.Visible.Should().BeFalse("the panel is closed");

            panel.SetVisible(true);
            media.Visible.Should().BeTrue();

            panel.Now = timer;
            media.Visible.Should().BeFalse("replaced while open");
            timer.Visible.Should().BeTrue();

            panel.SelectedTab = PanelTab.Tray;
            timer.Visible.Should().BeFalse("not shown on the Tray tab");
            panel.SelectedTab = PanelTab.Island;
            timer.Visible.Should().BeTrue();

            panel.SetVisible(false);
            timer.Visible.Should().BeFalse();

            mirror.State.Should().Be(MirrorState.Off);
            mirror.IsCameraActive.Should().BeFalse("the mirror is off by default and never opened the camera");
            panel.Calendar.Days.Single(d => d.IsToday).Number.Should().Be(25);
        });
    }

    [Fact]
    public void Switching_the_mirror_on_is_reported_for_the_setting()
    {
        StaThread.Run(() =>
        {
            var mirror = new CameraMirrorView(enabled: false);
            var reported = new List<bool>();
            mirror.EnabledChanged += (_, on) => reported.Add(on);

            mirror.SetEnabled(true); // panel not showing: the camera stays closed
            mirror.IsEnabled.Should().BeTrue();
            mirror.ToggleText.Should().Be("Turn off");
            mirror.IsCameraActive.Should().BeFalse();
            mirror.SetEnabled(false);

            reported.Should().Equal(true, false);
            mirror.State.Should().Be(MirrorState.Off);
        });
    }

    [Fact]
    public void Camera_mirror_setting_defaults_to_off_and_persists()
    {
        var dir = Directory.CreateTempSubdirectory("irisaura-settings-").FullName;
        try
        {
            var store = new IslandSettingsStore(Path.Combine(dir, "island-settings.json"));
            store.Load().CameraMirror.Should().BeFalse();
            store.Save(store.Load() with { CameraMirror = true });
            store.Load().CameraMirror.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- device banner

    [Fact]
    public void Device_view_fills_the_ring_to_the_battery_level()
    {
        var spec = BluetoothActivities.Connected("Desk Headset", DeviceCategory.Audio, 80);
        var manager = new ActivityManager(new ManualTimeProvider());
        manager.Start("bluetooth", spec);
        var view = new DeviceActivityView(manager.Find(BluetoothActivities.ActivityId)!);

        view.Name.Should().Be("Desk Headset");
        view.Glyph.Should().Be("Aura.Glyph.Headphones");
        view.HasBattery.Should().BeTrue();
        view.BatteryFraction.Should().BeApproximately(0.8, 1e-9);
        view.Status.Should().Be("Connected · 80%");

        manager.Start("bluetooth", BluetoothActivities.Connected("Desk Headset", DeviceCategory.Audio, null));
        view.Apply(manager.Find(BluetoothActivities.ActivityId)!);
        view.HasBattery.Should().BeFalse();
        view.Status.Should().Be("Connected");
        view.BatteryFraction.Should().Be(1, "a full faint ring when the level is unknown");
    }

    private sealed class RecordingView : IIslandActivityView
    {
        public bool Visible { get; private set; }

        public string AccessibleDescription => "test";

        public ExpandedKind? ExpandedKind => Shell.ExpandedKind.Generic;

        public double ExpandedHeight => 140;

        public void SetVisible(bool visible) => Visible = visible;
    }
}
