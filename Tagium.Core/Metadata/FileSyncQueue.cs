using System.Threading.Channels;

namespace Tagium.Core.Metadata;

/// <summary>
/// The single queue of files whose embedded metadata has to be rewritten.
///
/// Writing back used to be fire-and-forget per edit: a batch tag on N files started N tasks that
/// all blocked on the one lock inside the ExifTool daemon, in an order nobody controlled, with
/// every failure discarded. Everything now lands here instead, so one worker performs the writes
/// one at a time and the queue can report what it did.
///
/// Only the ordering, de-duplication, cancellation and counting live in this type; performing the
/// write is the caller's job, which keeps the queue testable without ExifTool, SQLite or files.
/// </summary>
public sealed class FileSyncQueue : IDisposable
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Files waiting for the worker, and the batch they will be reported under.</summary>
    private HashSet<Guid> _pending = [];
    private FileSyncBatch? _activeBatch;
    private int _total;
    private int _completed;
    private int _failed;
    private int _failedEver;
    private bool _running;
    private bool _isDisposed;

    /// <summary>
    /// The worker took an item, or a batch finished. <c>batch</c> is null while the batch is still
    /// draining; when it is named, <c>failed</c> says how many of that batch's files were left
    /// unwritten, so a failure count is always attributable.
    /// </summary>
    public event Action<int, int, FileSyncBatch?, int>? Progress;

    public bool IsRunning
    {
        get { lock (_lock) { return _running; } }
    }

    /// <summary>Files in the batch being drained: written so far, and queued in total.</summary>
    public int Completed
    {
        get { lock (_lock) { return _completed; } }
    }

    public int Total
    {
        get { lock (_lock) { return _total; } }
    }

    /// <summary>Writes that did not land, across every batch since the queue was created.</summary>
    public int FailedWrites
    {
        get { lock (_lock) { return _failedEver; } }
    }

    /// <summary>
    /// The batch the worker is draining, or the last one it finished. Edits are the default, since
    /// they are what the queue does most.
    /// </summary>
    public FileSyncBatch ActiveBatch
    {
        get { lock (_lock) { return _activeBatch ?? FileSyncBatch.EditedFiles; } }
    }

    /// <summary>
    /// Queues <paramref name="fileIds"/> for write-back, skipping ids already waiting so one batch
    /// cannot make the daemon rewrite the same file twice. Returns false when nothing was queued.
    /// </summary>
    public bool Enqueue(IEnumerable<Guid> fileIds, FileSyncBatch batch)
    {
        List<Guid> queued = [];

        lock (_lock)
        {
            if (_isDisposed)
            {
                return false;
            }

            foreach (Guid fileId in fileIds.Distinct())
            {
                if (_pending.Add(fileId))
                {
                    queued.Add(fileId);
                }
            }

            if (queued.Count == 0)
            {
                return false;
            }

            _total += queued.Count;

            // A batch that arrives while the worker is busy joins the running one rather than
            // renaming it mid-flight; the worker names the next batch when it picks it up.
            if (!_running)
            {
                _activeBatch = batch;
            }

            Progress?.Invoke(_completed, _total, null, 0);
        }

        // Written outside the lock: the worker may already be reading, and TryWrite only touches
        // the channel's own state.
        foreach (Guid fileId in queued)
        {
            _channel.Writer.TryWrite(fileId);
        }

        return true;
    }

    /// <summary>
    /// Drops everything still waiting. The item the worker is writing right now finishes first —
    /// it is already in ExifTool's hands — and the queue reports the batch it abandoned.
    /// </summary>
    public int CancelPending(FileSyncBatch batch)
    {
        lock (_lock)
        {
            int dropped = _pending.Count;
            if (dropped == 0)
            {
                return 0;
            }

            _pending = [];
            _total -= dropped;
            Progress?.Invoke(_completed, _total, batch, _failed);

            // The batch is settled by this report; the write already in flight finishes quietly and
            // the next batch starts from zero.
            _activeBatch = null;
            _total = 0;
            _completed = 0;
            _failed = 0;
            return dropped;
        }
    }

    /// <summary>
    /// Runs the queue until the queue is disposed. <paramref name="writeAsync"/> performs one file's
    /// write and returns whether it landed; every item is written strictly after the one before it,
    /// so the daemon only ever has one command in flight.
    /// </summary>
    public async Task RunAsync(Func<Guid, CancellationToken, Task<bool>> writeAsync)
    {
        ArgumentNullException.ThrowIfNull(writeAsync);

        try
        {
            await DrainAsync(writeAsync).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The queue was disposed: shutdown, not a failure.
        }
    }

    private async Task DrainAsync(Func<Guid, CancellationToken, Task<bool>> writeAsync)
    {
        await foreach (Guid fileId in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
        {
            FileSyncBatch? startedBatch;

            lock (_lock)
            {
                // Cancelled while it waited: the worker never writes a file that is not queued.
                if (!_pending.Remove(fileId))
                {
                    continue;
                }

                startedBatch = _running ? null : _activeBatch;
                _running = true;
            }

            bool written = false;

            try
            {
                written = await writeAsync(fileId, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Counted as a failure and reported below; the queue keeps going so one bad file
                // cannot strand the rest of the batch.
            }

            Complete(fileId, written, startedBatch);
        }
    }

    private void Complete(Guid fileId, bool written, FileSyncBatch? startedBatch)
    {
        lock (_lock)
        {
            _completed++;

            if (!written)
            {
                _failed++;
                _failedEver++;
            }

            if (startedBatch.HasValue)
            {
                _activeBatch = startedBatch;
            }

            // A file re-enqueued while its write was in flight is still in _pending and already back
            // in the channel, so the batch does not end here and the file gets written again.
            if (_pending.Count == 0)
            {
                _running = false;
                Progress?.Invoke(_completed, _total, _activeBatch, _failed);

                // The batch is settled; the next one starts from zero.
                _activeBatch = null;
                _total = 0;
                _completed = 0;
                _failed = 0;
                return;
            }

            Progress?.Invoke(_completed, _total, null, 0);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
