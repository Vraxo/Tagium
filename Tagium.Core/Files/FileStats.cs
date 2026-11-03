namespace Tagium.Core.Files;

/// <summary>
/// Aggregate counters over the cached file entries (cache.Files).
/// </summary>
public record FileStats(
    long TotalFiles,
    long MissingFiles,
    long TotalSizeBytes,
    long HashedFiles,
    long DuplicateGroups,
    long DuplicateFiles)
{
    public long PresentFiles => TotalFiles - MissingFiles;
}

/// <summary>
/// Per-extension aggregate over present (non-missing) files.
/// </summary>
public record ExtensionStat(string Extension, long FileCount, long SizeBytes);
