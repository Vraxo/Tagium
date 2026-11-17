using Microsoft.Extensions.Logging;
using Tagium.Core.Files;

namespace Tagium.Core.Library;

/// <summary>
/// Recognises files that were renamed or moved rather than deleted and re-added.
///
/// A catalogued file whose path no longer exists and an untracked file whose path is new are the
/// same file if their size, last-write time and content hash all agree. Matching them keeps the
/// file's id — and with it its ratings, title, description and tags — which re-importing would
/// throw away.
///
/// Candidates are grouped by size first so a large library does not hash everything against
/// everything; only same-size pairs ever get hashed.
/// </summary>
public sealed class FileRelocator
{
    private readonly FileHasher _fileHasher;
    private readonly ILogger<FileRelocator> _logger;

    public FileRelocator(FileHasher fileHasher, ILogger<FileRelocator> logger)
    {
        _fileHasher = fileHasher;
        _logger = logger;
    }

    /// <summary>
    /// Re-points catalogued entries that have moved, recording each match in
    /// <paramref name="relocatedPaths"/> so the caller does not import the file a second time,
    /// and <paramref name="onRelocated"/> is invoked per match so the caller can persist it and
    /// publish progress without this type taking a repository. Returns the entries it re-pointed.
    /// </summary>
    public async Task<IReadOnlyList<FileEntry>> RelocateAsync(
        IReadOnlyList<FileEntry> missingEntries,
        IReadOnlyList<string> untrackedPaths,
        ISet<string> relocatedPaths,
        Action<FileEntry> onRelocated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(missingEntries);
        ArgumentNullException.ThrowIfNull(untrackedPaths);
        ArgumentNullException.ThrowIfNull(relocatedPaths);
        ArgumentNullException.ThrowIfNull(onRelocated);

        using LogTiming timer = LogTiming.Start(_logger, "Rename and move detection of {Count} missing items", missingEntries.Count);

        Dictionary<long, List<string>> untrackedBySize = GroupBySize(untrackedPaths);
        List<FileEntry> relocated = [];

        foreach (FileEntry missing in missingEntries)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrEmpty(missing.Hash) || !untrackedBySize.TryGetValue(missing.Size, out List<string>? candidates))
            {
                continue;
            }

            if (await TryRelocateAsync(missing, candidates, relocatedPaths, cancellationToken))
            {
                relocated.Add(missing);
                onRelocated(missing);
            }
        }

        return relocated;
    }

    private async Task<bool> TryRelocateAsync(
        FileEntry missing,
        List<string> candidates,
        ISet<string> relocatedPaths,
        CancellationToken cancellationToken)
    {
        foreach (string candidatePath in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (relocatedPaths.Contains(candidatePath))
            {
                continue;
            }

            FileIdentity? identity = FileIdentity.TryRead(candidatePath);
            if (identity is null)
            {
                _logger.LogDebug("Rename candidate could not be inspected, skipping: {Path}", candidatePath);
                continue;
            }

            if (missing.ModifiedTime != identity.Value.ModifiedTimeUtc)
            {
                continue;
            }

            string? candidateHash = await _fileHasher.ComputeSha256Async(candidatePath);
            if (candidateHash != missing.Hash)
            {
                continue;
            }

            _logger.LogInformation("Detected file rename/relocation. Original: {Old}, New: {New}", missing.Path, candidatePath);
            missing.Path = candidatePath;
            missing.Name = Path.GetFileName(candidatePath);
            missing.IsMissing = false;

            _ = relocatedPaths.Add(candidatePath);
            return true;
        }

        return false;
    }

    private Dictionary<long, List<string>> GroupBySize(IReadOnlyList<string> untrackedPaths)
    {
        Dictionary<long, List<string>> map = [];

        foreach (string path in untrackedPaths)
        {
            FileIdentity? identity = FileIdentity.TryRead(path);
            if (identity is null)
            {
                _logger.LogDebug("Failed to inspect file size for grouping: {Path}", path);
                continue;
            }

            if (!map.TryGetValue(identity.Value.Size, out List<string>? list))
            {
                list = [];
                map[identity.Value.Size] = list;
            }

            list.Add(path);
        }

        return map;
    }
}
