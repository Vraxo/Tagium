using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class TagMetadataSyncService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private CancellationTokenSource? _tagSyncCts;

    public bool IsTagSyncRunning { get; private set; }
    public int TagSyncProcessed { get; private set; }
    public int TagSyncTotal { get; private set; }

    public event Action<int, int>? TagSyncProgress;
    public event Action? TagSyncCompleted;

    public TagMetadataSyncService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
    }

    public void QueueTagMetadataSync(List<Guid> fileIds)
    {
        _tagSyncCts?.Cancel();
        _tagSyncCts = new CancellationTokenSource();
        CancellationToken token = _tagSyncCts.Token;

        if (fileIds.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            IsTagSyncRunning = true;
            TagSyncProcessed = 0;
            TagSyncTotal = fileIds.Count;

            TagSyncProgress?.Invoke(TagSyncProcessed, TagSyncTotal);

            try
            {
                foreach (Guid fileId in fileIds)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    await SyncMetadataToFileAsync(fileId);

                    TagSyncProcessed++;
                    TagSyncProgress?.Invoke(TagSyncProcessed, TagSyncTotal);
                }
            }
            catch (Exception)
            {
                // Gracefully ignore
            }
            finally
            {
                IsTagSyncRunning = false;
                TagSyncCompleted?.Invoke();
            }
        }, token);
    }

    public async Task SyncMetadataToFileAsync(Guid fileId)
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
            Tags = tags.Select(t => t.Name).ToList(),
            Rating = file.Rating,
            Title = file.Title,
            Description = file.Description
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }

    public void CancelSync()
    {
        _tagSyncCts?.Cancel();
    }
}