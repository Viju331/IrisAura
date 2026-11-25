using System.IO;
using FluentAssertions;
using IrisAura.Island.Engine;
using IrisAura.Island.Providers.Audio;
using IrisAura.Island.Providers.Calls;
using IrisAura.Island.Providers.Devices;
using IrisAura.Island.Providers.Downloads;
using IrisAura.Island.Providers.Hud;
using IrisAura.Island.Providers.Power;
using IrisAura.Island.Providers.Privacy;
using IrisAura.Island.Providers.Timers;

namespace IrisAura.Island.Tests;

/// <summary>System providers (BLUEPRINT §8.6.3–§8.6.11, §10 Phase 4): the pure logic behind each provider.</summary>
public sealed class SystemProviderTests
{
    // ---------------------------------------------------------------- volume / brightness HUD

    [Fact]
    public void First_volume_reading_and_identical_resends_are_not_changes()
    {
        HudActivities.IsVolumeChange(null, 0.4, false).Should().BeFalse("subscribing (or a device switch) is not a user change");
        HudActivities.IsVolumeChange((0.40, false), 0.401, false).Should().BeFalse("same whole percent");
        HudActivities.IsVolumeChange((0.40, false), 0.41, false).Should().BeTrue();
        HudActivities.IsVolumeChange((0.40, false), 0.40, true).Should().BeTrue("mute toggled");
    }

    [Fact]
    public void Volume_hud_is_a_transient_with_level_and_mute_glyph()
    {
        var spec = HudActivities.Volume(0.43, muted: false);
        spec.Kind.Should().Be(ActivityKind.Volume);
        spec.Compact.LeadingGlyph.Should().Be("Aura.Glyph.Volume");
        spec.Compact.TrailingProgress.Should().BeApproximately(0.43, 1e-9);
        spec.Expanded!.Subtitle.Should().Be("43%");
        ActivityDefaults.IsTransient(spec.Kind).Should().BeTrue();
        ActivityDefaults.Ttl(spec.Kind).Should().Be(TimeSpan.FromSeconds(1.5));

        var muted = HudActivities.Volume(0.43, muted: true);
        muted.Compact.LeadingGlyph.Should().Be("Aura.Glyph.VolumeMute");
        muted.Expanded!.Subtitle.Should().Be("Muted");
        muted.Compact.TrailingProgress.Should().Be(0);
    }

    [Fact]
    public void Brightness_hud_clamps_to_0_100()
    {
        HudActivities.Brightness(70).Expanded!.Subtitle.Should().Be("70%");
        HudActivities.Brightness(140).Compact.TrailingProgress.Should().Be(1);
        HudActivities.Brightness(-5).Expanded!.Subtitle.Should().Be("0%");
    }

    // ---------------------------------------------------------------- charging and low battery

    [Fact]
    public void Plugging_in_shows_the_charging_hud_but_startup_does_not()
    {
        var monitor = new BatteryMonitor();
        monitor.Observe(new BatteryReading(true, PluggedIn: false, 64)).Should().BeNull("no banner at startup");

        var spec = monitor.Observe(new BatteryReading(true, PluggedIn: true, 64));

        spec.Should().NotBeNull();
        spec!.Id.Should().Be(BatteryMonitor.ChargingId);
        spec.Compact.Tone.Should().Be(ActivityTone.Success);
        spec.Compact.LeadingGlyph.Should().Be("Aura.Glyph.Bolt");
        spec.Expanded!.Subtitle.Should().Be("64%");
        monitor.Observe(new BatteryReading(true, true, 65)).Should().BeNull("charge updates while plugged in are not shown");
        monitor.Observe(new BatteryReading(true, false, 65)).Should().BeNull("unplugging is not announced");
    }

