using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using IrisAura.Island.Providers.Devices;
using IrisAura.Island.Providers.Downloads;
using IrisAura.Island.Providers.Hud;
using IrisAura.Island.Providers.Power;
using IrisAura.Island.Providers.Privacy;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace IrisAura.Island.Diagnostics;

/// <summary>
/// <c>IrisAura.Island.exe --system-probe report.json [--nudge-volume] [--nudge-brightness] [--probe-mic]</c>:
/// device evidence for BLUEPRINT §10 Phase 4. Runs the real providers against this machine and records what is
/// available and whether events arrive. Records <b>no device names, app names or file names</b>: only flags,
/// counts, categories and percentages.
/// <list type="bullet">
/// <item><c>--nudge-volume</c>: changes the master volume by 1 % and back, and checks the volume HUD fires.</item>
/// <item><c>--nudge-brightness</c>: changes the panel brightness by 1 % and back, and checks the brightness HUD fires.</item>
/// <item><c>--probe-mic</c>: opens the default microphone for 3 s (audio discarded, never stored) and checks the
///   consent-store monitor sees this process start and stop using it.</item>
/// </list>
/// Downloads are tested in a temporary folder, never the user's Downloads folder.
/// </summary>
internal static class SystemProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string outputPath, bool nudgeVolume, bool nudgeBrightness, bool probeMic)
    {
        var report = new Dictionary<string, object?>
        {
            ["generatedAt"] = DateTimeOffset.Now.ToString("O"),
            ["windowsBuild"] = Environment.OSVersion.Version.ToString(),
            ["power"] = Power(),
            ["brightness"] = await BrightnessAsync(nudgeBrightness),
            ["volume"] = await VolumeAsync(nudgeVolume),
            ["bluetooth"] = await BluetoothAsync(),
            ["privacy"] = await PrivacyAsync(probeMic),
            ["downloads"] = await DownloadsAsync(),
        };

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, JsonOptions));
        return 0;
    }

    private static object Power()
    {
        var reading = PowerProvider.Read();
        return new { reading.HasBattery, reading.PluggedIn, reading.Percent };
    }

    private static async Task<object> BrightnessAsync(bool nudge)
    {
        var current = BrightnessProvider.ReadCurrent();
        if (current is null || !nudge)
        {
            return new { wmiBrightnessAvailable = current is not null, currentPercent = current, nudged = false };
        }

        var sink = new RecordingSink();
        var provider = new BrightnessProvider();
        provider.Start(sink);
        await Task.Delay(1500); // WMI subscription
        var target = current.Value >= 100 ? current.Value - 1 : current.Value + 1;
        var watch = Stopwatch.StartNew();
        SetBrightness(target);
        var received = await sink.WaitForStartAsync(HudActivities.BrightnessId, TimeSpan.FromSeconds(4));
        var latency = watch.ElapsedMilliseconds;
        SetBrightness(current.Value);
        await Task.Delay(1000);
        provider.Stop();
        return new
        {
            wmiBrightnessAvailable = true,
            currentPercent = current,
            nudged = true,
            hudShown = received is not null,
            hudText = received?.Expanded?.Subtitle,
            latencyMs = received is null ? (long?)null : latency,
            unavailableReason = sink.Unavailable,
            restoredPercent = BrightnessProvider.ReadCurrent(),
        };
    }

    private static void SetBrightness(int percent)
    {
        using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
        foreach (var item in searcher.Get())
        {
            using var method = (ManagementObject)item;
            method.InvokeMethod("WmiSetBrightness", [1u, (byte)percent]);
        }
    }

    private static async Task<object> VolumeAsync(bool nudge)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            return new { outputDevice = false };
        }

        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var original = device.AudioEndpointVolume.MasterVolumeLevelScalar;
        if (!nudge)
        {
            return new { outputDevice = true, levelPercent = HudActivities.Percent(original), nudged = false };
        }

        var sink = new RecordingSink();
        var provider = new VolumeProvider();
        provider.Start(sink);
        await Task.Delay(300);
        var target = original >= 0.99f ? original - 0.01f : original + 0.01f;
        var watch = Stopwatch.StartNew();
        device.AudioEndpointVolume.MasterVolumeLevelScalar = target;
        var received = await sink.WaitForStartAsync(HudActivities.VolumeId, TimeSpan.FromSeconds(3));
        var latency = watch.ElapsedMilliseconds;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = original;
        await Task.Delay(500);
        provider.Stop();
        return new
        {
            outputDevice = true,
            levelPercent = HudActivities.Percent(original),
            nudged = true,
            hudShown = received is not null,
            hudText = received?.Expanded?.Subtitle,
            latencyMs = received is null ? (long?)null : latency,
            hudsShownTotal = sink.Started.Count(s => s.Id == HudActivities.VolumeId),
            restored = Math.Abs(device.AudioEndpointVolume.MasterVolumeLevelScalar - original) < 0.001,
        };
    }

    private static async Task<object> BluetoothAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
        {
            return new { adapter = false };
        }

        string[] properties = ["System.Devices.Aep.ContainerId"];
        var classic = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), properties, DeviceInformationKind.AssociationEndpoint);
        var lowEnergy = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected), properties, DeviceInformationKind.AssociationEndpoint);

        var devices = new List<object>();
        var containers = new HashSet<Guid>();
        foreach (var (info, isLe) in classic.Select(i => (i, false)).Concat(lowEnergy.Select(i => (i, true))))
        {
            var container = info.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var c) && c is Guid g ? g : Guid.Empty;
            if (!containers.Add(container))
            {
                continue;
            }

            DeviceCategory category;
            if (isLe)
            {
                using var le = await BluetoothLEDevice.FromIdAsync(info.Id);
                category = le is null ? DeviceCategory.Other : BluetoothActivities.FromAppearance(le.Appearance.RawValue);
            }
            else
            {
                using var bt = await BluetoothDevice.FromIdAsync(info.Id);
                category = bt is null ? DeviceCategory.Other : BluetoothActivities.FromClassOfDevice(bt.ClassOfDevice.RawValue);
            }

            var battery = container == Guid.Empty ? null : await BluetoothProvider.BatteryAsync(container);
            devices.Add(new { transport = isLe ? "LE" : "classic", category = category.ToString(), batteryPercent = battery });
        }

        return new
        {
            adapter = true,
            connectedClassicEndpoints = classic.Count,
            connectedLeEndpoints = lowEnergy.Count,
            connectedDevices = devices,
            note = "connect/disconnect announcements need a device to connect while the Island runs (owner check)",
        };
    }

    private static async Task<object> PrivacyAsync(bool probeMic)
    {
        var snapshot = CapabilityUsageMonitor.ReadSnapshot();
        var result = new Dictionary<string, object?>
        {
            ["consentStoreMicrophoneUsersNow"] = snapshot.Microphone.Count,
            ["consentStoreCameraUsersNow"] = snapshot.Camera.Count,
        };
        if (!probeMic)
        {
            return result;
        }

        using var monitor = new CapabilityUsageMonitor();
        var self = Process.GetCurrentProcess().ProcessName;
        var started = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new Stopwatch();
        monitor.Changed += (_, s) =>
        {
            var usingMic = s.Microphone.Any(u => string.Equals(u.ProcessName, self, StringComparison.OrdinalIgnoreCase));
            (usingMic ? started : stopped).TrySetResult(watch.ElapsedMilliseconds);
        };
        monitor.Acquire();

        try
        {
            var packets = 0;
            string? captureError = null;
            var directlySeen = false;
            using (var enumerator = new MMDeviceEnumerator())
            using (var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            using (var capture = new WasapiRecorderBuilder().WithDevice(microphone).WithSharedMode().Build())
            {
                // Packets are counted only: the audio is never read, copied or stored.
                capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => Interlocked.Increment(ref packets);
                capture.RecordingStopped += (_, e) => captureError = e.Exception?.GetType().Name;
                // A visible window for the duration of the test, so the user sees the microphone is being opened
                // (and Windows can attribute the use to a desktop app with a window).
                var notice = new System.Windows.Window
                {
                    Title = "IrisAura Island — microphone indicator test (audio is not recorded)",
                    Width = 420,
                    Height = 90,
                    WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = "Testing the microphone indicator for a few seconds. Audio is discarded.",
                        Margin = new System.Windows.Thickness(16),
                    },
                };
                notice.Show();
                watch.Start();
                capture.StartRecording();
                var deadline = Stopwatch.StartNew();
                while (!started.Task.IsCompleted && deadline.ElapsedMilliseconds < 4000)
                {
                    await Task.Delay(200);
                    directlySeen |= CapabilityUsageMonitor.ReadSnapshot().Microphone
                        .Any(u => string.Equals(u.ProcessName, self, StringComparison.OrdinalIgnoreCase));
                }

                capture.StopRecording();
                notice.Close();
            }

            var stopAt = watch.ElapsedMilliseconds;
            await Task.WhenAny(stopped.Task, Task.Delay(3000));
            result["micOpened"] = true;
            result["capturePackets"] = packets;
            result["captureError"] = captureError;
            result["consentStoreRecordedIt"] = directlySeen;
            result["monitorSawStartMs"] = started.Task.IsCompleted ? started.Task.Result : null;
            result["monitorSawStopMs"] = stopped.Task.IsCompleted ? stopped.Task.Result - stopAt : null;
        }
        catch (Exception ex)
        {
            result["micOpened"] = false;
            result["error"] = ex.GetType().Name;
        }
        finally
        {
            monitor.Release();
        }

        return result;
    }

    private static async Task<object> DownloadsAsync()
    {
        var real = DownloadsProvider.DefaultFolder();
        var temp = Directory.CreateTempSubdirectory("irisaura-download-probe-").FullName;
        var sink = new RecordingSink();
        var provider = new DownloadsProvider(() => temp);
        try
        {
            provider.Start(sink);

            // Chromium-style sequence: placeholder → named temp file growing → final name.
            var placeholder = Path.Combine(temp, "Unconfirmed 424242.crdownload");
            await File.WriteAllBytesAsync(placeholder, new byte[1024]);
            await Task.Delay(300);
            var named = Path.Combine(temp, "probe.bin.crdownload");
            File.Move(placeholder, named);
            await using (var stream = new FileStream(named, FileMode.Append))
            {
                await stream.WriteAsync(new byte[2 * 1024 * 1024]);
            }

            await Task.Delay(1600); // one size poll
            File.Move(named, Path.Combine(temp, "probe.bin"));
            await Task.Delay(500);

            // Firefox-style: final file + .part, .part deleted when done.
            await File.WriteAllBytesAsync(Path.Combine(temp, "second.bin"), []);
            var part = Path.Combine(temp, "second.bin.part");
            await File.WriteAllBytesAsync(part, new byte[4096]);
            await Task.Delay(300);
            await File.WriteAllBytesAsync(Path.Combine(temp, "second.bin"), new byte[4096]);
            File.Delete(part);
            await Task.Delay(500);

            // Cancelled: temp file deleted without a final file.
            var cancelled = Path.Combine(temp, "third.bin.crdownload");
            await File.WriteAllBytesAsync(cancelled, new byte[10]);
            await Task.Delay(300);
            File.Delete(cancelled);
            await Task.Delay(500);
        }
        finally
        {
            provider.Stop();
            Directory.Delete(temp, recursive: true);
        }

        return new
        {
            downloadsFolderFound = real is not null && Directory.Exists(real),
            simulatedInTempFolder = true,
            activityStarts = sink.Started.Count,
            sizesReported = sink.Started.Select(s => s.Compact.TrailingText).Distinct().ToList(),
            ended = sink.Ended.Count,
            endedWithDone = sink.EndedWithText.Count(t => t == "Done ✓"),
        };
    }

    /// <summary>Collects what a provider publishes (no host, no UI).</summary>
    private sealed class RecordingSink : IActivitySink
    {
        private readonly object _gate = new();
        private readonly List<ActivitySpec> _started = [];
        private readonly List<string?> _endedText = [];

        public List<ActivitySpec> Started
        {
            get
            {
                lock (_gate)
                {
                    return [.. _started];
                }
            }
        }

        public int EndedCount { get; private set; }

        public List<string> Ended
        {
            get
            {
                lock (_gate)
                {
                    return [.. Enumerable.Repeat("ended", EndedCount)];
                }
            }
        }

        public List<string?> EndedWithText
        {
            get
            {
                lock (_gate)
                {
                    return [.. _endedText];
                }
            }
        }

        public string? Unavailable { get; private set; }

        public void Start(ActivitySpec spec)
        {
            lock (_gate)
            {
                _started.Add(spec);
            }
        }

        public void Update(ActivityUpdate update)
        {
        }

        public void End(string id, string? finalText = null, TimeSpan? dismissAfter = null)
        {
            lock (_gate)
            {
                EndedCount++;
                _endedText.Add(finalText);
            }
        }

        public void ReportFailure(Exception error) => Unavailable = "failed: " + error.GetType().Name;

        public void ReportUnavailable(string reason) => Unavailable = reason;

        public async Task<ActivitySpec?> WaitForStartAsync(string id, TimeSpan timeout)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout)
            {
                var found = Started.FirstOrDefault(s => s.Id == id);
                if (found is not null)
                {
                    return found;
                }

                await Task.Delay(25);
            }

            return null;
        }
    }
}
