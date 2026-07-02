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
            foreach (var root in roots)
            {
                if (!Directory.Exists(root.Path))
                {
                    continue;
                }

                try
                {
                    var files = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
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

            HashSet<string> foundPaths = new(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in allCandidateFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    string? hash = await FileHasher.ComputeSha256Async(filePath);
                    if (hash is not null)
                    {
                        FileEntry? existing = _fileRepo.GetByHash(hash);

                        if (existing is not null)
                        {
                            if (!string.Equals(existing.Path, filePath, StringComparison.OrdinalIgnoreCase))
                            {
                                existing.Path = filePath;
                                existing.Name = Path.GetFileName(filePath);
                                existing.IsMissing = false;
                                _fileRepo.Update(existing);
                            }
                            else if (existing.IsMissing)
                            {
                                existing.IsMissing = false;
                                _fileRepo.Update(existing);
                            }
                        }
                        else
                        {
                            _ = _fileRepo.Add(filePath, hash);
                            FileEntry? newEntry = _fileRepo.GetByHash(hash);
                            if (newEntry is not null)
                            {
                                await _thumbnailService.GenerateThumbnailAsync(newEntry, cancellationToken: cancellationToken);

                                MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(filePath);
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
                                            var refreshed = _tagRepo.GetAll().FirstOrDefault(t => string.Equals(t.Name, norm, StringComparison.OrdinalIgnoreCase));
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

                        _ = foundPaths.Add(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scan] Error processing file {filePath}: {ex.Message}");
                }

                ProgressCurrent++;
                ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);
            }

            foreach (var root in roots)
            {
                var dbFiles = _fileRepo.GetFilesUnderPath(root.Path);
                List<Guid> missingIds = [];

                foreach (var dbFile in dbFiles)
                {
                    if (!foundPaths.Contains(dbFile.Path) && !dbFile.IsMissing)
                    {
                        missingIds.Add(dbFile.Id);
                    }
                }

                if (missingIds.Count > 0)
                {
                    _fileRepo.SetMissingStatus(missingIds, true);
                }

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