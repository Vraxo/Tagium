using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class LibraryService
{
    public bool CanUndo => _undoService.CanUndo;

    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;
    private readonly UndoService _undoService;
    private readonly LibraryRootRepository _rootRepo;
    private readonly LibraryScanService _scanService;
    private readonly TagMetadataSyncService _tagSyncService;

    public bool IsTagSyncRunning => _tagSyncService.IsTagSyncRunning;
    public int TagSyncProcessed => _tagSyncService.TagSyncProcessed;
    public int TagSyncTotal => _tagSyncService.TagSyncTotal;

    public event Action<int, int>? TagSyncProgress;
    public event Action? TagSyncCompleted;
    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService _undoService,
        LibraryRootRepository rootRepo,
        LibraryScanService scanService,
        TagMetadataSyncService tagSyncService)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        this._undoService = _undoService;
        _rootRepo = rootRepo;
        _scanService = scanService;
        _tagSyncService = tagSyncService;

        _scanService.ScanCompleted += () => LibraryChanged?.Invoke();
        _scanService.FilesImported += () => LibraryChanged?.Invoke();

        _tagSyncService.TagSyncProgress += (p, t) => TagSyncProgress?.Invoke(p, t);
        _tagSyncService.TagSyncCompleted += () => TagSyncCompleted?.Invoke();
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    public void Undo()
    {
        _undoService.Undo();
    }

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false, string? folderPath = null)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            return [.. _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)];
        }
        else if (!string.IsNullOrEmpty(folderPath))
        {
            return _fileRepo.GetFilesInFolderWithTags(folderPath, sort, dir);
        }
        else
        {
            return string.IsNullOrWhiteSpace(searchText)
                ? _fileRepo.GetFilesWithTags(sort, dir)
                : _searchRepo.Search(searchText, sort, dir);
        }
    }

    public List<string> GetSubfolders(string parentFolder)
    {
        return _fileRepo.GetSubfolders(parentFolder);
    }

    public FileEntry? GetFileById(Guid id)
    {
        return _fileRepo.GetById(id);
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        return _fileRepo.GetFileWithTagsById(id);
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        AddTagToFiles([fileId], tagId);
    }

    public void AddTagToFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

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
                _tagSyncService,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = _tagSyncService.SyncMetadataToFileAsync(fileId);
            }
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        RemoveTagFromFiles([fileId], tagId);
    }

    public void RemoveTagFromFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

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
                _tagSyncService,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = _tagSyncService.SyncMetadataToFileAsync(fileId);
            }
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

        HashSet<Guid> commonIds = [.. _fileTagRepo.GetTagIdsForFile(ids[0])];

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

    public List<LibraryRoot> GetRoots()
    {
        return _rootRepo.GetAll();
    }

    public void AddRoot(string path)
    {
        _rootRepo.Add(path);
        LibraryChanged?.Invoke();
    }

    public AddRootResult AddRootWithValidation(string path)
    {
        string targetPath = path.Trim();
        if (string.IsNullOrEmpty(targetPath))
        {
            return new AddRootResult { Status = AddRootStatus.Duplicate, Message = "Invalid path." };
        }

        string targetWithSep = targetPath.EndsWith(Path.DirectorySeparatorChar)
            ? targetPath
            : targetPath + Path.DirectorySeparatorChar;

        List<LibraryRoot> existingRoots = _rootRepo.GetAll();

        bool exactDuplicate = existingRoots.Any(r => string.Equals(r.Path.TrimEnd(Path.DirectorySeparatorChar), targetPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (exactDuplicate)
        {
            return new AddRootResult
            {
                Status = AddRootStatus.Duplicate,
                Message = "This folder is already registered as a library root."
            };
        }

        LibraryRoot? parentRoot = existingRoots.FirstOrDefault(r =>
        {
            string existingWithSep = r.Path.EndsWith(Path.DirectorySeparatorChar) ? r.Path : r.Path + Path.DirectorySeparatorChar;
            return targetWithSep.StartsWith(existingWithSep, StringComparison.OrdinalIgnoreCase);
        });

        if (parentRoot != null)
        {
            return new AddRootResult
            {
                Status = AddRootStatus.NestedInsideExisting,
                Message = $"This folder is already covered by your existing library folder: {parentRoot.Path}"
            };
        }

        List<LibraryRoot> childrenRoots = existingRoots.Where(r =>
        {
            string existingWithSep = r.Path.EndsWith(Path.DirectorySeparatorChar) ? r.Path : r.Path + Path.DirectorySeparatorChar;
            return existingWithSep.StartsWith(targetWithSep, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        List<string> consolidated = [];
        if (childrenRoots.Count > 0)
        {
            foreach (LibraryRoot child in childrenRoots)
            {
                _rootRepo.Delete(child.Id, child.Path);
                consolidated.Add(child.Path);
            }
        }

        _rootRepo.Add(targetPath);
        LibraryChanged?.Invoke();

        if (consolidated.Count > 0)
        {
            string consolidatedNames = string.Join(", ", consolidated.Select(p => Path.GetFileName(p) is string name && !string.IsNullOrEmpty(name) ? name : p));
            return new AddRootResult
            {
                Status = AddRootStatus.ConsolidatedChildren,
                Message = $"Added {targetPath}. Consolidated nested folder(s): {consolidatedNames}",
                ConsolidatedPaths = consolidated
            };
        }

        return new AddRootResult
        {
            Status = AddRootStatus.Success,
            Message = "Library folder added successfully."
        };
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        Tag? original = _tagRepo.GetTagsByIds([tag.Id]).FirstOrDefault();
        bool nameChanged = original is not null && !string.Equals(original.Name, tag.Name, StringComparison.OrdinalIgnoreCase);

        _tagRepo.Update(tag);

        if (nameChanged)
        {
            List<Guid> fileIds = _fileTagRepo.GetFileIdsForTag(tag.Id);
            _tagSyncService.QueueTagMetadataSync(fileIds);
        }

        LibraryChanged?.Invoke();
    }

    public void DeleteTag(Guid id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }

    public void UpdateRoot(LibraryRoot root)
    {
        _rootRepo.Update(root);
        LibraryChanged?.Invoke();
    }

    public void DeleteRoot(Guid id, string path)
    {
        _rootRepo.Delete(id, path);
        LibraryChanged?.Invoke();
    }

    public async Task ScanAsync()
    {
        await _scanService.ScanAllAsync();
    }
}