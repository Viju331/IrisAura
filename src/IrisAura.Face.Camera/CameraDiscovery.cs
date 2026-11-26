using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace IrisAura.Face.Camera;

/// <summary>Finds cameras and their RGB / IR / depth sources (BLUEPRINT §3.4, §7.1 <c>ICameraDiscovery</c>).</summary>
public interface ICameraDiscovery
{
    Task<IReadOnlyList<CameraInfo>> EnumerateAsync();
}

/// <summary>
/// <c>MediaFrameSourceGroup.FindAllAsync()</c> → one <see cref="CameraInfo"/> per group, with each source's kind, stream
/// type and formats, the USB VID/PID from the device symbolic link, the enclosure panel (built-in cameras report Front/
/// Back) and a virtual-camera flag. Read-only; opens no camera.
/// </summary>
public sealed class CameraDiscovery : ICameraDiscovery
{
    public async Task<IReadOnlyList<CameraInfo>> EnumerateAsync()
    {
        var cameras = new List<CameraInfo>();
        foreach (var group in await MediaFrameSourceGroup.FindAllAsync())
        {
            var sources = new List<CameraSourceInfo>();
            string? link = null;
            string? panel = null;
            foreach (var info in group.SourceInfos)
            {
                if (ToKind(info.SourceKind) is not { } kind)
                {
                    continue;
                }

                link ??= info.DeviceInformation?.Id;
                panel ??= info.DeviceInformation?.EnclosureLocation?.Panel.ToString();
                sources.Add(new CameraSourceInfo(info.Id, kind, info.MediaStreamType.ToString()));
            }

            if (sources.Count == 0)
            {
                continue;
            }

            cameras.Add(new CameraInfo(
                group.Id,
                group.DisplayName,
                sources,
                CameraClassifier.VidPid(link),
                panel ?? "Unknown",
                CameraClassifier.IsVirtual(group.DisplayName, link)));
        }

        return cameras;
    }

    internal static CameraKind? ToKind(MediaFrameSourceKind kind) => kind switch
    {
        MediaFrameSourceKind.Color => CameraKind.Color,
        MediaFrameSourceKind.Infrared => CameraKind.Infrared,
        MediaFrameSourceKind.Depth => CameraKind.Depth,
        _ => null,
    };
}

/// <summary>
/// Tries to read frames from one source of a camera (Phase 0B evidence). Frames are inspected for size, format and an
/// aggregate mean level, then released: nothing is stored, logged or sent. The camera is released when the probe ends.
/// </summary>
public static class CameraProbe
{
    /// <summary>Mean level below which a frame counts as unlit (IR emitters strobe, so dark IR frames are normal).</summary>
    public const double DarkThreshold = 8;

    public static async Task<ProbeResult> ProbeAsync(
        string groupId,
        CameraKind kind,
        MediaCaptureSharingMode mode,
        TimeSpan duration,
        int maxFrames = 90)
    {
        var modeName = mode == MediaCaptureSharingMode.ExclusiveControl ? "Exclusive" : "SharedReadOnly";
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        var watch = Stopwatch.StartNew();
        var frames = 0;
        var lit = 0;
        double? first = null;
        double? min = null;
        double? max = null;
        int width = 0, height = 0;
        string? subtype = null;
        var gate = new object();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var group = await MediaFrameSourceGroup.FromIdAsync(groupId)
                ?? throw new InvalidOperationException("Camera group not found");
            var source = group.SourceInfos.FirstOrDefault(s => CameraDiscovery.ToKind(s.SourceKind) == kind)
                ?? throw new InvalidOperationException($"No {kind} source");

            capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = mode,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });

            reader = await capture.CreateFrameReaderAsync(capture.FrameSources[source.Id]);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += (sender, _) =>
            {
                using var frame = sender.TryAcquireLatestFrame();
                var video = frame?.VideoMediaFrame;
                if (video is null)
                {
                    return;
                }

                double? level = null;
                var bitmap = video.SoftwareBitmap;
                if (bitmap is not null && kind != CameraKind.Depth)
                {
                    level = MeanOf(bitmap);
                }

                lock (gate)
                {
                    frames++;
                    first ??= watch.Elapsed.TotalMilliseconds;
                    width = (int)video.VideoFormat.Width;
                    height = (int)video.VideoFormat.Height;
                    subtype = video.VideoFormat.MediaFrameFormat.Subtype;
                    if (level is { } l)
                    {
                        min = min is null ? l : Math.Min(min.Value, l);
                        max = max is null ? l : Math.Max(max.Value, l);
                        if (l >= DarkThreshold)
                        {
                            lit++;
                        }
                    }

                    if (frames >= maxFrames)
                    {
                        done.TrySetResult();
                    }
                }
            };

            watch.Restart();
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                return new ProbeResult(kind, modeName, true, 0, null, 0, 0, null, 0, null, null, $"reader {status}");
            }

            await Task.WhenAny(done.Task, Task.Delay(duration));
            await reader.StopAsync();
            lock (gate)
            {
                return new ProbeResult(kind, modeName, true, frames, first is null ? null : Math.Round(first.Value), width, height, subtype, lit,
                    min is null ? null : Math.Round(min.Value, 1), max is null ? null : Math.Round(max.Value, 1), frames == 0 ? "no frames" : null);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new ProbeResult(kind, modeName, false, 0, null, 0, 0, null, 0, null, null, "access denied (camera privacy setting or policy)");
        }
        catch (Exception ex)
        {
            return new ProbeResult(kind, modeName, false, 0, null, 0, 0, null, 0, null, null, $"{ex.GetType().Name} 0x{ex.HResult:X8}");
        }
        finally
        {
            reader?.Dispose();
            capture?.Dispose(); // releases the camera
        }
    }

    /// <summary>Converts to 8-bit grey and samples the mean; aggregate only.</summary>
    private static double? MeanOf(SoftwareBitmap bitmap)
    {
        try
        {
            using var gray = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Gray8);
            var bytes = new byte[gray.PixelWidth * gray.PixelHeight];
            gray.CopyToBuffer(bytes.AsBuffer());
            return CameraClassifier.MeanLevel(bytes);
        }
        catch (Exception)
        {
            return null; // format without a grey conversion
        }
    }
}
