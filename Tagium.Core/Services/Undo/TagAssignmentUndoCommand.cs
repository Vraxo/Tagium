using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services.Undo;

public class TagAssignmentUndoCommand : IUndoCommand
{
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly Action _onChanged;
    private readonly Guid _tagId;
    private readonly List<Guid> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        FileRepository fileRepo,
        TagRepository tagRepo,
        Action onChanged,
        Guid tagId,
        IEnumerable<Guid> fileIds,
        bool wasAdding)
    {
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _onChanged = onChanged;
        _tagId = tagId;
        _fileIds = fileIds.ToList();
        _wasAdding = wasAdding;
    }

    public void Undo()
    {
        foreach (Guid fileId in _fileIds)
        {
            if (_wasAdding)
            {
                _fileTagRepo.RemoveTagFromFile(fileId, _tagId);
            }
            else
            {
                _fileTagRepo.AddTagToFile(fileId, _tagId);
            }

            _ = SyncMetadataToFileAsync(fileId);
        }

        _onChanged();
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