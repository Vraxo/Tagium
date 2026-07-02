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
        ExifToolService exifTool)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
    }

    public async Task ScanAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        ProgressCurrent = 0;
        ProgressTotal = 0;

        await Task.Run(async () => await ExecuteScanPipelineAsync(cancellationToken), cancellationToken);
    }

    private async Task ExecuteScanPipelineAsync(CancellationToken cancellationToken)
    {
        Stopwatch uiTimer = Stopwatch.StartNew();
        ScanContext context = new();

        try
        {
            List<LibraryRoot> roots = _rootRepo.GetAll().Where(r => r.IsEnabled).ToList();
            if (roots.Count == 0)
            {
                return;
            }

            DiscoverCandidateFiles(roots, context);
            LoadDatabaseCache(roots, context);

            ProgressTotal = context.CandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            await ProcessCandidateFilesAsync(context, uiTimer, cancellationToken);
            await ResolveMissingAndImportNewAsync(context, uiTimer, cancellationToken);

            FinalizeMissingFiles(context);
            UpdateRootsLastScanTime(roots);

            if (context.HasChanges)
            {
                FilesImported?.Invoke();
            }
        }
        finally
        {
            IsScanning = false;
            ScanProgress?.Invoke(ProgressTotal, ProgressTotal);
            ScanCompleted?.Invoke();
        }
    }

    private static void DiscoverCandidateFiles(List<LibraryRoot> roots, ScanContext context)
    {
        foreach (LibraryRoot root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                continue;
            }

            try
            {
                IEnumerable<string> files = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)));

                context.CandidateFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scan] Error enumerating directory {root.Path}: {ex.Message}");
            }
        }
    }

    private void LoadDatabaseCache(List<LibraryRoot> roots, ScanContext context)
    {
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
        foreach (string filePath in context.CandidateFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
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
                        existing.IsMissing = false;
                        _fileRepo.Update(existing);
                        context.HasChanges = true;
                    }
                    _ = context.VerifiedPaths.Add(filePath);
                    ProgressCurrent++;
                }
                else
                {
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
            Console.WriteLine($"[Scan] Error examining path {filePath}: {ex.Message}");
        }
    }

    private async Task UpdateChangedFileAsync(FileEntry existing, string filePath, long size, long modifiedTime, ScanContext context, CancellationToken cancellationToken)
    {
        string? hash = await FileHasher.ComputeSha256Async(filePath);
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
                        string? candidateHash = await FileHasher.ComputeSha256Async(candidatePath);
                        if (candidateHash == missing.Hash)
                        {
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

    private static Dictionary<long, List<string>> GroupUntrackedBySize(List<string> untrackedFiles)
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
            catch { }
        }
        return map;
    }

    private async Task ImportRemainingUntrackedAsync(ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
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

                string? hash = await FileHasher.ComputeSha256Async(path);
                if (hash is not null)
                {
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
                Console.WriteLine($"[Scan] Error importing untracked file {path}: {ex.Message}");
            }

            ProgressCurrent++;
            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task ImportFileMetadataAsync(string path, FileEntry newEntry)
    {
        MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
        if (metadata is null)
        {
            return;
        }

        newEntry.Rating = metadata.Rating;
        newEntry.Title = metadata.Title;
        newEntry.Description = metadata.Description;
        _fileRepo.Update(newEntry);

        if (metadata.Tags.Count == 0)
        {
            return;
        }

        Dictionary<string, Tag> existingTags = _tagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        foreach (string rawTag in metadata.Tags)
        {
            string norm = TagFormatter.Normalize(rawTag);
            if (string.IsNullOrWhiteSpace(norm))
            {
                continue;
            }

            if (!existingTags.TryGetValue(norm, out Tag? dbTag))
            {
                _tagRepo.Add(norm, "#757575", null);
                Tag? refreshed = _tagRepo.GetAll().FirstOrDefault(t => string.Equals(t.Name, norm, StringComparison.OrdinalIgnoreCase));
                if (refreshed is not null)
                {
                    _fileTagRepo.AddTagToFile(newEntry.Id, refreshed.Id);
                }
            }
            else
            {
                _fileTagRepo.AddTagToFile(newEntry.Id, dbTag.Id);
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
            _fileRepo.SetMissingStatus(finalMissingIds, true);
            context.HasChanges = true;
        }
    }

    private void UpdateRootsLastScanTime(List<LibraryRoot> roots)
    {
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