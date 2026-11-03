namespace Tagium.Photino.Blazor.Models;

public class FolderNode
{
    public required string Path { get; set; }
    public required string Name { get; set; }
    public bool IsExpanded { get; set; }
    public List<FolderNode> Children { get; set; } = [];
    public bool ChildrenLoaded { get; set; }
}