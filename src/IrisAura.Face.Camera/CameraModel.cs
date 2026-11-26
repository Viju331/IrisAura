namespace IrisAura.Face.Camera;

/// <summary>Stream kinds a camera can expose (BLUEPRINT §3.4, §7.1).</summary>
public enum CameraKind
{
    Color,
    Infrared,
    Depth,
}

/// <summary>Device tier (BLUEPRINT §1.3, §7.1): T1 Windows Hello available, T2 readable IR + RGB in one group, T3 RGB only.</summary>
public enum CameraTier
{
    None,
    T1WindowsHello,
    T2InfraredAndColor,
    T3ColorOnly,
}

/// <summary>One frame source of a camera group.</summary>
/// <param name="SourceId">Source id within the group.</param>
/// <param name="Kind">Color / infrared / depth.</param>
/// <param name="StreamType">VideoPreview, VideoRecord, …</param>
public sealed record CameraSourceInfo(string SourceId, CameraKind Kind, string StreamType);

/// <summary>A camera (a MediaFrameSourceGroup), with what identifies and classifies it. No serial numbers.</summary>
/// <param name="GroupId">Frame source group id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Sources">Its frame sources.</param>
/// <param name="VidPid">USB vendor:product ("04F2:B6C2"), or <see langword="null"/>.</param>
/// <param name="Panel">Enclosure panel (Front, Back, Unknown); built-in cameras report a panel.</param>
/// <param name="IsVirtual">Software or virtual camera (untrusted for sign-in).</param>
public sealed record CameraInfo(string GroupId, string Name, IReadOnlyList<CameraSourceInfo> Sources, string? VidPid, string? Panel, bool IsVirtual)
{
    public bool IsBuiltIn => Panel is "Front" or "Back";

    public bool Has(CameraKind kind) => Sources.Any(s => s.Kind == kind);
}

/// <summary>Result of trying to read frames from one source.</summary>
/// <param name="Kind">Stream kind.</param>
/// <param name="SharingMode">ExclusiveControl or SharedReadOnly.</param>
/// <param name="Opened">The source could be initialised.</param>
/// <param name="Frames">Frames received.</param>
/// <param name="FirstFrameMs">Time from start to the first frame.</param>
/// <param name="Width">Frame width.</param>
/// <param name="Height">Frame height.</param>
/// <param name="Subtype">Pixel format.</param>
/// <param name="LitFrames">Frames whose mean level is above the dark threshold (IR emitters strobe; dark frames are expected).</param>
/// <param name="MeanLevelMin">Lowest per-frame mean level (0–255), aggregated only.</param>
/// <param name="MeanLevelMax">Highest per-frame mean level.</param>
/// <param name="Error">Error type or status when it failed.</param>
public sealed record ProbeResult(
    CameraKind Kind,
    string SharingMode,
    bool Opened,
    int Frames,
    double? FirstFrameMs,
    int Width,
    int Height,
    string? Subtype,
    int LitFrames,
    double? MeanLevelMin,
    double? MeanLevelMax,
    string? Error)
{
    public bool ReceivedFrames => Frames > 0;
}

/// <summary>Pure classification helpers (testable without hardware).</summary>
public static class CameraClassifier
{
    private static readonly string[] VirtualNames =
    [
        "virtual", "obs", "manycam", "snap camera", "xsplit", "nvidia broadcast", "camo", "droidcam", "ivcam", "epoccam",
        "splitcam", "youcam", "e2esoft", "vcam", "streamlabs", "mmhmm", "chromacam", "logi capture",
    ];

    /// <summary>USB VID:PID from a device symbolic link ("\\?\USB#VID_04F2&amp;PID_B6C2&amp;MI_00#…"), or <see langword="null"/>.</summary>
    public static string? VidPid(string? symbolicLink)
    {
        if (symbolicLink is null)
        {
            return null;
        }

        var upper = symbolicLink.ToUpperInvariant();
        var vid = upper.IndexOf("VID_", StringComparison.Ordinal);
        var pid = upper.IndexOf("PID_", StringComparison.Ordinal);
        if (vid < 0 || pid < 0 || vid + 8 > upper.Length || pid + 8 > upper.Length)
        {
            return null;
        }

        var v = upper.Substring(vid + 4, 4);
        var p = upper.Substring(pid + 4, 4);
        return v.All(Uri.IsHexDigit) && p.All(Uri.IsHexDigit) ? $"{v}:{p}" : null;
    }

    /// <summary>
    /// Virtual/software cameras (BLUEPRINT §3.4: "flag virtual cameras as untrusted"): known virtual-camera names, or a
    /// software-device (SWD) link.
    /// </summary>
    public static bool IsVirtual(string name, string? symbolicLink)
    {
        var lower = name.ToLowerInvariant();
        if (VirtualNames.Any(lower.Contains))
        {
            return true;
        }

        // Software devices (SWD) are how most virtual cameras register. A missing VID/PID alone is not enough: some
        // built-in MIPI cameras have none.
        var link = symbolicLink?.ToUpperInvariant() ?? string.Empty;
        return link.Contains(@"\SWD#", StringComparison.Ordinal) || link.StartsWith("SWD", StringComparison.Ordinal);
    }

    /// <summary>Tier from what was actually readable (BLUEPRINT §7.1).</summary>
    public static CameraTier Tier(bool windowsHelloFace, IEnumerable<(CameraInfo Camera, IReadOnlyList<ProbeResult> Probes)> cameras)
    {
        if (windowsHelloFace)
        {
            return CameraTier.T1WindowsHello;
        }

        var trusted = cameras.Where(c => !c.Camera.IsVirtual).ToList();
        bool Readable((CameraInfo Camera, IReadOnlyList<ProbeResult> Probes) c, CameraKind kind) =>
            c.Probes.Any(p => p.Kind == kind && p.ReceivedFrames);

        if (trusted.Any(c => Readable(c, CameraKind.Infrared) && Readable(c, CameraKind.Color)))
        {
            return CameraTier.T2InfraredAndColor;
        }

        return trusted.Any(c => Readable(c, CameraKind.Color)) ? CameraTier.T3ColorOnly : CameraTier.None;
    }

    /// <summary>Mean of an 8-bit plane sample (luma or IR), 0–255; used only as an aggregate "is the frame lit" signal.</summary>
    public static double MeanLevel(ReadOnlySpan<byte> plane, int step = 16)
    {
        if (plane.IsEmpty)
        {
            return 0;
        }

        long sum = 0;
        var count = 0;
        for (var i = 0; i < plane.Length; i += step)
        {
            sum += plane[i];
            count++;
        }

        return (double)sum / count;
    }
}
