namespace Tagium.Core.Models;

public class Tag
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }

    // Computed property for UI convenience, populated by join queries
    public int FileCount { get; set; }

    public override string ToString()
    {
        return Name;
    }
}