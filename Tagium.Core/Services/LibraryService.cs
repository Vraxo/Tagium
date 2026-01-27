using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);

public record AddFileDetailedResult(AddFileResult Status, FileEntry? Entry);

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;
    private readonly UndoService _undoService;

    public event Action? LibraryChanged;
    public event Action<int, int, int>? ImportCompleted;
    public event Action<int, int>? ImportProgress;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService undoService)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        _undoService = undoService;
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    public bool CanUndo => _undoService.CanUndo;

    public void Undo()
    {
        _undoService.Undo();
    }

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

    public FileEntry? GetFileById(Guid id)
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
            return new AddFileDetailedResult(AddFileResult.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(hash);
        if (existing is not null)
        {
            return new AddFileDetailedResult(AddFileResult.Duplicate, existing);
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            if (!suppressNotification)
            {
                LibraryChanged?.Invoke();
            }
            FileEntry? newEntry = _fileRepo.GetByHash(hash);
            return new AddFileDetailedResult(AddFileResult.Success, newEntry);
        }

        return new AddFileDetailedResult(result, null);
    }

    public AddFileDetailedResult AddFileDetailed(string path, bool suppressNotification = false)
    {
        return AddFileDetailedAsync(path, suppressNotification).GetAwaiter().GetResult();
    }

    public async Task ImportBatchAsync(string[] paths)
    {
        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(0, 0, 0);
            return;
        }

        const int MaxParallelism = 4;
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

    public void ImportBatch(string[] paths)
    {
        _ = ImportBatchAsync(paths);
    }

    [Obsolete]
    public RelocateResult RelocateFile(Guid fileId, string newPath)
    {
        if (!File.Exists(newPath))
        {
            return new RelocateResult(RelocateResultStatus.NotFound, null);
        }

        string? newHash = FileHasher.ComputeSha256Async(newPath).GetAwaiter().GetResult();
        if (newHash is null)
        {
            return new RelocateResult(RelocateResultStatus.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(newHash);
        if (existing is not null && existing.Id != fileId)
        {
            return new RelocateResult(RelocateResultStatus.Duplicate, existing);
        }

        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return new RelocateResult(RelocateResultStatus.Error, null);
        }

        file.Path = newPath;
        file.Name = Path.GetFileName(newPath);
        file.Hash = newHash;
        file.IsMissing = false;

        _fileRepo.Update(file);

        _ = _thumbnailService.GenerateThumbnailAsync(file);

        LibraryChanged?.Invoke();
        return new RelocateResult(RelocateResultStatus.Success, null);
    }

    public void RemoveFile(Guid fileId)
    {
        var file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        var tagIds = _fileTagRepo.GetTagIdsForFile(fileId);

        _undoService.Push(new FileDeletionUndoCommand(
            _fileRepo,
            _fileTagRepo,
            () => LibraryChanged?.Invoke(),
            [(file, tagIds)]));

        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void RemoveFiles(IEnumerable<Guid> fileIds)
    {
        var idsList = fileIds.ToList();
        if (idsList.Count == 0)
        {
            return;
        }

        var deletedItems = new List<(FileEntry file, List<Guid> tagIds)>();

        foreach (var id in idsList)
        {
            var file = _fileRepo.GetById(id);
            if (file is not null)
            {
                var tags = _fileTagRepo.GetTagIdsForFile(id);
                deletedItems.Add((file, tags));
            }
        }

        if (deletedItems.Count != 0)
        {
            _undoService.Push(new FileDeletionUndoCommand(
                _fileRepo,
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                deletedItems));
        }

        foreach (var id in idsList)
        {
            _fileRepo.Remove(id);
        }

        LibraryChanged?.Invoke();
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool alreadyHadTag = existingIds.Contains(tagId);

        _fileTagRepo.AddTagToFile(fileId, tagId);

        if (!alreadyHadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: true));
        }

        LibraryChanged?.Invoke();
    }

    public void AddTagToFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];
        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (!existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.AddTagToFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool hadTag = existingIds.Contains(tagId);

        _fileTagRepo.RemoveTagFromFile(fileId, tagId);

        if (hadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: false));
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];
        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.RemoveTagFromFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));
        }

        LibraryChanged?.Invoke();
    }

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTags(TagSortBy sortBy, SortDirection direction)
    {
        return _tagRepo.GetAll(sortBy, direction);
    }

    public List<Tag> GetTagsForFile(Guid fileId)
    {
        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        return _tagRepo.GetTagsByIds(tagIds);
    }

    public List<Tag> GetCommonTags(IEnumerable<Guid> fileIds)
    {
        List<Guid> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        HashSet<Guid> commonIds = _fileTagRepo.GetTagIdsForFile(ids[0]).ToHashSet();

        for (int i = 1; i < ids.Count; i++)
        {
            List<Guid> tags = _fileTagRepo.GetTagIdsForFile(ids[i]);
            commonIds.IntersectWith(tags);

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

    public void DeleteTag(Guid id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }
}