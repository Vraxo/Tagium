using Microsoft.Extensions.Logging;
using Tagium.Core.Files;
using Tagium.Core.Tags;

namespace Tagium.Core.Metadata;

/// <summary>
/// Rewrites the embedded metadata of files whose tags, ratings, titles or descriptions changed in
/// the catalog. Every write goes through one <see cref="FileSyncQueue"/>, so the ExifTool daemon is
/// handed one command at a time no matter how many files an edit touched, and a write that does not
/// land is counted and reported instead of vanishing into a discarded task.
/// </summary>
public class TagMetadataSyncService : IDisposable
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly ILogger<TagMetadataSyncService> _logger;
    private readonly FileSyncQueue _queue = new();
    private readonly Task _worker;

    public bool IsTagSyncRunning => _queue.IsRunning;
    public int TagSyncProcessed => _queue.Completed;
    public int TagSyncTotal => _queue.Total;

    /// <summary>The batch being drained, or the last one when the queue is idle.</summary>
    public FileSyncBatch CurrentBatch => _queue.ActiveBatch;

    /// <summary>Progress of the batch being written; the batch is named when it finishes.</summary>
    public event Action<int, int, FileSyncBatch?>? TagSyncProgress;

    public event Action? TagSyncCompleted;

    /// <summary>Raised once per finished batch that left files unwritten.</summary>
    public event Action<int, FileSyncBatch>? TagSyncFailed;

    public TagMetadataSyncService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        ILogger<TagMetadataSyncService> logger)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _logger = logger;

        _queue.Progress += OnQueueProgress;
        _worker = Task.Run(RunQueueAsync);
    }

    /// <summary>
    /// Queues the files touched by an edit — a tag added or removed, a rating changed, an undo.
    /// </summary>
    public void EnqueueFileUpdates(IEnumerable<Guid> fileIds) =>
        Enqueue(fileIds, FileSyncBatch.EditedFiles);

    /// <summary>
    /// Queues every file carrying a tag that was renamed or deleted, so the old name does not stay
    /// embedded in them.
    /// </summary>
    public void EnqueueTagUpdate(IEnumerable<Guid> fileIds) =>
        Enqueue(fileIds, FileSyncBatch.TagUpdated);

    private void Enqueue(IEnumerable<Guid> fileIds, FileSyncBatch batch)
    {
        // Without ExifTool the catalog is still authoritative; there is nothing to write and
        // nothing worth reporting.
        if (!_exifTool.IsEnabled)
        {
            return;
        }

        _ = _queue.Enqueue(fileIds, batch);
    }

    private async Task RunQueueAsync()
    {
        try
        {
            await _queue.RunAsync(WriteAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The queue was disposed during shutdown.
        }
    }

    /// <summary>
    /// Writes one file's catalog state into the file itself. Reads the catalog at write time rather
    /// than at enqueue time, so a file queued twice in a row still ends up with its latest state.
    /// </summary>
    private async Task<bool> WriteAsync(Guid fileId, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return false;
        }

        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            // Nothing to write to; a deleted entry is not a failure.
            return true;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = TagOrdering.ByName(tags).Select(tag => tag.Name).ToList(),
            Rating = file.Rating,
            Title = file.Title,
            Description = file.Description
        };

        bool written = await _exifTool.WriteMetadataAsync(file.Path, metadata).ConfigureAwait(false);

        if (!written)
        {
            _logger.LogWarning("Embedded metadata could not be written to {FilePath}", file.Path);
        }

        return written;
    }

    private void OnQueueProgress(int completed, int total, FileSyncBatch? finishedBatch, int failedInBatch)
    {
        TagSyncProgress?.Invoke(completed, total, finishedBatch);

        if (finishedBatch is null)
        {
            return;
        }

        TagSyncCompleted?.Invoke();

        if (failedInBatch > 0)
        {
            _logger.LogWarning(
                "{FailedCount} of {TotalCount} files kept their old embedded metadata after a {Batch} write-back",
                failedInBatch, total, finishedBatch.Value);

            TagSyncFailed?.Invoke(failedInBatch, finishedBatch.Value);
        }
    }

    public void Dispose()
    {
        _queue.Progress -= OnQueueProgress;
        _queue.Dispose();

        GC.SuppressFinalize(this);
    }
}
