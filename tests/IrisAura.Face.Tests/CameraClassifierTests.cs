using FluentAssertions;
using IrisAura.Face.Camera;

namespace IrisAura.Face.Tests;

/// <summary>Camera classification for Phase 0B (BLUEPRINT §3.4, §7.1): VID/PID, virtual cameras, tier, frame level.</summary>
public sealed class CameraClassifierTests
{
    [Theory]
    [InlineData(@"\?\USB#VID_04F2&PID_B6F1&MI_00#6&1a2b3c&0&0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL", "04F2:B6F1")]
    [InlineData(@"USB\VID_046D&PID_085E&MI_00", "046D:085E")]
    [InlineData(@"\?\SWD#VCAMDEVAPI#{guid}", null)]
    [InlineData(@"\?\USB#VID_ZZZZ&PID_0001", null)]
    [InlineData(null, null)]
    public void Vid_pid_comes_from_the_symbolic_link(string? link, string? expected) =>
        CameraClassifier.VidPid(link).Should().Be(expected);

    [Theory]
    [InlineData("OBS Virtual Camera", @"\?\SWD#VCAMDEVAPI#x", true)]
    [InlineData("NVIDIA Broadcast", @"\?\ROOT#IMAGE#0000", true)]
    [InlineData("Some Camera", @"\?\SWD#MSRRAS#x", true)]
    [InlineData("HP TrueVision HD Camera", @"\?\USB#VID_04F2&PID_B6F1&MI_00#x", false)]
    [InlineData("Integrated Camera", @"\?\MIPI#OV01A10#x", false)]
    public void Virtual_cameras_are_flagged(string name, string link, bool expected) =>
        CameraClassifier.IsVirtual(name, link).Should().Be(expected);

    private static CameraInfo Camera(bool ir, bool isVirtual = false) => new(
        "g", "cam",
        ir ? [new CameraSourceInfo("c", CameraKind.Color, "VideoRecord"), new CameraSourceInfo("i", CameraKind.Infrared, "VideoRecord")]
           : [new CameraSourceInfo("c", CameraKind.Color, "VideoRecord")],
        "04F2:B6F1", "Front", isVirtual);

    private static ProbeResult Probe(CameraKind kind, int frames) =>
        new(kind, "Exclusive", true, frames, frames > 0 ? 200 : null, 640, 360, "YUY2", frames, 10, 120, frames > 0 ? null : "no frames");

    [Fact]
    public void Tier_follows_what_was_actually_readable()
    {
        CameraClassifier.Tier(windowsHelloFace: true, []).Should().Be(CameraTier.T1WindowsHello);
        CameraClassifier.Tier(false, [(Camera(ir: false), [Probe(CameraKind.Color, 80)])]).Should().Be(CameraTier.T3ColorOnly);
        CameraClassifier.Tier(false, [(Camera(ir: true), [Probe(CameraKind.Color, 80), Probe(CameraKind.Infrared, 40)])])
            .Should().Be(CameraTier.T2InfraredAndColor);
        CameraClassifier.Tier(false, [(Camera(ir: true), [Probe(CameraKind.Color, 80), Probe(CameraKind.Infrared, 0)])])
            .Should().Be(CameraTier.T3ColorOnly, "an IR source that delivers no frames does not count");
        CameraClassifier.Tier(false, [(Camera(ir: false, isVirtual: true), [Probe(CameraKind.Color, 80)])])
            .Should().Be(CameraTier.None, "virtual cameras are untrusted");
        CameraClassifier.Tier(false, [(Camera(ir: false), [Probe(CameraKind.Color, 0)])]).Should().Be(CameraTier.None);
    }

    [Fact]
    public void Mean_level_samples_a_plane()
    {
        CameraClassifier.MeanLevel(Enumerable.Repeat((byte)200, 1024).ToArray()).Should().Be(200);
        CameraClassifier.MeanLevel(new byte[1024]).Should().Be(0);
        CameraClassifier.MeanLevel([]).Should().Be(0);
    }
}
