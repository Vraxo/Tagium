using System.Globalization;

namespace Tagium.Core.Theming;

/// <summary>
/// WCAG 2.x relative-luminance and contrast-ratio helpers, shared by the theme guardrail
/// tests and by UI code that has to pick a readable foreground for an arbitrary colour.
/// </summary>
public static class ColorContrast
{
    /// <summary>
    /// Parses a CSS hex colour (<c>#rgb</c>, <c>#rrggbb</c>, with or without the hash and
    /// surrounding whitespace) into its WCAG relative luminance.
    /// </summary>
    public static bool TryGetRelativeLuminance(string? hex, out double luminance)
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

    /// <summary>
    /// Contrast ratio between two hex colours, where 1 is identical and 21 is black on white.
    /// Returns 0 when either colour cannot be parsed, so callers can treat it as "unknown".
    /// </summary>
    public static double GetContrastRatio(string? first, string? second)
    {
        if (!TryGetRelativeLuminance(first, out double firstLuminance) ||
            !TryGetRelativeLuminance(second, out double secondLuminance))
        {
            return 0;
        }

        double lighter = Math.Max(firstLuminance, secondLuminance);
        double darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>
    /// Picks whichever of the two supplied inks stays readable on top of <paramref name="backgroundHex"/>.
    /// Falls back to <paramref name="lightInk"/> when the background cannot be parsed.
    /// </summary>
    public static string GetReadableInk(string? backgroundHex, string lightInk, string darkInk)
    {
        if (!TryGetRelativeLuminance(backgroundHex, out _))
        {
            return lightInk;
        }

        return GetContrastRatio(backgroundHex, darkInk) >= GetContrastRatio(backgroundHex, lightInk)
            ? darkInk
            : lightInk;
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
