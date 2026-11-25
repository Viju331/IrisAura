using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace IrisAura.Island.Views.Panel;

/// <summary>What the mirror widget shows.</summary>
public enum MirrorState
{
    /// <summary>Not switched on by the user (default).</summary>
    Off,

    /// <summary>Switched on; the camera starts when the panel's Island tab is visible.</summary>
    Ready,

    Starting,

    Live,

    /// <summary>No camera, camera access off in Windows, or the camera failed.</summary>
    Unavailable,
}

/// <summary>
/// Camera mirror (BLUEPRINT §8.13A): a round, mirrored live preview in the panel.
/// <list type="bullet">
/// <item><b>Opt-in:</b> off by default; the user switches it on in the panel (persisted in island-settings.json).</item>
/// <item>The camera runs <b>only while the panel's Island tab is showing</b> and is released (MediaCapture disposed)
///   as soon as the panel closes or another tab is chosen.</item>
/// <item>Opens the camera in <c>SharedReadOnly</c> mode, so it never takes the camera away from a call app.</item>
/// <item>Frames are drawn and dropped: nothing is stored, logged or sent. At most 15 frames per second at 320 × 240.</item>
/// <item>While live, the Island's camera dot shows (<see cref="IsCameraActive"/>), and Windows' own camera light is on.</item>
/// </list>
/// </summary>
public sealed class CameraMirrorView : ObservableObject
{
    private const int FrameWidth = 320;
    private const int FrameHeight = 240;
    private static readonly long MinFrameTicks = Stopwatch.Frequency / 15;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private WriteableBitmap? _bitmap;
    private byte[] _pixels = [];
    private int _pending;
    private long _lastFrame;
    private bool _enabled;
    private bool _wanted;
    private MirrorState _state;
    private string _message = "Off";

    public CameraMirrorView(bool enabled)
    {
        _enabled = enabled;
        _state = enabled ? MirrorState.Ready : MirrorState.Off;
        ToggleCommand = new RelayCommand(() => SetEnabled(!_enabled));
    }

    /// <summary>The user switched the mirror on or off (persist it).</summary>
    public event EventHandler<bool>? EnabledChanged;

    /// <summary>The camera started or stopped (privacy dot).</summary>
    public event EventHandler<bool>? CameraActiveChanged;

    public RelayCommand ToggleCommand { get; }

    public bool IsEnabled => _enabled;

    public MirrorState State => _state;

    public bool IsLive => _state == MirrorState.Live;

    /// <summary>MediaCapture is open (between start and release).</summary>
    public bool IsCameraActive => _capture is not null;

    /// <summary>Frames drawn since the camera last started (self-test).</summary>
    public int FramesShown { get; private set; }

    public ImageSource? Frame => _bitmap;

    public string ToggleText => _enabled ? "Turn off" : "Turn on";

