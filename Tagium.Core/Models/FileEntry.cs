namespace Tagium.Core.Models;

public class FileEntry
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }

    // Loaded on demand
    public List<Tag> Tags { get; set; } = [];
}