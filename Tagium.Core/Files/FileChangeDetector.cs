namespace Tagium.Core.Files;

/// <summary>
/// Decides whether a catalogued file needs re-reading, and whether a file the catalog marks as
/// missing has come back.
///
/// The rule is one line, but it was previously spelled out at two call sites with subtly
/// different shapes, and it is the whole basis for skipping the expensive hash + metadata read.
/// Keeping it in one place means "unchanged" means the same thing everywhere.
/// </summary>
public static class FileChangeDetector
{
    /// <summary>
    /// True when the catalogued entry matches the file on disk, so its content can be trusted
    /// without re-hashing. Size catches truncation and most edits; the timestamp catches
    /// same-size edits.
    /// </summary>
    public static bool IsUnchanged(FileEntry entry, FileIdentity current) =>
        entry.Size == current.Size && entry.ModifiedTime == current.ModifiedTimeUtc;

    /// <summary>
    /// Whether this is a file returning to the library rather than one being edited: the content
    /// is unchanged but the entry is flagged missing.
    /// </summary>
    public static bool IsReturned(FileEntry entry, FileIdentity current) =>
        IsUnchanged(entry, current) && entry.IsMissing;
}
