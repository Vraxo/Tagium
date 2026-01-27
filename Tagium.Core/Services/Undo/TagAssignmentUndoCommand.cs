using Tagium.Core.Data;

namespace Tagium.Core.Services.Undo;

public class TagAssignmentUndoCommand : IUndoCommand
{
    private readonly FileTagRepository _fileTagRepo;
    private readonly Action _onChanged;
    private readonly long _tagId;
    private readonly List<long> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        Action onChanged,
        long tagId,
        IEnumerable<long> fileIds,
        bool wasAdding)
    {
        _fileTagRepo = fileTagRepo;
        _onChanged = onChanged;
        _tagId = tagId;
        _fileIds = fileIds.ToList();
        _wasAdding = wasAdding;
    }

    public void Undo()
    {
        foreach (long fileId in _fileIds)
        {
            if (_wasAdding)
            {
                _fileTagRepo.RemoveTagFromFile(fileId, _tagId);
            }
            else
            {
                _fileTagRepo.AddTagToFile(fileId, _tagId);
            }
        }

        _onChanged();
    }
}