using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public record ImportResult(AddFileResult Status, FileEntry? Entry);

public class ImportService
{
    private readonly FileRepository _fileRepo;
    private readonly ThumbnailService _thumbnailService;

    public event Action<int, int, int>? ImportCompleted;
    public event Action<int, int>? ImportProgress;

    public ImportService(FileRepository fileRepo, ThumbnailService thumbnailService)
    {
        _fileRepo = fileRepo;
        _thumbnailService = thumbnailService;
    }

    [Obsolete]
    public async Task<ImportResult> ImportAsync(string path)
    {
        string? hash = await FileHasher.ComputeSha256Async(path);
        if (hash is null)
        {
            return new ImportResult(AddFileResult.Error, null);
        }

        FileEntry? existing = _fileRepo.GetByHash(hash);
        if (existing is not null)
        {
            return new ImportResult(AddFileResult.Duplicate, existing);
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            FileEntry? newEntry = _fileRepo.GetByHash(hash);

            if (newEntry is not null)
            {
                await _thumbnailService.GenerateThumbnailAsync(newEntry);
            }

            return new ImportResult(AddFileResult.Success, newEntry);
        }

        return new ImportResult(result, null);
    }

    [Obsolete]
    public async Task ImportBatchAsync(string[] paths)
    {
        if (paths.Length == 0)
        {
            ImportCompleted?.Invoke(0, 0, 0);
            return;
        }

        const int MaxParallelism = 4;
        using var semaphore = new SemaphoreSlim(MaxParallelism);

        var tasks = paths.Select(async (path, index) =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!File.Exists(path))
                {
                    return (Status: AddFileResult.Error, Entry: null);
                }

                var result = await ImportAsync(path);

                if ((index + 1) % 10 == 0 || index == paths.Length - 1)
                {
                    ImportProgress?.Invoke(index + 1, paths.Length);
                }

                return (result.Status, result.Entry);
            }
            finally
            {
                _ = semaphore.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);

        int added = results.Count(r => r.Status == AddFileResult.Success);
        int duplicates = results.Count(r => r.Status == AddFileResult.Duplicate);
        int errors = results.Count(r => r.Status == AddFileResult.Error);

        ImportCompleted?.Invoke(added, duplicates, errors);
    }
}