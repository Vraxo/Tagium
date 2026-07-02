using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services;

public class LibraryScanService
{
    private readonly LibraryRootRepository _rootRepo;
    private readonly FileRepository _fileRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;

    public bool IsScanning { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }

    public event Action<int, int>? ScanProgress;
    public event Action? ScanCompleted;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public LibraryScanService(
        LibraryRootRepository rootRepo,
        FileRepository fileRepo,
        ThumbnailService thumbnailService,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _thumbnailService = thumbnailService;
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

        try
        {
            List<LibraryRoot> roots = _rootRepo.GetAll().Where(r => r.IsEnabled).ToList();
            if (roots.Count == 0)
            {
                return;
            }

            List<string> allCandidateFiles = [];
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
                    allCandidateFiles.AddRange(files);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scan] Error enumerating directory {root.Path}: {ex.Message}");
                }
            }

            ProgressTotal = allCandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            Dictionary<string, FileEntry> dbFilesMap = [];
            foreach (LibraryRoot root in roots)
            {
                List<FileEntry> dbFiles = _fileRepo.GetFilesUnderPath(root.Path);
                foreach (FileEntry dbFile in dbFiles)
                {
                    dbFilesMap[dbFile.Path] = dbFile;
                }
            }

            List<string> untrackedFiles = [];
            HashSet<string> verifiedPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in allCandidateFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    FileInfo fileInfo = new(filePath);
                    long size = fileInfo.Length;
                    long modifiedTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

                    if (dbFilesMap.TryGetValue(filePath, out FileEntry? existing))
                    {
                        if (existing.Size == size && existing.ModifiedTime == modifiedTime)
                        {
                            if (existing.IsMissing)
                            {
                                existing.IsMissing = false;
                                _fileRepo.Update(existing);
                            }
                            _ = verifiedPaths.Add(filePath);
                        }
                        else
                        {
                            string? hash = await FileHasher.ComputeSha256Async(filePath);
                            if (hash is not null)
                            {
                                existing.Hash = hash;
                                existing.Size = size;
                                existing.ModifiedTime = modifiedTime;
                                existing.IsMissing = false;
                                _fileRepo.Update(existing);

                                await _thumbnailService.GenerateThumbnailAsync(existing, cancellationToken: cancellationToken);
                                _ = verifiedPaths.Add(filePath);
                            }
                        }
                    }
                    else
                    {
                        untrackedFiles.Add(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scan] Error examining path {filePath}: {ex.Message}");
                }

                ProgressCurrent++;
                ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);
            }

            List<FileEntry> missingFilesInDb = [];
            foreach (FileEntry entry in dbFilesMap.Values)
            {
                if (!verifiedPaths.Contains(entry.Path))
                {
                    missingFilesInDb.Add(entry);
                }
            }

            Dictionary<long, List<string>> untrackedBySize = [];
            foreach (string path in untrackedFiles)
            {
                try
                {
                    long s = new FileInfo(path).Length;
                    if (!untrackedBySize.TryGetValue(s, out List<string>? value))
                    {
                        value = [];
                        untrackedBySize[s] = value;
                    }
                    value.Add(path);
                }
                catch { }
            }

            HashSet<string> processedUntracked = [];

            foreach (FileEntry missing in missingFilesInDb)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrEmpty(missing.Hash))
                {
                    continue;
                }

                if (untrackedBySize.TryGetValue(missing.Size, out List<string>? candidates))
                {
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

                                    _ = processedUntracked.Add(candidatePath);
                                    _ = verifiedPaths.Add(candidatePath);
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            foreach (string path in untrackedFiles)
            {
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
                        FileEntry? newEntry = _fileRepo.GetByHash(hash);
                        if (newEntry is not null)
                        {
                            await _thumbnailService.GenerateThumbnailAsync(newEntry, cancellationToken: cancellationToken);

                            MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
                            if (metadata is not null && metadata.Tags.Count > 0)
                            {
                                var existingTags = _tagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
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
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scan] Error importing untracked file {path}: {ex.Message}");
                }
            }

            List<Guid> finalMissingIds = [];
            foreach (FileEntry missing in missingFilesInDb)
            {
                if (!verifiedPaths.Contains(missing.Path) && !missing.IsMissing)
                {
                    finalMissingIds.Add(missing.Id);
                }
            }

            if (finalMissingIds.Count > 0)
            {
                _fileRepo.SetMissingStatus(finalMissingIds, true);
            }

            foreach (LibraryRoot root in roots)
            {
                root.LastScanTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _rootRepo.Update(root);
            }
        }
        finally
        {
            IsScanning = false;
            ScanCompleted?.Invoke();
        }
    }
}