    [Fact]
    public void Low_battery_warns_at_20_and_10_percent_once_per_discharge_cycle()
    {
        var monitor = new BatteryMonitor();
        monitor.Observe(new BatteryReading(true, false, 30));

        monitor.Observe(new BatteryReading(true, false, 21)).Should().BeNull();
        var at20 = monitor.Observe(new BatteryReading(true, false, 20));
        at20!.Id.Should().Be(BatteryMonitor.LowBatteryId);
        at20.Compact.Tone.Should().Be(ActivityTone.Alert);
        monitor.Observe(new BatteryReading(true, false, 19)).Should().BeNull("20 % already shown this cycle");
        monitor.Observe(new BatteryReading(true, false, 10))!.Expanded!.Subtitle.Should().Be("10% remaining");
        monitor.Observe(new BatteryReading(true, false, 9)).Should().BeNull();

        monitor.Observe(new BatteryReading(true, true, 9)).Should().NotBeNull("plugged in: charging HUD, new cycle");
        monitor.Observe(new BatteryReading(true, false, 25)).Should().BeNull();
        monitor.Observe(new BatteryReading(true, false, 20)).Should().NotBeNull("new discharge cycle");
    }

    [Fact]
    public void Dropping_past_both_thresholds_shows_one_banner_and_startup_below_a_threshold_skips_it()
    {
        var monitor = new BatteryMonitor();
        monitor.Observe(new BatteryReading(true, false, 25));
        monitor.Observe(new BatteryReading(true, false, 8))!.Expanded!.Subtitle.Should().Be("8% remaining");
        monitor.Observe(new BatteryReading(true, false, 7)).Should().BeNull();

        var started = new BatteryMonitor();
        started.Observe(new BatteryReading(true, false, 15)).Should().BeNull();
        started.Observe(new BatteryReading(true, false, 14)).Should().BeNull("20 % had passed before the Island started");
        started.Observe(new BatteryReading(true, false, 10)).Should().NotBeNull();
    }

    [Fact]
    public void No_battery_never_shows_anything()
    {
        var monitor = new BatteryMonitor();
        monitor.Observe(new BatteryReading(false, true, 0)).Should().BeNull();
        monitor.Observe(new BatteryReading(false, false, 0)).Should().BeNull();
        monitor.Observe(new BatteryReading(false, true, 0)).Should().BeNull();
    }

    // ---------------------------------------------------------------- Bluetooth

    [Theory]
    [InlineData(0x240404u, DeviceCategory.Audio)] // wearable headset
    [InlineData(0x200418u, DeviceCategory.Audio)] // headphones
    [InlineData(0x000540u, DeviceCategory.Keyboard)]
    [InlineData(0x000580u, DeviceCategory.Mouse)]
    [InlineData(0x0005C0u, DeviceCategory.Keyboard)] // combo keyboard/pointer
    [InlineData(0x5A020Cu, DeviceCategory.Phone)]
    [InlineData(0x000104u, DeviceCategory.Other)] // computer
    public void Classic_class_of_device_maps_to_a_category(uint raw, DeviceCategory expected) =>
        BluetoothActivities.FromClassOfDevice(raw).Should().Be(expected);

    [Theory]
    [InlineData((ushort)0x03C1, DeviceCategory.Keyboard)]
    [InlineData((ushort)0x03C2, DeviceCategory.Mouse)]
    [InlineData((ushort)0x0941, DeviceCategory.Audio)] // earbud
    [InlineData((ushort)0x0943, DeviceCategory.Audio)] // headphones
    [InlineData((ushort)0x0841, DeviceCategory.Audio)] // speaker
    [InlineData((ushort)0x0040, DeviceCategory.Phone)]
    [InlineData((ushort)0x0080, DeviceCategory.Other)] // computer
    [InlineData((ushort)0x0000, DeviceCategory.Other)]
    public void Le_appearance_maps_to_a_category(ushort raw, DeviceCategory expected) =>
        BluetoothActivities.FromAppearance(raw).Should().Be(expected);

