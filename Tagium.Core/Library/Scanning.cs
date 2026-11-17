using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Tagium.Core.Files;
using Tagium.Core.Maintenance;

namespace Tagium.Core.Library;

/// <summary>
/// Runs a library scan: decides the order of the phases, owns the progress and completion events,
/// and throttles how often the UI hears about progress.
///
/// The phases themselves are separate types — <see cref="FileScanner"/> finds candidates,
/// <see cref="FileRelocator"/> re-points moved files, <see cref="MetadataImporter"/> reads
/// embedded metadata. This class holds no rules about files; it sequences those and records
/// what happened in a <see cref="ScanContext"/>.
/// </summary>
public class LibraryScanService
{
    private readonly LibraryRootRepository _rootRepo;
    private readonly FileRepository _fileRepo;
    private readonly FileScanner _fileScanner;
    private readonly FileRelocator _fileRelocator;
    private readonly MetadataImporter _metadataImporter;
    private readonly FileHasher _fileHasher;
    private readonly LibraryIntegrityService _integrityService;
    private readonly ILogger<LibraryScanService> _logger;

    public bool IsScanning { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }

    public event Action<int, int>? ScanProgress;
    public event Action? ScanCompleted;
    public event Action? FilesImported;
    public event Action<IReadOnlyList<FileEntry>>? DuplicatesDetected;

    public LibraryScanService(
        LibraryRootRepository rootRepo,
        FileRepository fileRepo,
        FileScanner fileScanner,
        FileRelocator fileRelocator,
        MetadataImporter metadataImporter,
        FileHasher fileHasher,
        LibraryIntegrityService integrityService,
        ILogger<LibraryScanService> logger)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _fileScanner = fileScanner;
        _fileRelocator = fileRelocator;
        _metadataImporter = metadataImporter;
        _fileHasher = fileHasher;
        _integrityService = integrityService;
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

            context.CandidateFiles.AddRange(_fileScanner.DiscoverFiles(roots));
            LoadDatabaseCache(roots, context);

            ProgressTotal = context.CandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            _logger.LogInformation("Processing {Count} candidate files...", ProgressTotal);
            await ProcessCandidateFilesAsync(context, uiTimer, cancellationToken);
            await ResolveMissingAndImportNewAsync(context, uiTimer, cancellationToken);

            if (context.Duplicates.Count > 0)
            {
                List<FileEntry> distinct = context.Duplicates.DistinctBy(d => d.Id).ToList();
                _logger.LogInformation("Detected {Count} imported files that duplicate existing library entries", distinct.Count);
                DuplicatesDetected?.Invoke(distinct);
            }

            UpdateRootsLastScanTime(roots);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in execute scan pipeline");
        }
        finally
        {
            await ReconcileAndNotifyAsync(context);
        }
    }

    /// <summary>
    /// Runs even when the scan failed or was partial, so the catalog still reflects what is on
    /// disk: availability is reconciled first, then listeners are told if anything changed.
    /// </summary>
    private async Task ReconcileAndNotifyAsync(ScanContext context)
    {
        try
        {
            IntegrityScanResult integrityResult = await _integrityService.ScanAsync();
            if (integrityResult.NewlyMissing > 0 || integrityResult.NewlyFound > 0)
            {
                context.HasChanges = true;
                _logger.LogInformation(
                    "Reconciled file availability after scan: {Missing} newly missing, {Found} returned",
                    integrityResult.NewlyMissing,
                    integrityResult.NewlyFound);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile file availability after scan");
        }

        if (context.HasChanges)
        {
            _logger.LogInformation("Library content changes detected. Triggering files imported event");
            try
            {
                FilesImported?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify listeners about library changes");
            }
        }

        IsScanning = false;
        ScanProgress?.Invoke(ProgressTotal, ProgressTotal);
        _logger.LogInformation("Library scan completed. Resetting state");
        ScanCompleted?.Invoke();
    }

    private void LoadDatabaseCache(List<LibraryRoot> roots, ScanContext context)
    {
        _logger.LogInformation("Loading current file definitions from database");

        foreach (LibraryRoot root in roots)
        {
            foreach (FileEntry dbFile in _fileRepo.GetFilesUnderPath(root.Path))
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
            FileIdentity identity = FileIdentity.Read(filePath);

            if (!context.DbFilesMap.TryGetValue(filePath, out FileEntry? existing))
            {
                context.UntrackedFiles.Add(filePath);
                return;
            }

            if (FileChangeDetector.IsUnchanged(existing, identity))
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
                return;
            }

            _logger.LogInformation("File change detected: {Path}", filePath);
            await UpdateChangedFileAsync(existing, filePath, identity, context, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error examining path: {Path}", filePath);
        }
    }

    private async Task UpdateChangedFileAsync(
        FileEntry existing,
        string filePath,
        FileIdentity identity,
        ScanContext context,
        CancellationToken cancellationToken)
    {
        string? hash = await _fileHasher.ComputeSha256Async(filePath);

        // An unreadable file is counted as processed either way: it is one candidate, done with.
        if (hash is not null)
        {
            existing.Hash = hash;
            existing.Size = identity.Size;
            existing.ModifiedTime = identity.ModifiedTimeUtc;
            existing.IsMissing = false;

            // Fields only: a rescan refreshes rating/title/description but never re-maps embedded
            // tags, so tags the user deleted do not come back. One write covers identity + fields.
            _ = await _metadataImporter.ApplyFieldsAsync(filePath, existing);

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

        HashSet<string> relocatedPaths = [];

        _ = await _fileRelocator.RelocateAsync(
            missingDbFiles,
            context.UntrackedFiles,
            relocatedPaths,
            onRelocated: entry =>
            {
                _fileRepo.Update(entry);
                context.HasChanges = true;
                ProgressCurrent++;
            },
            cancellationToken);

        await ImportRemainingUntrackedAsync(context, relocatedPaths, uiTimer, cancellationToken);
    }

    private async Task ImportRemainingUntrackedAsync(ScanContext context, HashSet<string> relocatedPaths, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Importing {Count} remaining untracked items", context.UntrackedFiles.Count - relocatedPaths.Count);

        foreach (string path in context.UntrackedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (relocatedPaths.Contains(path))
            {
                continue;
            }

            await ImportUntrackedFileAsync(path, context);
            ProgressCurrent++;
            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task ImportUntrackedFileAsync(string path, ScanContext context)
    {
        try
        {
            FileIdentity identity = FileIdentity.Read(path);

            string? hash = await _fileHasher.ComputeSha256Async(path);
            if (hash is null)
            {
                return;
            }

            FileEntry? existingWithSameHash = _fileRepo.GetByHash(hash);
            if (existingWithSameHash is not null &&
                !string.Equals(existingWithSameHash.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Imported file {Path} duplicates existing entry {ExistingPath}", path, existingWithSameHash.Path);
                context.Duplicates.Add(existingWithSameHash);
            }

            _logger.LogInformation("Importing untracked file: {Path}", path);
            FileEntry? added = _fileRepo.Add(path, hash, identity.Size, identity.ModifiedTimeUtc);
            context.HasChanges = true;

            // A rejected insert stored nothing, so there is no row to attach metadata to.
            if (added is null)
            {
                _logger.LogWarning("File could not be added to the catalog, skipping metadata: {Path}", path);
                return;
            }

            await _metadataImporter.ImportAsync(path, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing untracked file {Path}", path);
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
        if (uiTimer.ElapsedMilliseconds <= 250)
        {
            return;
        }

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
