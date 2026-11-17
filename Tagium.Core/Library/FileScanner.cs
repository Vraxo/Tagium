using Microsoft.Extensions.Logging;
using Tagium.Core.Files;

namespace Tagium.Core.Library;

/// <summary>
/// Finds the files worth scanning: every supported file under the configured library roots,
/// minus anything already covered by a parent root.
///
/// Root pruning matters for correctness as well as speed. A root configured inside another root
/// would otherwise yield the same path twice, and a duplicate path would be processed twice in
/// one scan. Only the topmost root of any nested set is walked.
/// </summary>
public sealed class FileScanner
{
    private readonly ILogger<FileScanner> _logger;

    public FileScanner(ILogger<FileScanner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The outermost enabled roots, shortest path first, so a parent is always seen before the
    /// children it contains. Roots whose directory no longer exists are reported and skipped.
    /// </summary>
    public List<LibraryRoot> TopLevelRoots(IEnumerable<LibraryRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        List<LibraryRoot> ordered = [.. roots.Where(r => r.IsEnabled).OrderBy(r => r.Path.Length)];
        List<LibraryRoot> topLevel = [];

        foreach (LibraryRoot root in ordered)
        {
            bool isNested = topLevel.Any(parent => IsSameOrUnder(root.Path, parent.Path));
            if (!isNested)
            {
                topLevel.Add(root);
            }
        }

        return topLevel;
    }

    /// <summary>Every supported file under the given roots, in root order.</summary>
    public List<string> DiscoverFiles(IEnumerable<LibraryRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        List<string> files = [];

        foreach (LibraryRoot root in TopLevelRoots(roots))
        {
            if (!Directory.Exists(root.Path))
            {
                _logger.LogWarning("Library root directory does not exist: {Path}", root.Path);
                continue;
            }

            try
            {
                _logger.LogInformation("Enumerate files in library root: {Path}", root.Path);
                files.AddRange(Directory
                    .EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedFormats.All.Contains(Path.GetExtension(file))));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating directory: {Path}", root.Path);
            }
        }

        return files;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is the root itself or sits beneath it. Compared
    /// case-insensitively with a trailing separator so "/photos2" is not treated as inside
    /// "/photos".
    /// </summary>
    private static bool IsSameOrUnder(string path, string rootPath) =>
        path.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase);
}
