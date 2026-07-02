using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Modals;

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
    private readonly ExifToolService _exifTool;

    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService undoService,
        LibraryRootRepository rootRepo,
        LibraryScanService scanService,
        ExifToolService exifTool)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        _undoService = undoService;
        _rootRepo = rootRepo;
        _scanService = scanService;
        _exifTool = exifTool;

        _scanService.ScanCompleted += () => LibraryChanged?.Invoke();
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    public void Undo()
    {
        _undoService.Undo();
    }

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            return [.. _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)];
        }
        else
        {
            return string.IsNullOrWhiteSpace(searchText)
                ? _fileRepo.GetFilesWithTags(sort, dir)
                : _searchRepo.Search(searchText, sort, dir);
        }
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
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool alreadyHadTag = existingIds.Contains(tagId);

        _fileTagRepo.AddTagToFile(fileId, tagId);

        if (!alreadyHadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: true));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
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
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
            }
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
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: false));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
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
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
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

    public List<LibraryRoot> GetRoots()
    {
        return _rootRepo.GetAll();
    }

    public void AddRoot(string path)
    {
        _rootRepo.Add(path);
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

    private async Task SyncMetadataToFileAsync(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = tags.Select(t => t.Name).ToList()
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }
}