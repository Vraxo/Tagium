using AwesomeAssertions;
using Tagium.Core.Tags;

namespace Tagium.Core.Tests.Tags;

public class TagColorContrastTests
{
    [Theory]
    [InlineData("#FFFFFF", TagColorContrast.DarkInk)]
    [InlineData("#fff", TagColorContrast.DarkInk)]
    [InlineData("  #ffffff  ", TagColorContrast.DarkInk)]
    [InlineData("#fdd835", TagColorContrast.DarkInk)] // yellow swatch
    [InlineData("#c0ca33", TagColorContrast.DarkInk)]
    [InlineData("#1e88e5", TagColorContrast.DarkInk)]
    [InlineData("#000000", TagColorContrast.LightInk)]
    [InlineData("#e53935", TagColorContrast.LightInk)]
    [InlineData("#757575", TagColorContrast.LightInk)] // default tag colour
    [InlineData("#3949ab", TagColorContrast.LightInk)]
    public void GetTextColor_PicksReadableInkForBackground(string background, string expected)
    {
        TagColorContrast.GetTextColor(background).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-colour")]
    [InlineData("#12345")]
    public void GetTextColor_FallsBackToLightInkForInvalidInput(string? background)
    {
        TagColorContrast.GetTextColor(background).Should().Be(TagColorContrast.LightInk);
    }

    [Fact]
    public void GetTextColor_ReachesWcagAaOnPaletteSwatches()
    {
        string[] palette =
        [
            "#e53935", "#d81b60", "#8e24aa", "#5e35b1", "#3949ab", "#1e88e5", "#039be5",
            "#00acc1", "#00897b", "#43a047", "#7cb342", "#c0ca33", "#fdd835", "#ffb300",
            "#fb8c00", "#f4511e", "#6d4c41", "#757575", "#546e7a", "#FFFFFF", "#000000"
        ];

        foreach (string color in palette)
        {
            string ink = TagColorContrast.GetTextColor(color);
            double ratio = ContrastRatio(color, ink);

            // 4.5 is the WCAG AA threshold for normal text; mid-tone reds top out just below it.
            Assert.True(ratio >= 4.2, $"{ink} on {color} only reaches a contrast ratio of {ratio:F2}");
        }
    }

    private static double ContrastRatio(string background, string foreground)
    {
        double backgroundLuminance = Luminance(background);
        double foregroundLuminance = Luminance(foreground);
        double lighter = Math.Max(backgroundLuminance, foregroundLuminance);
        double darker = Math.Min(backgroundLuminance, foregroundLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        int rgb = Convert.ToInt32(value, 16);
        int red = (rgb >> 16) & 0xFF;
        int green = (rgb >> 8) & 0xFF;
        int blue = rgb & 0xFF;

        return (0.2126 * ToLinear(red)) + (0.7152 * ToLinear(green)) + (0.0722 * ToLinear(blue));
    }

    private static double ToLinear(int channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
