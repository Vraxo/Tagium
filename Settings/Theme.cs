namespace Tagium.Photino.Blazor.Models;

public class Theme
{
    public required string Name { get; set; }
    public required Dictionary<string, string> Colors { get; set; }
}