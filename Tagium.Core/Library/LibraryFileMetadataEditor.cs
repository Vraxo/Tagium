using Tagium.Core.Files;
using Tagium.Core.Metadata;
using Tagium.Core.Tags;

namespace Tagium.Core.Library;

/// <summary>Applies file metadata changes together with undo and on-disk synchronization.</summary>
internal sealed class LibraryFileMetadataEditor
{
    private readonly FileRepository _fileRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly UndoService _undoService;
    private readonly TagMetadataSyncService _tagSyncService;
    private readonly Action _notifyChanged;

    public LibraryFileMetadataEditor(FileRepository fileRepo, FileTagRepository fileTagRepo,
        UndoService undoService, TagMetadataSyncService tagSyncService, Action notifyChanged)
    {
        _fileRepo = fileRepo;
        _fileTagRepo = fileTagRepo;
        _undoService = undoService;
        _tagSyncService = tagSyncService;
        _notifyChanged = notifyChanged;
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
                _notifyChanged,
                tagId,
                modifiedFiles,
                wasAdding: true));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = _tagSyncService.SyncMetadataToFileAsync(fileId);
            }
        }

        _notifyChanged();
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
                _notifyChanged,
                tagId,
                modifiedFiles,
                wasAdding: false));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = _tagSyncService.SyncMetadataToFileAsync(fileId);
            }
        }

        _notifyChanged();
    }

    public void SetRating(Guid fileId, int? rating)
    {
        SetRatings([fileId], rating);
    }

    public void SetRatings(IEnumerable<Guid> fileIds, int? rating)
    {
        if (rating is not null && (rating < 1 || rating > 5))
        {
            return;
        }

        List<Guid> ids = [.. fileIds];
        if (ids.Count == 0)
        {
            return;
        }

        Dictionary<Guid, int?> previousRatings = [];

        foreach (Guid id in ids)
        {
            FileEntry? file = _fileRepo.GetById(id);
            if (file is null || file.Rating == rating)
            {
                continue;
            }

            previousRatings[id] = file.Rating;
            _fileRepo.SetRating(id, rating);
        }

        if (previousRatings.Count != 0)
        {
            _undoService.Push(new RatingUndoCommand(
                _fileRepo,
                _tagSyncService,
                _notifyChanged,
                previousRatings));

            foreach (Guid fileId in previousRatings.Keys)
            {
                _ = _tagSyncService.SyncMetadataToFileAsync(fileId);
            }
        }

        _notifyChanged();
    }
}