    [Fact]
    public void Connected_activity_shows_battery_only_when_reported()
    {
        var plain = BluetoothActivities.Connected("Desk Headset", DeviceCategory.Audio, null);
        plain.Kind.Should().Be(ActivityKind.Bluetooth);
        plain.Compact.LeadingGlyph.Should().Be("Aura.Glyph.Headphones");
        plain.Compact.TrailingText.Should().Be("Connected");
        plain.Expanded!.Subtitle.Should().Be("Connected");

        var withBattery = BluetoothActivities.Connected("Desk Headset", DeviceCategory.Audio, 90);
        withBattery.Compact.TrailingText.Should().Be("90%");
        withBattery.Expanded!.Subtitle.Should().Be("Connected · 90% battery");
        withBattery.AccessibleText.Should().Contain("battery 90 percent");

        BluetoothActivities.Connected("  ", DeviceCategory.Other, null).Expanded!.Title.Should().Be("Bluetooth device");
        BluetoothActivities.Connected("Mouse", DeviceCategory.Mouse, null).Compact.LeadingGlyph.Should().Be("Aura.Glyph.Mouse");
    }

    [Fact]
    public void A_device_with_classic_and_le_endpoints_is_announced_once()
    {
        var dedupe = new ConnectionDeduplicator(TimeSpan.FromSeconds(10));
        var t0 = DateTimeOffset.UnixEpoch;

        dedupe.ShouldAnnounce("container-a", t0).Should().BeTrue();
        dedupe.ShouldAnnounce("CONTAINER-A", t0.AddSeconds(2)).Should().BeFalse("LE endpoint of the same device");
        dedupe.ShouldAnnounce("container-b", t0.AddSeconds(2)).Should().BeTrue();
        dedupe.ShouldAnnounce("container-a", t0.AddSeconds(15)).Should().BeTrue("reconnected later");
    }

    // ---------------------------------------------------------------- microphone / camera

    [Theory]
    [InlineData(132_000_000_000_000_000L, 0L, true)]
    [InlineData(132_000_000_000_000_000L, 132_000_000_000_000_001L, false)]
    [InlineData(0L, 0L, false)]
    public void Device_is_in_use_while_started_and_not_stopped(long start, long stop, bool expected) =>
        CapabilityUsage.IsInUse(start, stop).Should().Be(expected);

    [Fact]
    public void Consent_store_keys_give_readable_app_names()
    {
        var zoom = CapabilityUsage.UserFromKey(@"C:#Program Files#Zoom#bin#Zoom.exe", nonPackaged: true);
        zoom.DisplayName.Should().Be("Zoom");
        zoom.ProcessName.Should().Be("Zoom");
        zoom.PackageFamilyName.Should().BeNull();

        var camera = CapabilityUsage.UserFromKey("Microsoft.WindowsCamera_8wekyb3d8bbwe", nonPackaged: false);
        camera.DisplayName.Should().Be("WindowsCamera");
        camera.PackageFamilyName.Should().Be("Microsoft.WindowsCamera_8wekyb3d8bbwe");
        camera.ProcessName.Should().BeNull();

        CapabilityUsage.UserFromKey("MSTeams_8wekyb3d8bbwe", nonPackaged: false).DisplayName.Should().Be("MSTeams");
    }

    [Fact]
    public void Capture_sessions_add_microphone_users_the_consent_store_missed()
    {
        var consent = new[] { CapabilityUsage.UserFromKey(@"C:#Program Files#Zoom#bin#Zoom.exe", nonPackaged: true) };

        var merged = CapabilityUsage.MergeCapture(consent, ["Zoom", "ms-teams"]);

        merged.Should().HaveCount(2, "Zoom is already listed by the consent store");
        merged.Should().ContainSingle(u => u.ProcessName == "ms-teams" && u.Key == "capture:ms-teams");
        CapabilityUsage.MergeCapture([], []).Should().BeEmpty();
    }

    [Fact]
    public void Privacy_provider_starts_and_ends_indicators_as_apps_come_and_go()
    {
        var source = new FakeCapabilitySource();
        var sink = new RecordingSink();
        var provider = new PrivacyProvider(source);
        provider.Start(sink);
        source.Acquired.Should().Be(1);
        sink.Started.Should().BeEmpty();

        source.Raise(new CapabilitySnapshot([User("Zoom")], []));
        sink.Started.Should().ContainSingle(s => s.Id == PrivacyProvider.MicrophoneId && s.Kind == ActivityKind.PrivacyIndicator);
        sink.Started[0].Expanded!.Subtitle.Should().Be("Zoom");

        source.Raise(new CapabilitySnapshot([User("Zoom")], [User("WindowsCamera")]));
        sink.Started.Should().HaveCount(2, "microphone unchanged; camera added");
        sink.Started[1].Id.Should().Be(PrivacyProvider.CameraId);

        source.Raise(CapabilitySnapshot.Empty);
        sink.Ended.Should().BeEquivalentTo([PrivacyProvider.MicrophoneId, PrivacyProvider.CameraId]);

        provider.Stop();
        source.Acquired.Should().Be(0);
    }

