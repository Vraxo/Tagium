namespace Tagium.Core.Library;

/// <summary>
/// A folder the user can currently reach, in display order. Collapsed folders contribute their
/// own row but none of their descendants.
/// </summary>
public sealed record FolderRow(string Path, int Level);
