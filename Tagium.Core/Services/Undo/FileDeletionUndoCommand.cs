using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Undo;

public class FileDeletionUndoCommand : IUndoCommand
{
    private readonly FileRepository _fileRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly Action _onChanged;
    private readonly List<(FileEntry file, List<Guid> tagIds)> _deletedItems;

    public FileDeletionUndoCommand(
        FileRepository fileRepo,
        FileTagRepository fileTagRepo,
        Action onChanged,
        List<(FileEntry file, List<Guid> tagIds)> deletedItems)
    {
        _fileRepo = fileRepo;
        _fileTagRepo = fileTagRepo;
        _onChanged = onChanged;
        _deletedItems = deletedItems;
    }

    public void Undo()
    {
        foreach (var (file, tagIds) in _deletedItems)
        {
            _fileRepo.Restore(file);

            foreach (var tagId in tagIds)
            {
                _fileTagRepo.AddTagToFile(file.Id, tagId);
            }
        }

        _onChanged();
    }
}