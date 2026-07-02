namespace Tagium.Core.Models;

public class Tag
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }
    public int FileCount { get; set; }
    public string? Category { get; set; }

    public override string ToString()
    {
        return Name;
    }
}