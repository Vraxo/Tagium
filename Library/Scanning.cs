using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class LibraryScanService
{
    private readonly LibraryRootRepository _rootRepo;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly FileHasher _fileHasher;
    private readonly ILogger<LibraryScanService> _logger;

    public bool IsScanning { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }

    public event Action<int, int>? ScanProgress;
    public event Action? ScanCompleted;
    public event Action? FilesImported;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public LibraryScanService(
        LibraryRootRepository rootRepo,
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        FileHasher fileHasher,
        ILogger<LibraryScanService> logger)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _fileHasher = fileHasher;
        _logger = logger;
    }

    public async Task ScanAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
        {
            _logger.LogWarning("ScanAllAsync requested while scanning is already in progress");
            return;
        }

        IsScanning = true;
        ProgressCurrent = 0;
        ProgressTotal = 0;

        _logger.LogInformation("Starting library scan...");
        await Task.Run(async () => await ExecuteScanPipelineAsync(cancellationToken), cancellationToken);
    }

    private async Task ExecuteScanPipelineAsync(CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Library scan execution pipeline");

        Stopwatch uiTimer = Stopwatch.StartNew();
        ScanContext context = new();

        try
        {
            List<LibraryRoot> roots = _rootRepo.GetAll().Where(r => r.IsEnabled).ToList();
            if (roots.Count == 0)
            {
                _logger.LogInformation("No active library roots configured. Scan aborted");
                return;
            }

            DiscoverCandidateFiles(roots, context);
            LoadDatabaseCache(roots, context);

            ProgressTotal = context.CandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            _logger.LogInformation("Processing {Count} candidate files...", ProgressTotal);
            await ProcessCandidateFilesAsync(context, uiTimer, cancellationToken);
            await ResolveMissingAndImportNewAsync(context, uiTimer, cancellationToken);

            FinalizeMissingFiles(context);
            UpdateRootsLastScanTime(roots);

            if (context.HasChanges)
            {
                _logger.LogInformation("Library content changes detected. Triggering files imported event");
                FilesImported?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in execute scan pipeline");
        }
        finally
        {
            IsScanning = false;
            ScanProgress?.Invoke(ProgressTotal, ProgressTotal);
            _logger.LogInformation("Library scan completed. Resetting state");
            ScanCompleted?.Invoke();
        }
    }

    private void DiscoverCandidateFiles(List<LibraryRoot> roots, ScanContext context)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Candidate file discovery in active roots");

        List<LibraryRoot> activeRoots = roots.Where(r => r.IsEnabled).OrderBy(r => r.Path.Length).ToList();
        List<LibraryRoot> pruned = new();

        foreach (var root in activeRoots)
        {
            bool isNested = pruned.Any(p =>
                root.Path.StartsWith(p.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.Path, p.Path, StringComparison.OrdinalIgnoreCase));

            if (!isNested)
            {
                pruned.Add(root);
            }
        }

        foreach (LibraryRoot root in pruned)
        {
            if (!Directory.Exists(root.Path))
            {
                _logger.LogWarning("Library root directory does not exist: {Path}", root.Path);
                continue;
            }

            try
            {
                using CancellationTokenSource searchCts = new(TimeSpan.FromSeconds(30));
                _logger.LogInformation("Enumerate files in library root: {Path}", root.Path);
                IEnumerable<string> files = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)));

                context.CandidateFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating directory: {Path}", root.Path);
            }
        }
    }

    private void LoadDatabaseCache(List<LibraryRoot> roots, ScanContext context)
    {
        _logger.LogInformation("Loading current file definitions from database");
        foreach (LibraryRoot root in roots)
        {
            List<FileEntry> dbFiles = _fileRepo.GetFilesUnderPath(root.Path);
            foreach (FileEntry dbFile in dbFiles)
            {
                context.DbFilesMap[dbFile.Path] = dbFile;
            }
        }
    }

    private async Task ProcessCandidateFilesAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Verification processing of {Count} candidates", context.CandidateFiles.Count);

        foreach (string filePath in context.CandidateFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("File processing canceled");
                break;
            }

            await EvaluateSingleFileAsync(filePath, context, cancellationToken);

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task EvaluateSingleFileAsync(string filePath, ScanContext context, CancellationToken cancellationToken)
    {
        try
        {
            FileInfo fileInfo = new(filePath);
            long size = fileInfo.Length;
            long modifiedTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

            if (context.DbFilesMap.TryGetValue(filePath, out FileEntry? existing))
            {
                if (existing.Size == size && existing.ModifiedTime == modifiedTime)
                {
                    if (existing.IsMissing)
                    {
                        _logger.LogInformation("File returned back to active library state: {Path}", filePath);
                        existing.IsMissing = false;
                        _fileRepo.Update(existing);
                        context.HasChanges = true;
                    }
                    _ = context.VerifiedPaths.Add(filePath);
                    ProgressCurrent++;
                }
                else
                {
                    _logger.LogInformation("File change detected: {Path}", filePath);
                    await UpdateChangedFileAsync(existing, filePath, size, modifiedTime, context, cancellationToken);
                }
            }
            else
            {
                context.UntrackedFiles.Add(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error examining path: {Path}", filePath);
        }
    }

    private async Task UpdateChangedFileAsync(FileEntry existing, string filePath, long size, long modifiedTime, ScanContext context, CancellationToken cancellationToken)
    {
        string? hash = await _fileHasher.ComputeSha256Async(filePath);
        if (hash is not null)
        {
            existing.Hash = hash;
            existing.Size = size;
            existing.ModifiedTime = modifiedTime;
            existing.IsMissing = false;

            MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(filePath);
            if (metadata is not null)
            {
                existing.Rating = metadata.Rating;
                existing.Title = metadata.Title;
                existing.Description = metadata.Description;
            }

            _fileRepo.Update(existing);
            context.HasChanges = true;
            _ = context.VerifiedPaths.Add(filePath);
        }
        ProgressCurrent++;
    }

    private async Task ResolveMissingAndImportNewAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        List<FileEntry> missingDbFiles = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path))
            .ToList();

        HashSet<string> processedUntracked = [];

        await DetectRenamesAndMovesAsync(missingDbFiles, context, processedUntracked, uiTimer, cancellationToken);
        await ImportRemainingUntrackedAsync(context, processedUntracked, uiTimer, cancellationToken);
    }

    private async Task DetectRenamesAndMovesAsync(List<FileEntry> missingDbFiles, ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Rename and move detection of {Count} missing items", missingDbFiles.Count);

        Dictionary<long, List<string>> untrackedBySize = GroupUntrackedBySize(context.UntrackedFiles);

        foreach (FileEntry missing in missingDbFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrEmpty(missing.Hash) || !untrackedBySize.TryGetValue(missing.Size, out List<string>? candidates))
            {
                continue;
            }

            foreach (string candidatePath in candidates)
            {
                if (processedUntracked.Contains(candidatePath))
                {
                    continue;
                }

                try
                {
                    FileInfo info = new(candidatePath);
                    long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                    if (missing.ModifiedTime == modifiedTime)
                    {
                        string? candidateHash = await _fileHasher.ComputeSha256Async(candidatePath);
                        if (candidateHash == missing.Hash)
                        {
                            _logger.LogInformation("Detected file rename/relocation. Original: {Old}, New: {New}", missing.Path, candidatePath);
                            missing.Path = candidatePath;
                            missing.Name = Path.GetFileName(candidatePath);
                            missing.IsMissing = false;

                            _fileRepo.Update(missing);
                            context.HasChanges = true;

                            _ = processedUntracked.Add(candidatePath);
                            _ = context.VerifiedPaths.Add(candidatePath);
                            ProgressCurrent++;
                            break;
                        }
                    }
                }
                catch { }
            }

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private Dictionary<long, List<string>> GroupUntrackedBySize(List<string> untrackedFiles)
    {
        Dictionary<long, List<string>> map = [];
        foreach (string path in untrackedFiles)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (!map.TryGetValue(size, out List<string>? list))
                {
                    list = [];
                    map[size] = list;
                }
                list.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect file size for grouping: {Path}", path);
            }
        }
        return map;
    }

    private async Task ImportRemainingUntrackedAsync(ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Importing {Count} remaining untracked items", context.UntrackedFiles.Count - processedUntracked.Count);

        foreach (string path in context.UntrackedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (processedUntracked.Contains(path))
            {
                continue;
            }

            try
            {
                FileInfo info = new(path);
                long size = info.Length;
                long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                string? hash = await _fileHasher.ComputeSha256Async(path);
                if (hash is not null)
                {
                    _logger.LogInformation("Importing untracked file: {Path}", path);
                    _ = _fileRepo.Add(path, hash, size, modifiedTime);
                    context.HasChanges = true;

                    FileEntry? newEntry = _fileRepo.GetByHash(hash);
                    if (newEntry is not null)
                    {
                        await ImportFileMetadataAsync(path, newEntry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing untracked file {Path}", path);
            }

            ProgressCurrent++;
            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task ImportFileMetadataAsync(string path, FileEntry newEntry)
    {
        using LogTiming timer = LogTiming.StartDebug(_logger, "Metadata and tag mapping extraction for {Path}", path);

        MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
        if (metadata is null)
        {
            return;
        }

        _logger.LogInformation("Extracting and writing embedded ExifTool metadata for imported file: {Path}", path);
        newEntry.Rating = metadata.Rating;
        newEntry.Title = metadata.Title;
        newEntry.Description = metadata.Description;
        _fileRepo.Update(newEntry);

        if (metadata.Tags.Count == 0)
        {
            return;
        }

        foreach (string rawTag in metadata.Tags)
        {
            string norm = TagFormatter.Normalize(rawTag);
            if (string.IsNullOrWhiteSpace(norm))
            {
                continue;
            }

            Guid? resolvedId = _tagRepo.ResolveTagId(norm);
            if (resolvedId.HasValue)
            {
                _fileTagRepo.AddTagToFile(newEntry.Id, resolvedId.Value);
            }
            else
            {
                _tagRepo.Add(norm, "#757575", null);
                Guid? refreshedId = _tagRepo.ResolveTagId(norm);
                if (refreshedId.HasValue)
                {
                    _fileTagRepo.AddTagToFile(newEntry.Id, refreshedId.Value);
                }
            }
        }
    }

    private void FinalizeMissingFiles(ScanContext context)
    {
        List<Guid> finalMissingIds = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path) && !e.IsMissing)
            .Select(e => e.Id)
            .ToList();

        if (finalMissingIds.Count > 0)
        {
            _logger.LogInformation("Setting missing status for {Count} cataloged files", finalMissingIds.Count);
            _fileRepo.SetMissingStatus(finalMissingIds, true);
            context.HasChanges = true;
        }
    }

    private void UpdateRootsLastScanTime(List<LibraryRoot> roots)
    {
        _logger.LogInformation("Updating scan timing markers on active roots");
        foreach (LibraryRoot root in roots)
        {
            root.LastScanTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _rootRepo.Update(root);
        }
    }

    private async Task YieldAndNotifyAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        if (uiTimer.ElapsedMilliseconds > 250)
        {
            if (context.HasChanges)
            {
                FilesImported?.Invoke();
                context.HasChanges = false;
            }

            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);
            uiTimer.Restart();

            await Task.Delay(50, cancellationToken);
        }
    }
}