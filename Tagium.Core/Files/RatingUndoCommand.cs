using Tagium.Core;
using Tagium.Core.Metadata;

namespace Tagium.Core.Files;

public class RatingUndoCommand : IUndoCommand
{
    private readonly FileRepository _fileRepo;
    private readonly TagMetadataSyncService _tagSyncService;
    private readonly Action _onChanged;
    private readonly Dictionary<Guid, int?> _previousRatings;

    public RatingUndoCommand(
        FileRepository fileRepo,
        TagMetadataSyncService tagSyncService,
        Action onChanged,
        Dictionary<Guid, int?> previousRatings)
    {
        _fileRepo = fileRepo;
        _tagSyncService = tagSyncService;
        _onChanged = onChanged;
        _previousRatings = previousRatings;
    }

    public void Undo()
    {
        foreach (KeyValuePair<Guid, int?> pair in _previousRatings)
        {
            _fileRepo.SetRating(pair.Key, pair.Value);
            _ = _tagSyncService.SyncMetadataToFileAsync(pair.Key);
        }

        _onChanged();
    }
}
