using AwesomeAssertions;
using Tagium.Core.Metadata;

namespace Tagium.Core.Tests.Metadata;

public class FileSyncQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly Guid First = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Third = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>Reports raised by <see cref="FileSyncQueue.Progress"/>, in order.</summary>
    private sealed record Report(int Completed, int Total, FileSyncBatch? Batch, int Failed);

    private sealed class RecordingWriter
    {
        private readonly Func<Guid, Task<bool>> _write;

        public RecordingWriter(Func<Guid, Task<bool>>? write = null)
        {
            _write = write ?? (_ => Task.FromResult(true));
        }

        public List<Guid> Writes { get; } = [];
        public int MaxInFlight;

        private int _inFlight;

        public async Task<bool> WriteAsync(Guid fileId, CancellationToken token)
        {
            int inFlight = Interlocked.Increment(ref _inFlight);

            lock (Writes)
            {
                MaxInFlight = Math.Max(MaxInFlight, inFlight);
                Writes.Add(fileId);
            }

            try
            {
                return await _write(fileId);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
            }
        }

        public int WriteCount
        {
            get { lock (Writes) { return Writes.Count; } }
        }

        public List<Guid> Copy()
        {
            lock (Writes) { return [.. Writes]; }
        }
    }

    /// <summary>
    /// Runs <paramref name="act"/> against a live queue and returns once the queue reports a
    /// finished batch. The subscription is in place before anything is enqueued, so a one-file
    /// batch cannot finish before the test is listening.
    /// </summary>
    private static async Task<List<Report>> DrainAsync(FileSyncQueue queue, Func<FileSyncQueue, Task> act)
    {
        List<Report> reports = [];
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        queue.Progress += (completed, total, batch, failed) =>
        {
            lock (reports)
            {
                reports.Add(new Report(completed, total, batch, failed));
            }

            if (batch.HasValue)
            {
                finished.TrySetResult();
            }
        };

        await act(queue);
        await finished.Task.WaitAsync(Timeout);

        lock (reports)
        {
            return [.. reports];
        }
    }

    private static async Task WaitForWrites(RecordingWriter writer, int count)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(Timeout);

        while (writer.WriteCount < count)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Only saw {writer.WriteCount} of {count} expected writes.");
            }

            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Writes_OneFileAtATime_InQueueOrder()
    {
        FileSyncQueue queue = new();
        RecordingWriter writer = new(async fileId =>
        {
            if (fileId == First)
            {
                // Hold the first file open long enough for a second writer to have jumped in,
                // had the queue allowed one.
                await Task.Delay(80);
            }

            return true;
        });

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, q =>
        {
            _ = q.Enqueue([First, Second, Third], FileSyncBatch.EditedFiles);
            return Task.CompletedTask;
        });

        writer.Copy().Should().Equal(First, Second, Third);
        writer.MaxInFlight.Should().Be(1, "the ExifTool daemon must never be handed two commands at once");
        reports[^1].Should().Be(new Report(3, 3, FileSyncBatch.EditedFiles, 0));

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task FailedWrites_AreCountedAndReportedWithTheFinishedBatch()
    {
        FileSyncQueue queue = new();
        RecordingWriter writer = new(fileId => Task.FromResult(fileId != First));

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, q =>
        {
            _ = q.Enqueue([First, Second], FileSyncBatch.TagUpdated);
            return Task.CompletedTask;
        });

        reports[^1].Should().Be(new Report(2, 2, FileSyncBatch.TagUpdated, 1));
        queue.FailedWrites.Should().Be(1);

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AWriteThatThrows_IsAFailure_AndDoesNotStrandTheBatch()
    {
        FileSyncQueue queue = new();
        RecordingWriter writer = new(fileId =>
            fileId == First
                ? throw new IOException("the file is open in another program")
                : Task.FromResult(true));

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, q =>
        {
            _ = q.Enqueue([First, Second], FileSyncBatch.EditedFiles);
            return Task.CompletedTask;
        });

        reports[^1].Should().Be(new Report(2, 2, FileSyncBatch.EditedFiles, 1));

        // The file after the failure must still be written.
        writer.Copy().Should().Equal(First, Second);

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public void Enqueue_SkipsFilesAlreadyWaiting_AndRepeatsInsideOneBatch()
    {
        FileSyncQueue queue = new();

        queue.Enqueue([First, First], FileSyncBatch.EditedFiles).Should().BeTrue();
        queue.Enqueue([First], FileSyncBatch.EditedFiles).Should().BeFalse("the file is already queued");
        queue.Total.Should().Be(1);

        queue.Enqueue([], FileSyncBatch.EditedFiles).Should().BeFalse();
        queue.Total.Should().Be(1);

        queue.Dispose();
    }

    [Fact]
    public async Task AFileEditedWhileItsWriteIsInFlight_IsWrittenAgain()
    {
        FileSyncQueue queue = new();
        TaskCompletionSource firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstWriteMayFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingWriter writer = null!;

        writer = new RecordingWriter(async fileId =>
        {
            if (fileId != First || writer.WriteCount > 1)
            {
                return true;
            }

            // The user tags the same file again while ExifTool is still working on it. The write
            // stays open until the test says so, which is what makes the overlap certain rather
            // than a race the test happens to win.
            _ = queue.Enqueue([First], FileSyncBatch.EditedFiles);
            firstWriteStarted.TrySetResult();
            await firstWriteMayFinish.Task.WaitAsync(Timeout);

            return true;
        });

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, async q =>
        {
            _ = q.Enqueue([First], FileSyncBatch.EditedFiles);

            await firstWriteStarted.Task.WaitAsync(Timeout);
            firstWriteMayFinish.TrySetResult();
        });

        writer.Copy().Should().Equal(First, First);
        reports[^1].Should().Be(new Report(2, 2, FileSyncBatch.EditedFiles, 0));

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancelPending_DropsQueuedFiles_AndNamesTheBatchItAbandoned()
    {
        FileSyncQueue queue = new();
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingWriter writer = new(async _ =>
        {
            await gate.Task.WaitAsync(Timeout);
            return true;
        });

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, async q =>
        {
            _ = q.Enqueue([First, Second, Third], FileSyncBatch.TagUpdated);

            // Wait until the first file is actually in ExifTool's hands, then abandon the rest.
            await WaitForWrites(writer, 1);
            q.CancelPending(FileSyncBatch.TagUpdated).Should().Be(2);

            gate.TrySetResult();
        });

        // Not reports[^1]: the write that was already in flight when the batch was abandoned
        // finishes afterwards and raises one more progress report, which the test may or may not
        // have collected by the time it looks. The cancel itself always reports, so find it.
        Report cancelReport = reports.Should()
            .ContainSingle(report => report.Batch == FileSyncBatch.TagUpdated)
            .Which;

        cancelReport.Total.Should().Be(1, "the two dropped files are no longer owed a write");
        cancelReport.Completed.Should().Be(0, "nothing had been written when the batch was dropped");

        await Task.Delay(100);
        writer.Copy().Should().Equal(First);
        queue.IsRunning.Should().BeFalse();

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Progress_CountsUpToTheBatchTotal_AndResetsAfterIt()
    {
        FileSyncQueue queue = new();
        RecordingWriter writer = new();

        Task worker = queue.RunAsync(writer.WriteAsync);

        List<Report> reports = await DrainAsync(queue, q =>
        {
            _ = q.Enqueue([First, Second, Third], FileSyncBatch.EditedFiles);
            return Task.CompletedTask;
        });

        reports[0].Should().Be(new Report(0, 3, null, 0), "the queue reports as soon as work is owed");
        reports[^1].Should().Be(new Report(3, 3, FileSyncBatch.EditedFiles, 0));

        queue.Completed.Should().Be(0, "the counters belong to the batch that just finished");
        queue.Total.Should().Be(0);
        queue.IsRunning.Should().BeFalse();

        queue.Dispose();
        await worker.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Dispose_StopsTheWorker_AndRefusesFurtherWork()
    {
        FileSyncQueue queue = new();
        RecordingWriter writer = new();
        Task worker = queue.RunAsync(writer.WriteAsync);

        queue.Dispose();

        await worker.WaitAsync(Timeout);
        queue.Enqueue([First], FileSyncBatch.EditedFiles).Should().BeFalse();
        writer.WriteCount.Should().Be(0);

        // Disposing twice must not throw on the already-disposed cancellation source.
        Action act = queue.Dispose;
        act.Should().NotThrow();
    }
}
