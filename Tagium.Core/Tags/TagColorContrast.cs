using Tagium.Core.Theming;

namespace Tagium.Core.Tags;

/// <summary>
/// Picks a readable label colour for chips that are filled with a user-chosen tag colour.
/// Tag colours come from a free palette (including near-white and near-black), so a fixed
/// white label disappears on the lighter entries.
/// </summary>
public static class TagColorContrast
{
    public const string LightInk = "#ffffff";
    public const string DarkInk = "#1b1a1c";

    /// <summary>
    /// Returns light or dark ink for the supplied background colour (any CSS hex form).
    /// Unparseable input falls back to light ink, matching the historical behaviour.
    /// </summary>
    public static string GetTextColor(string? backgroundHex) =>
        ColorContrast.GetReadableInk(backgroundHex, LightInk, DarkInk);
}
