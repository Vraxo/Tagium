using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public enum RelocateResultStatus { Success, Duplicate, Error, NotFound }

public class RelocateResult
{
    public RelocateResultStatus Status { get; set; }
    public FileEntry? ConflictingFile { get; set; }
}

public class AddFileDetailedResult
{
    public AddFileResult Status { get; set; }
    public FileEntry? Entry { get; set; }
}

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;

    public event Action? LibraryChanged;
    public event Action<int, int, int>? ImportCompleted; // added, duplicates, errors
    public event Action<int, int>? ImportProgress; // processed, total

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    // --- Files ---

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        return missingOnly
            ? _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)
                .ToList()
            : string.IsNullOrWhiteSpace(searchText)
            ? _fileRepo.GetFilesWithTags(sort, dir)
            : _searchRepo.Search(searchText, sort, dir);
    }

    public FileEntry? GetFileById(long id)
    {
        return _fileRepo.GetById(id);
    }

    public bool AddFile(string path)
    {
        return AddFileDetailed(path).Status == AddFileResult.Success;
    }

    public async Task<AddFileDetailedResult> AddFileDetailedAsync(string path, bool suppressNotification = false)
    {
        string? hash = await FileHasher.ComputeSha256Async(path);
        if (hash is null)
        {
            return new AddFileDetailedResult { Status = AddFileResult.Error };
        }

        // Check for existing by hash first to return the entry
        FileEntry? existing = _fileRepo.GetByHash(hash);
        if (existing is not null)
        {
            return new AddFileDetailedResult { Status = AddFileResult.Duplicate, Entry = existing };
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            if (!suppressNotification)
            {
                LibraryChanged?.Invoke();
            }
            // Fetch the newly added entry
            FileEntry? newEntry = _fileRepo.GetByHash(hash);
            return new AddFileDetailedResult { Status = AddFileResult.Success, Entry = newEntry };
        }

        return new AddFileDetailedResult { Status = result };
    }

    // Kept for backward compatibility
    public AddFileDetailedResult AddFileDetailed(string path, bool suppressNotification = false)
    {
        return AddFileDetailedAsync(path, suppressNotification).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Imports multiple files asynchronously with progress reporting and non-blocking I/O.
    /// </summary>
    public async Task ImportBatchAsync(string[] paths)
    {
        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(0, 0, 0);
            return;
        }

        const int MaxParallelism = 4; // Prevent disk overload
        using var semaphore = new SemaphoreSlim(MaxParallelism);

        var tasks = paths.Select(async (path, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!File.Exists(path))
                {
                    return (Status: AddFileResult.Error, Entry: null);
                }

                var result = await AddFileDetailedAsync(path, suppressNotification: true);

                // Report progress every 10 files to reduce UI churn
                if ((index + 1) % 10 == 0 || index == paths.Length - 1)
                {
                    ImportProgress?.Invoke(index + 1, paths.Length);
                }

                return (result.Status, result.Entry);
            }
            finally
            {
                _ = semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int added = results.Count(r => r.Status == AddFileResult.Success);
        int duplicates = results.Count(r => r.Status == AddFileResult.Duplicate);
        int errors = results.Count(r => r.Status == AddFileResult.Error);

        if (added > 0)
        {
            LibraryChanged?.Invoke();
        }

        ImportCompleted?.Invoke(added, duplicates, errors);
    }

    // Kept for backward compatibility if needed, but redirects to async
    public void ImportBatch(string[] paths)
    {
        _ = ImportBatchAsync(paths);
    }

    public RelocateResult RelocateFile(long fileId, string newPath)
    {
        if (!File.Exists(newPath))
        {
            return new RelocateResult { Status = RelocateResultStatus.NotFound };
        }

        // This is a single file operation, async not critical
        string? newHash = FileHasher.ComputeSha256Async(newPath).GetAwaiter().GetResult();
        if (newHash is null)
        {
            return new RelocateResult { Status = RelocateResultStatus.Error };
        }

        // Check if the new file is already in the database
        FileEntry? existing = _fileRepo.GetByHash(newHash);
        if (existing is not null && existing.Id != fileId)
        {
            return new RelocateResult { Status = RelocateResultStatus.Duplicate, ConflictingFile = existing };
        }

        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return new RelocateResult { Status = RelocateResultStatus.Error };
        }

        file.Path = newPath;
        file.Name = Path.GetFileName(newPath);
        file.Hash = newHash;
        file.IsMissing = false;

        _fileRepo.Update(file);

        // Regenerate thumbnail for the new path
        _ = _thumbnailService.GenerateThumbnailAsync(file);

        LibraryChanged?.Invoke();
        return new RelocateResult { Status = RelocateResultStatus.Success };
    }

    public void RemoveFile(long fileId)
    {
        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void RemoveFiles(IEnumerable<long> fileIds)
    {
        if (!fileIds.Any())
        {
            return;
        }

        foreach (long id in fileIds)
        {
            _fileRepo.Remove(id);
        }
        LibraryChanged?.Invoke();
    }

    public void AddTagToFile(long fileId, long tagId)
    {
        _fileTagRepo.AddTagToFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    public void AddTagToFiles(IEnumerable<long> fileIds, long tagId)
    {
        if (!fileIds.Any())
        {
            return;
        }

        foreach (long id in fileIds)
        {
            _fileTagRepo.AddTagToFile(id, tagId);
        }
        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        _fileTagRepo.RemoveTagFromFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFiles(IEnumerable<long> fileIds, long tagId)
    {
        if (!fileIds.Any())
        {
            return;
        }

        foreach (long id in fileIds)
        {
            _fileTagRepo.RemoveTagFromFile(id, tagId);
        }
        LibraryChanged?.Invoke();
    }

    // --- Tags ---

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTagsForFile(long fileId)
    {
        List<long> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        return _tagRepo.GetTagsByIds(tagIds);
    }

    public List<Tag> GetCommonTags(IEnumerable<long> fileIds)
    {
        List<long> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // Start with tags of first file
        HashSet<long> commonIds = _fileTagRepo.GetTagIdsForFile(ids[0]).ToHashSet();

        // Intersect with remaining files
        for (int i = 1; i < ids.Count; i++)
        {
            List<long> tags = _fileTagRepo.GetTagIdsForFile(ids[i]);
            commonIds.IntersectWith(tags);

            // Optimization: If intersection is empty, no need to continue
            if (commonIds.Count == 0)
            {
                break;
            }
        }

        return _tagRepo.GetTagsByIds(commonIds);
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        _tagRepo.Update(tag);
        LibraryChanged?.Invoke();
    }

    public void DeleteTag(long id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }
}