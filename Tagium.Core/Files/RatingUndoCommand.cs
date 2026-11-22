using Tagium.Core;

namespace Tagium.Core.Files;

public class RatingUndoCommand : IUndoCommand
{
    private readonly FileRepository _fileRepo;
    private readonly Action<Guid> _enqueueFileSync;
    private readonly Action _onChanged;
    private readonly Dictionary<Guid, int?> _previousRatings;

    public RatingUndoCommand(
        FileRepository fileRepo,
        Action<Guid> enqueueFileSync,
        Action onChanged,
        Dictionary<Guid, int?> previousRatings)
    {
        _fileRepo = fileRepo;
        _enqueueFileSync = enqueueFileSync;
        _onChanged = onChanged;
        _previousRatings = previousRatings;
    }

    public void Undo()
    {
        foreach (KeyValuePair<Guid, int?> pair in _previousRatings)
        {
            _fileRepo.SetRating(pair.Key, pair.Value);
            _enqueueFileSync(pair.Key);
        }

        _onChanged();
    }
}
