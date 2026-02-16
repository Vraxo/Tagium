using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Import;

public sealed class ImportService
{
    private readonly FileRepository _fileRepo;
    private readonly ThumbnailService _thumbnailService;

    public event Action<ImportSummary>? ImportCompleted;
    public event Action<ImportProgress>? ImportProgress;

    private const int DefaultProgressInterval = 10;
    private const int MaxParallelImports = 4;

    public ImportService(FileRepository fileRepo, ThumbnailService thumbnailService)
    {
        _fileRepo = fileRepo;
        _thumbnailService = thumbnailService;
    }

    public async Task<ImportResult> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        cancellationToken.ThrowIfCancellationRequested();

        string? hash = await FileHasher.ComputeSha256Async(path);

        if (hash is null)
        {
            return new(AddFileResult.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(hash);

        if (existing is not null)
        {
            return new(AddFileResult.Duplicate, existing);
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result != AddFileResult.Success)
        {
            return new(result, null);
        }

        FileEntry? newEntry = _fileRepo.GetByHash(hash);

        if (newEntry is not null)
        {
            await _thumbnailService.GenerateThumbnailAsync(newEntry);
        }

        return new(AddFileResult.Success, newEntry);
    }

    public async Task ImportBatchAsync(string[] paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(new ImportSummary(0, 0, 0));
            return;
        }

        using SemaphoreSlim semaphore = new(MaxParallelImports);
        List<AddFileResult> results = new(paths.Length);

        List<Task<AddFileResult>> tasks = [.. paths.Select(async (path, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);

            AddFileResult status;

            try
            {
                status = await TryImportAsync(path, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }

            lock (results)
            {
                results.Add(status);
            }

            if (ShouldReportProgress(index, paths.Length))
            {
                ImportProgress?.Invoke(new(index + 1, paths.Length));
            }

            return status;
        })];

        await Task.WhenAll(tasks);

        ImportCompleted?.Invoke(CreateSummary(results));
    }

    private async Task<AddFileResult> TryImportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return AddFileResult.Error;
        }

        ImportResult result = await ImportAsync(path, cancellationToken);
        return result.Status;
    }

    private static ImportSummary CreateSummary(List<AddFileResult> results)
    {
        return new(
            Added: results.Count(r => r == AddFileResult.Success),
            Duplicates: results.Count(r => r == AddFileResult.Duplicate),
            Errors: results.Count(r => r == AddFileResult.Error)
        );
    }

    private static bool ShouldReportProgress(int index, int total)
    {
        int current = index + 1;
        bool isInterval = current % DefaultProgressInterval == 0;

        return isInterval || current == total;
    }
}