    [Fact]
    public void Privacy_indicators_never_take_the_pill()
    {
        var time = new ManualTimeProvider();
        var manager = new ActivityManager(time);
        manager.Start("privacy", PrivacyProvider.Indicator(Capability.Microphone, [User("Zoom")])!);
        manager.Start("media", Specs.Persistent("media", ActivityKind.Media));

        var layout = PriorityArbiter.Arrange(new ArbiterInput(manager.Activities, time.GetUtcNow()));

        layout.Main!.Id.Should().Be("media");
        layout.Bubble.Should().BeNull();
        layout.PrivacyIndicators.Should().ContainSingle(a => a.Id == PrivacyProvider.MicrophoneId);
    }

    // ---------------------------------------------------------------- call detection (beta)

    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Microphone_use_alone_is_never_a_call()
    {
        var detector = new CallDetector(CallApp.Defaults);
        var mic = new CapabilitySnapshot([User("Zoom")], []);

        detector.Observe(mic, _ => false, T0).Should().Be(CallChange.None, "no audio output: not corroborated");
        detector.ActiveApp.Should().BeNull();
        detector.NeedsTicks(mic).Should().BeTrue("waits for corroboration");

        var notACallApp = new CapabilitySnapshot([User("Audacity")], []);
        detector.Observe(notACallApp, _ => true, T0).Should().Be(CallChange.None);
        detector.NeedsTicks(notACallApp).Should().BeFalse();
    }

    [Fact]
    public void Call_starts_with_corroboration_and_ends_3_s_after_the_microphone_is_released()
    {
        var detector = new CallDetector(CallApp.Defaults);
        var inCall = new CapabilitySnapshot([User("ms-teams")], []);

        detector.Observe(inCall, app => app.DisplayName == "Microsoft Teams", T0).Should().Be(CallChange.Started);
        detector.ActiveApp!.DisplayName.Should().Be("Microsoft Teams");
        detector.Observe(inCall, _ => false, T0.AddSeconds(30)).Should().Be(CallChange.Tick, "corroboration is needed only to start");

        detector.Observe(CapabilitySnapshot.Empty, _ => false, T0.AddSeconds(31)).Should().Be(CallChange.Tick);
        detector.Observe(CapabilitySnapshot.Empty, _ => false, T0.AddSeconds(33)).Should().Be(CallChange.Tick, "within the 3 s grace (mute toggles)");
        detector.Observe(inCall, _ => false, T0.AddSeconds(33.5)).Should().Be(CallChange.Tick, "microphone back: same call");
        detector.Observe(CapabilitySnapshot.Empty, _ => false, T0.AddSeconds(34)).Should().Be(CallChange.Tick);
        detector.Observe(CapabilitySnapshot.Empty, _ => false, T0.AddSeconds(37.5)).Should().Be(CallChange.Ended);
        detector.ActiveApp.Should().BeNull();
    }

    [Fact]
    public void Packaged_call_apps_match_by_package_family()
    {
        var teams = CapabilityUsage.UserFromKey("MSTeams_8wekyb3d8bbwe", nonPackaged: false);
        CallApp.Defaults.Single(a => a.DisplayName == "Microsoft Teams").Matches(teams).Should().BeTrue();
        CallApp.Defaults.Any(a => a.Matches(CapabilityUsage.UserFromKey("Microsoft.WindowsCamera_8wekyb3d8bbwe", false))).Should().BeFalse();
    }

