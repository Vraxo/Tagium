namespace Tagium.Core.Library;

/// <summary>
/// One folder in the sidebar tree. It lives in Core rather than next to the component so the
/// navigation maths in <see cref="FolderTreeNavigation"/> can be unit-tested without the UI.
/// </summary>
public class FolderNode
{
    public required string Path { get; set; }
    public required string Name { get; set; }
    public bool IsExpanded { get; set; }
    public List<FolderNode> Children { get; set; } = [];
    public bool ChildrenLoaded { get; set; }
}
