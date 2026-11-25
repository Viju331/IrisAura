using System.IO;
using System.Windows.Media;
using FluentAssertions;
using IrisAura.Island.Views;

namespace IrisAura.Island.Tests;

/// <summary>Album-art decode and tint on real image bytes (BLUEPRINT §8.6.1), off the UI thread.</summary>
public sealed class MediaArtworkTests
{
    private static byte[] LogoBytes() =>
        File.ReadAllBytes(Path.Combine(RepositoryPaths.Root, "assets", "brand", "irisaura-logo.png"));

    [Fact]
    public async Task Artwork_decodes_to_a_frozen_downscaled_bitmap_on_a_background_thread()
    {
        var bytes = LogoBytes();
        var bitmap = await Task.Run(() => AppVisualsResolver.Decode(bytes, 112), TestContext.Current.CancellationToken);

        bitmap.Should().NotBeNull();
        bitmap!.IsFrozen.Should().BeTrue("decoded off the UI thread, it must be frozen to cross threads");
        bitmap.PixelWidth.Should().Be(112);
    }

    [Fact]
    public async Task Tint_from_the_blue_violet_logo_is_a_blue_hue()
    {
        var bytes = LogoBytes();
        var brush = await Task.Run(() => MediaActivityView.TintFrom(bytes), TestContext.Current.CancellationToken);

        var color = brush.Should().BeOfType<SolidColorBrush>().Subject.Color;
        brush!.IsFrozen.Should().BeTrue();
        color.B.Should().BeGreaterThan(color.R).And.BeGreaterThan(color.G);
    }

    [Fact]
    public void Corrupt_artwork_is_ignored()
    {
        var junk = new byte[] { 1, 2, 3, 4, 5 };
        AppVisualsResolver.Decode(junk, 112).Should().BeNull();
        MediaActivityView.TintFrom(junk).Should().BeNull();
    }
}
