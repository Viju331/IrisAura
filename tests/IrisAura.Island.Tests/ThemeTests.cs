using System.Windows;
using System.Windows.Media;
using FluentAssertions;

namespace IrisAura.Island.Tests;

/// <summary>Verifies the shared brand tokens and glyphs against BLUEPRINT §0.4 and §0.5.</summary>
public sealed class ThemeTests
{
    // Exact values from BLUEPRINT §0.4.
    public static TheoryData<string, string> BrandTokens() => new()
    {
        { "Aura.Void", "#FF000000" },
        { "Aura.Surface", "#FF0B0D1A" },
        { "Aura.Frost", "#FFE2E6F4" },
        { "Aura.Mist", "#FFA8C4F0" },
        { "Aura.Cyan", "#FF22C3F2" },
        { "Aura.Blue", "#FF4A92F2" },
        { "Aura.Indigo", "#FF1A30A8" },
        { "Aura.Violet", "#FF8A3FF2" },
        { "Aura.Error", "#FFFF4D5E" },
    };

    [Theory]
    [MemberData(nameof(BrandTokens))]
    public void Brand_token_has_blueprint_colour_as_colour_and_frozen_brush(string token, string expectedHex)
    {
        StaThread.Run(() =>
        {
            var brand = Load("Themes/Brand.xaml");
            var expected = (Color)ColorConverter.ConvertFromString(expectedHex);

            brand[token + ".Color"].Should().Be(expected);

            var brush = brand[token].Should().BeOfType<SolidColorBrush>().Subject;
            brush.Color.Should().Be(expected);
            brush.IsFrozen.Should().BeTrue();
        });
    }

    [Fact]
    public void Aura_ring_runs_cyan_blue_violet_at_135_degrees()
    {
        StaThread.Run(() =>
        {
            var brand = Load("Themes/Brand.xaml");
            var ring = brand["Aura.Ring"].Should().BeOfType<LinearGradientBrush>().Subject;

            ring.StartPoint.Should().Be(new Point(0, 0));
            ring.EndPoint.Should().Be(new Point(1, 1));
            ring.GradientStops.Select(s => s.Color).Should().Equal(
                (Color)brand["Aura.Cyan.Color"], (Color)brand["Aura.Blue.Color"], (Color)brand["Aura.Violet.Color"]);
        });
    }

    [Theory]
    [InlineData("Aura.Glyph.FaceBrackets")]
    [InlineData("Aura.Glyph.FacePerson")]
    [InlineData("Aura.Glyph.Check")]
    [InlineData("Aura.Glyph.Play")]
    [InlineData("Aura.Glyph.Pause")]
    [InlineData("Aura.Glyph.Previous")]
    [InlineData("Aura.Glyph.Next")]
    [InlineData("Aura.Glyph.Volume")]
    [InlineData("Aura.Glyph.VolumeMute")]
    [InlineData("Aura.Glyph.Brightness")]
    [InlineData("Aura.Glyph.Bolt")]
    [InlineData("Aura.Glyph.BatteryLow")]
    [InlineData("Aura.Glyph.Headphones")]
    [InlineData("Aura.Glyph.Mouse")]
    [InlineData("Aura.Glyph.Keyboard")]
    [InlineData("Aura.Glyph.Phone")]
    [InlineData("Aura.Glyph.Device")]
    [InlineData("Aura.Glyph.Timer")]
    [InlineData("Aura.Glyph.Download")]
    [InlineData("Aura.Glyph.Mic")]
    [InlineData("Aura.Glyph.Camera")]
    [InlineData("Aura.Glyph.Call")]
    public void Glyph_fits_the_24_unit_design_grid(string key)
    {
        StaThread.Run(() =>
        {
            var glyphs = Load("Assets/Glyphs.xaml");
            var geometry = glyphs[key].Should().BeAssignableTo<Geometry>().Subject;

            var bounds = geometry.Bounds;
            bounds.IsEmpty.Should().BeFalse();
            bounds.Left.Should().BeGreaterThanOrEqualTo(0);
            bounds.Top.Should().BeGreaterThanOrEqualTo(0);
            bounds.Right.Should().BeLessThanOrEqualTo(24);
            bounds.Bottom.Should().BeLessThanOrEqualTo(24);
        });
    }

    // Application.LoadComponent resolves the compiled BAML from the referenced IrisAura.Theme assembly
    // without needing a running Application (pack:// URIs alone are not registered in a test host).
    private static ResourceDictionary Load(string path) =>
        (ResourceDictionary)Application.LoadComponent(new Uri($"/IrisAura.Theme;component/{path}", UriKind.Relative));
}