    [Fact]
    public void Call_activity_is_labelled_beta_and_shows_elapsed_time()
    {
        var spec = CallDetector.Activity(CallApp.Defaults[0], TimeSpan.FromSeconds(75));
        spec.Kind.Should().Be(ActivityKind.Call);
        spec.Compact.TrailingText.Should().Be("01:15");
        spec.Expanded!.Subtitle.Should().Contain("beta");
        spec.SourceAppId.Should().Be("ms-teams");
        CallDetector.FormatElapsed(TimeSpan.FromMinutes(61)).Should().Be("1:01:00");
    }

    [Fact]
    public void Call_provider_publishes_start_and_end()
    {
        var time = new ManualTimeProvider();
        var source = new FakeCapabilitySource();
        var audio = new FakeAudio { Playing = true };
        var sink = new RecordingSink();
        var provider = new CallProvider(source, audio, time);
        provider.Start(sink);

        source.Raise(new CapabilitySnapshot([User("Zoom")], []));
        sink.Started.Should().ContainSingle(s => s.Id == CallProvider.ActivityId);

        source.Raise(CapabilitySnapshot.Empty);
        time.Advance(TimeSpan.FromSeconds(4));
        provider.Evaluate();
        sink.Ended.Should().Contain(CallProvider.ActivityId);
        provider.Stop();
    }

    // ---------------------------------------------------------------- timers

    [Theory]
    [InlineData(0.2, "00:01")]
    [InlineData(59.0, "00:59")]
    [InlineData(59.4, "01:00")]
    [InlineData(300.0, "05:00")]
    [InlineData(3600.0, "1:00:00")]
    [InlineData(0.0, "00:00")]
    public void Timer_text_rounds_up_to_whole_seconds(double seconds, string expected) =>
        TimerState.Format(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Fact]
    public void Timer_pauses_resumes_and_gains_a_minute()
    {
        var state = TimerState.StartNew(TimeSpan.FromMinutes(5), T0);
        state.Remaining(T0.AddMinutes(1)).Should().Be(TimeSpan.FromMinutes(4));

        var paused = state.Pause(T0.AddMinutes(1));
        paused.IsPaused.Should().BeTrue();
        paused.Remaining(T0.AddMinutes(10)).Should().Be(TimeSpan.FromMinutes(4), "time does not run while paused");

        var resumed = paused.Resume(T0.AddMinutes(10));
        resumed.Remaining(T0.AddMinutes(11)).Should().Be(TimeSpan.FromMinutes(3));

        var longer = resumed.AddTime(TimeSpan.FromMinutes(1));
        longer.Duration.Should().Be(TimeSpan.FromMinutes(6));
        longer.Remaining(T0.AddMinutes(11)).Should().Be(TimeSpan.FromMinutes(4));
        paused.AddTime(TimeSpan.FromMinutes(1)).Remaining(T0).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Timer_is_raised_to_85_in_the_last_10_s_then_finishes_with_a_banner()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var sink = new RecordingSink();
        var provider = new TimerProvider(new TimerStore(dir.File("timer.json")), time);
        var finished = 0;
        provider.Finished += (_, _) => finished++;
        provider.Start(sink);

        provider.StartTimer(TimeSpan.FromSeconds(30));
        sink.Started.Last().Priority.Should().Be(ActivityDefaults.Priority(ActivityKind.Timer));
        sink.Started.Last().View.Should().BeOfType<TimerState>();

        time.Advance(TimeSpan.FromSeconds(21));
        provider.Wake();
        sink.Started.Last().Priority.Should().Be(TimerProvider.FinishingPriority);

        time.Advance(TimeSpan.FromSeconds(10));
        provider.Wake();
        sink.Ended.Should().Contain(TimerProvider.ActivityId);
        sink.Started.Last().Id.Should().Be(TimerProvider.DoneId);
        sink.Started.Last().IsTransient.Should().BeTrue();
        sink.Started.Last().Compact.Tone.Should().Be(ActivityTone.Alert);
        finished.Should().Be(1);
        File.Exists(dir.File("timer.json")).Should().BeFalse("a finished timer is not restored");
        provider.Stop();
    }

