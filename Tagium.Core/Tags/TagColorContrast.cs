using System.Globalization;

namespace Tagium.Core.Tags;

/// <summary>
/// Picks a readable label colour for chips that are filled with a user-chosen tag colour.
/// Tag colours come from a free palette (including near-white and near-black), so a fixed
/// white label disappears on the lighter entries. This helper mirrors the WCAG 2.x
/// relative-luminance formula and returns whichever ink has the higher contrast ratio.
/// </summary>
public static class TagColorContrast
{
    public const string LightInk = "#ffffff";
    public const string DarkInk = "#1b1a1c";

    private static readonly double LightInkLuminance = ComputeLuminance(0xFF, 0xFF, 0xFF);
    private static readonly double DarkInkLuminance = ComputeLuminance(0x1B, 0x1A, 0x1C);

    /// <summary>
    /// Returns light or dark ink for the supplied background colour (any CSS hex form).
    /// Unparseable input falls back to light ink, matching the historical behaviour.
    /// </summary>
    public static string GetTextColor(string? backgroundHex)
    {
        if (!TryGetLuminance(backgroundHex, out double backgroundLuminance))
        {
            return LightInk;
        }

        double lightContrast = ContrastRatio(backgroundLuminance, LightInkLuminance);
        double darkContrast = ContrastRatio(backgroundLuminance, DarkInkLuminance);

        return darkContrast >= lightContrast ? DarkInk : LightInk;
    }

    private static double ContrastRatio(double first, double second)
    {
        double lighter = Math.Max(first, second);
        double darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static bool TryGetLuminance(string? hex, out double luminance)
    {
        luminance = 0;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string value = hex.Trim().TrimStart('#');

        if (value.Length == 3)
        {
            value = $"{value[0]}{value[0]}{value[1]}{value[1]}{value[2]}{value[2]}";
        }

        if (value.Length != 6)
        {
            return false;
        }

        if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return false;
        }

        luminance = ComputeLuminance((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        return true;
    }

    private static double ComputeLuminance(int red, int green, int blue)
    {
        return (0.2126 * ToLinear(red)) + (0.7152 * ToLinear(green)) + (0.0722 * ToLinear(blue));
    }

    private static double ToLinear(int channel)
    {
        double value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
