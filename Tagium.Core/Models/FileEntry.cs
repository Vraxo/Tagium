namespace Tagium.Core.Models;

public class FileEntry
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public List<Tag> Tags { get; set; } = [];
}