    [Fact]
    public void Timer_survives_a_restart_and_one_that_ended_meanwhile_shows_once_without_sound()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var store = new TimerStore(dir.File("timer.json"));

        var first = new TimerProvider(store, time);
        first.Start(new RecordingSink());
        first.StartTimer(TimeSpan.FromMinutes(5));
        first.TogglePause();
        first.Stop();

        time.Advance(TimeSpan.FromHours(1));
        var second = new TimerProvider(store, time);
        var sink = new RecordingSink();
        second.Start(sink);
        second.Current!.IsPaused.Should().BeTrue();
        second.Current.Remaining(time.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(5));
        sink.Started.Should().ContainSingle(s => s.Id == TimerProvider.ActivityId);

        second.TogglePause(); // running again, 5 min left
        second.Stop();
        time.Advance(TimeSpan.FromMinutes(6));

        var third = new TimerProvider(store, time);
        var finished = 0;
        third.Finished += (_, _) => finished++;
        var thirdSink = new RecordingSink();
        third.Start(thirdSink);
        thirdSink.Started.Should().ContainSingle(s => s.Id == TimerProvider.DoneId);
        thirdSink.Started[0].Expanded!.Subtitle.Should().Contain("closed");
        finished.Should().Be(0, "no sound for a timer that ended while the Island was closed");
        third.Current.Should().BeNull();
        third.Stop();
    }

    [Fact]
    public void Timer_actions_toggle_add_and_cancel()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var sink = new RecordingSink();
        var provider = new TimerProvider(new TimerStore(dir.File("timer.json")), time);
        provider.Start(sink);
        provider.StartOrToggle();
        provider.Current!.Duration.Should().Be(TimerProvider.DefaultDuration);

        provider.OnAction(TimerProvider.ActivityId, ActivityActions.AddMinute).Should().BeTrue();
        provider.Current!.Duration.Should().Be(TimeSpan.FromMinutes(6));
        provider.StartOrToggle();
        provider.Current!.IsPaused.Should().BeTrue("the shortcut pauses a running timer");
        provider.OnAction(TimerProvider.ActivityId, ActivityActions.Toggle).Should().BeTrue();
        provider.Current!.IsPaused.Should().BeFalse();
        provider.OnAction(TimerProvider.ActivityId, ActivityActions.Open).Should().BeFalse("the Island's own timer has no app");

        provider.OnAction(TimerProvider.ActivityId, ActivityActions.Cancel).Should().BeTrue();
        provider.Current.Should().BeNull();
        sink.Ended.Should().Contain(TimerProvider.ActivityId);
        provider.Stop();
    }

    // ---------------------------------------------------------------- downloads

    [Fact]
    public void Chromium_download_is_tracked_through_its_renames_to_done()
    {
        var tracker = new DownloadTracker();
        var started = tracker.OnCreated(@"C:\D\Unconfirmed 123456.crdownload");
        started!.Kind.Should().Be(DownloadEventKind.Started);
        started.FileName.Should().Be("Download");

        var named = tracker.OnRenamed(@"C:\D\Unconfirmed 123456.crdownload", @"C:\D\report.pdf.crdownload", 1000);
        named!.Kind.Should().Be(DownloadEventKind.Progress);
        named.Id.Should().Be(started.Id);
        named.FileName.Should().Be("report.pdf");

        tracker.OnSize(@"C:\D\report.pdf.crdownload", 5_000_000)!.Bytes.Should().Be(5_000_000);
        tracker.OnSize(@"C:\D\report.pdf.crdownload", 5_000_000).Should().BeNull("no change");

        var done = tracker.OnRenamed(@"C:\D\report.pdf.crdownload", @"C:\D\report.pdf");
        done!.Kind.Should().Be(DownloadEventKind.Completed);
        done.Id.Should().Be(started.Id);
        tracker.Count.Should().Be(0);
    }

    [Fact]
    public void Firefox_part_file_completes_when_deleted_beside_the_final_file_and_cancels_otherwise()
    {
        var tracker = new DownloadTracker();
        tracker.OnCreated(@"C:\D\setup.exe.part")!.FileName.Should().Be("setup.exe");
        tracker.OnDeleted(@"C:\D\setup.exe.part", finalFileExists: true)!.Kind.Should().Be(DownloadEventKind.Completed);

        tracker.OnCreated(@"C:\D\video.mp4.part");
        tracker.OnDeleted(@"C:\D\video.mp4.part", finalFileExists: false)!.Kind.Should().Be(DownloadEventKind.Cancelled);

        tracker.OnCreated(@"C:\D\notes.txt").Should().BeNull("not a temporary download file");
        tracker.OnDeleted(@"C:\D\untracked.part", true).Should().BeNull();
    }

    [Fact]
    public void At_most_five_downloads_are_tracked()
    {
        var tracker = new DownloadTracker();
        for (var i = 0; i < DownloadTracker.MaxTracked; i++)
        {
            tracker.OnCreated($@"C:\D\file{i}.crdownload").Should().NotBeNull();
        }

        tracker.OnCreated(@"C:\D\one-too-many.crdownload").Should().BeNull();
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5_242_880L, "5.0 MB")]
    [InlineData(3_221_225_472L, "3.00 GB")]
    public void Sizes_are_formatted_for_the_pill(long bytes, string expected) =>
        DownloadTracker.FormatBytes(bytes).Should().Be(expected);

    [Fact]
    public void Download_activity_says_the_total_is_unknown()
    {
        var spec = DownloadTracker.Activity(new DownloadEvent(DownloadEventKind.Progress, "download:1", "report.pdf", 1_048_576));
        spec.Kind.Should().Be(ActivityKind.Download);
        spec.Compact.TrailingText.Should().Be("1.0 MB");
        spec.Expanded!.Subtitle.Should().Contain("best-effort");
        spec.Expanded.Progress.Should().BeNull("there is no true percentage");
    }

    // ---------------------------------------------------------------- settings

    [Fact]
    public void Provider_settings_round_trip_and_survive_a_corrupt_file()
    {
        using var dir = new TempDir();
        var store = new IslandSettingsStore(dir.File("island-settings.json"));
        store.Load().IsEnabled("calls", defaultValue: false).Should().BeFalse();

        store.Save(new IslandSettings().WithProvider("calls", true).WithProvider("volume", false));
        var loaded = store.Load();
        loaded.IsEnabled("calls", false).Should().BeTrue();
        loaded.IsEnabled("volume", true).Should().BeFalse();
        loaded.IsEnabled("power", true).Should().BeTrue("unlisted providers use their default");

        File.WriteAllText(dir.File("island-settings.json"), "{ not json");
        store.Load().Providers.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static CapabilityUser User(string process) => new($"C:#Apps#{process}.exe", process, process, null);

    private sealed class RecordingSink : IActivitySink
    {
        public List<ActivitySpec> Started { get; } = [];

        public List<ActivityUpdate> Updates { get; } = [];

        public List<string> Ended { get; } = [];

        public string? Unavailable { get; private set; }

        public void Start(ActivitySpec spec) => Started.Add(spec);

        public void Update(ActivityUpdate update) => Updates.Add(update);

        public void End(string id, string? finalText = null, TimeSpan? dismissAfter = null) => Ended.Add(id);

        public void ReportFailure(Exception error) => throw error;

        public void ReportUnavailable(string reason) => Unavailable = reason;
    }

    private sealed class FakeCapabilitySource : ICapabilityUsageSource
    {
        public event EventHandler<CapabilitySnapshot>? Changed;

        public CapabilitySnapshot Current { get; private set; } = CapabilitySnapshot.Empty;

        public int Acquired { get; private set; }

        public void Acquire() => Acquired++;

        public void Release() => Acquired--;

        public void Raise(CapabilitySnapshot snapshot)
        {
            Current = snapshot;
            Changed?.Invoke(this, snapshot);
        }
    }

    private sealed class FakeAudio : IAudioSessionActivity
    {
        public bool Playing { get; set; }

        public bool IsPlaying(IReadOnlyList<string> processNames) => Playing;
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _path = Directory.CreateTempSubdirectory("irisaura-tests-").FullName;

        public string File(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
