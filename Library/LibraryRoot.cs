namespace Tagium.Core.Models;

public class LibraryRoot
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long? LastScanTime { get; set; }
}