    public string Message => _message;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        SetState(enabled ? MirrorState.Ready : MirrorState.Off, enabled ? "Starting…" : "Off");
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ToggleText));
        EnabledChanged?.Invoke(this, enabled);
        _ = ApplyAsync();
    }

    /// <summary>Whether the panel is showing the mirror; the camera runs only while this is true and the mirror is on.</summary>
    public void SetShown(bool shown)
    {
        if (_wanted == shown)
        {
            return;
        }

        _wanted = shown;
        _ = ApplyAsync();
    }

    /// <summary>Starts or stops the camera to match the switch and visibility (serialised).</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_enabled && _wanted)
            {
                if (_capture is null)
                {
                    await StartAsync();
                }
            }
            else if (_capture is not null)
            {
                await StopAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartAsync()
    {
        FramesShown = 0;
        SetState(MirrorState.Starting, "Starting…");
        try
        {
            var (group, source) = await FindColorSourceAsync();
            if (group is null || source is null)
            {
                SetState(MirrorState.Unavailable, "No camera found");
                return;
            }

            var capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            });
            _capture = capture;
            CameraActiveChanged?.Invoke(this, true);
            OnPropertyChanged(nameof(IsCameraActive));

            var reader = await capture.CreateFrameReaderAsync(
                capture.FrameSources[source.Id],
                MediaEncodingSubtypes.Bgra8,
                new BitmapSize { Width = FrameWidth, Height = FrameHeight });
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;
            _reader = reader;
            FramesShown = 0;
            var status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                await StopAsync();
                SetState(MirrorState.Unavailable, status == MediaFrameReaderStartStatus.DeviceNotAvailable ? "Camera in use" : "Camera unavailable");
                return;
            }

            SetState(MirrorState.Live, "Live · not recorded");
        }
        catch (UnauthorizedAccessException)
        {
            await StopAsync();
            SetState(MirrorState.Unavailable, "Camera access is off in Windows Settings");
        }
        catch (Exception ex)
        {
            Log.Information("Camera mirror could not start ({Error})", ex.GetType().Name);
            await StopAsync();
            SetState(MirrorState.Unavailable, "Camera unavailable");
        }
    }

    private async Task StopAsync()
    {
        var reader = _reader;
        var capture = _capture;
        _reader = null;
        _capture = null;
        if (reader is not null)
        {
            reader.FrameArrived -= OnFrameArrived;
            try
            {
                await reader.StopAsync();
            }
            catch (Exception)
            {
                // Device already gone.
            }

            reader.Dispose();
        }

        if (capture is not null)
        {
            capture.Dispose(); // releases the camera
            CameraActiveChanged?.Invoke(this, false);
            OnPropertyChanged(nameof(IsCameraActive));
        }

        // The last frame is not kept either.
        _bitmap = null;
        OnPropertyChanged(nameof(Frame));

        if (_state is MirrorState.Live or MirrorState.Starting)
        {
            SetState(_enabled ? MirrorState.Ready : MirrorState.Off, _enabled ? "Starting…" : "Off");
        }
    }

    private static async Task<(MediaFrameSourceGroup? Group, MediaFrameSourceInfo? Source)> FindColorSourceAsync()
    {
        foreach (var group in await MediaFrameSourceGroup.FindAllAsync())
        {
            var source = group.SourceInfos.FirstOrDefault(s =>
                s.SourceKind == MediaFrameSourceKind.Color
                && s.MediaStreamType is Windows.Media.Capture.MediaStreamType.VideoPreview or Windows.Media.Capture.MediaStreamType.VideoRecord);
            if (source is not null)
            {
                return (group, source);
            }
        }

        return (null, null);
    }

    /// <summary>Camera thread: copy the newest frame (throttled, one in flight) and draw it on the UI thread.</summary>
    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastFrame < MinFrameTicks || Volatile.Read(ref _pending) == 1)
        {
            return;
        }

        using var frame = sender.TryAcquireLatestFrame();
        var source = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (source is null)
        {
            return;
        }

        SoftwareBitmap? converted = null;
        try
        {
            var bitmap = source;
            if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || source.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                bitmap = converted;
            }

            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;
            if (_pixels.Length != width * height * 4)
            {
                _pixels = new byte[width * height * 4];
            }

            bitmap.CopyToBuffer(_pixels.AsBuffer());
            _lastFrame = now;
            Volatile.Write(ref _pending, 1);
            _dispatcher.BeginInvoke(DispatcherPriority.Render, () => Draw(width, height));
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void Draw(int width, int height)
    {
        try
        {
            if (_capture is null)
            {
                return; // released meanwhile
            }

            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            {
                _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                OnPropertyChanged(nameof(Frame));
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
            FramesShown++;
        }
        finally
        {
            Volatile.Write(ref _pending, 0);
        }
    }

    private void SetState(MirrorState state, string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => SetState(state, message));
            return;
        }

        _state = state;
        _message = message;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(Message));
    }
}
