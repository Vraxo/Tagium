namespace Tagium.Core.Models;

public class FileEntry
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public long Size { get; set; }
    public long ModifiedTime { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public int? Rating { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}