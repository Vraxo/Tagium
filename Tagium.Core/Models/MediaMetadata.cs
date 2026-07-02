namespace Tagium.Core.Models;

public class MediaMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Rating { get; set; }
    public List<string> Tags { get; set; } = [];
}