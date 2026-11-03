namespace Tagium.Core.Files;

public static class DuplicateGrouper
{
    /// <summary>
    /// Groups files that share the same non-empty content hash. Only groups with
    /// more than one file are returned, ordered by descending group size.
    /// </summary>
    public static List<List<FileEntry>> Group(IEnumerable<FileEntry> files)
    {
        return files
            .Where(f => !string.IsNullOrEmpty(f.Hash))
            .GroupBy(f => f.Hash!)
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderByDescending(g => g.Count)
            .ToList();
    }
}
