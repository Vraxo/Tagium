namespace Tagium.Photino.Blazor.Theming;

public class Theme
{
    public required string Name { get; set; }

    /// <summary>
    /// Revision of the bundled theme definition. Themes shipped with the app are
    /// refreshed on startup when this is newer than the copy stored in app data,
    /// so palette/token fixes reach existing installations.
    /// </summary>
    public int Version { get; set; } = 1;

    public required Dictionary<string, string> Colors { get; set; }
}
