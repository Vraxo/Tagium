using System.Collections.Concurrent;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public sealed class ThumbnailService
{
    private readonly ThumbnailCache _cache;
    private readonly ThumbnailGenerator _generator;
    private readonly VideoFrameExtractor _extractor;

    private readonly ConcurrentDictionary<string, bool> _activeGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _generationSemaphore;

    private int _queueTotal = 0;
    private int _queueProcessed = 0;
    private readonly object _queueLock = new();

    private CancellationTokenSource? _bgQueueCts;
    private bool _isBgQueueRunning = false;

    private const int DefaultThumbnailSize = 300;

    public event Action<Guid>? ThumbnailGenerated;
    public event Action<int, int>? QueueProgressChanged;
    public event Action? QueueCompleted;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public ThumbnailService()
    {
        _cache = new ThumbnailCache();
        _generator = new ThumbnailGenerator();
        _extractor = new VideoFrameExtractor();

        int concurrencyLimit = Math.Max(2, Environment.ProcessorCount / 2);
        _generationSemaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        return _cache.GetThumbnailPath(hash, fileId);
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        return _cache.HasThumbnail(fileId, hash);
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultThumbnailSize, CancellationToken cancellationToken = default)
    {
        string key = !string.IsNullOrEmpty(file.Hash) ? file.Hash : file.Id.ToString("N");
        string destPath = GetThumbnailPath(file.Hash, file.Id);

        if (_cache.HasThumbnail(file.Id, file.Hash) || !File.Exists(file.Path))
        {
            return;
        }

        if (!_activeGenerations.TryAdd(key, true))
        {
            return;
        }

        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(destPath))
            {
                await TryGenerateThumbnailInternalAsync(file.Path, destPath, size, cancellationToken);
            }
            _cache.AddToCache(key);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThumbnailService] Error generating for {file.Path}: {ex.Message}");
            _generator.GeneratePlaceholder(destPath, size, "ERR", ThumbnailGenerator.ErrorColor);
            _cache.AddToCache(key);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        finally
        {
            _generationSemaphore.Release();
            _activeGenerations.TryRemove(key, out _);
        }
    }

    private async Task TryGenerateThumbnailInternalAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ImageExtensions.Contains(extension))
        {
            _ = await _generator.GenerateImageThumbnailAsync(sourcePath, destPath, size, cancellationToken);
        }
        else if (VideoExtensions.Contains(extension))
        {
            if (!_extractor.HasFFmpeg)
            {
                _generator.GeneratePlaceholder(destPath, size, "VIDEO", ThumbnailGenerator.VideoColor);
                return;
            }

            string? tempFramePath = await _extractor.ExtractFrameAsync(sourcePath);
            if (tempFramePath is not null)
            {
                try
                {
                    _ = await _generator.GenerateImageThumbnailAsync(tempFramePath, destPath, size, cancellationToken);
                }
                finally
                {
                    VideoFrameExtractor.CleanupTempFile(tempFramePath);
                }
            }
            else
            {
                _generator.GeneratePlaceholder(destPath, size, "VIDEO", ThumbnailGenerator.VideoColor);
            }
        }
        else
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            _generator.GeneratePlaceholder(destPath, size, label, ThumbnailGenerator.GenericColor);
        }
    }

    public void QueueBackgroundGeneration(IEnumerable<FileEntry> files)
    {
        _bgQueueCts?.Cancel();
        _bgQueueCts = new CancellationTokenSource();
        CancellationToken token = _bgQueueCts.Token;

        List<FileEntry> fileList = files.ToList();
        if (fileList.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _isBgQueueRunning = true;
            try
            {
                List<FileEntry> missingFiles = fileList.Where(f => !HasThumbnail(f.Id, f.Hash)).ToList();
                int totalMissing = missingFiles.Count;

                if (totalMissing == 0)
                {
                    lock (_queueLock)
                    {
                        _queueProcessed = 0;
                        _queueTotal = 0;
                    }
                    QueueCompleted?.Invoke();
                    return;
                }

                lock (_queueLock)
                {
                    _queueProcessed = 0;
                    _queueTotal = totalMissing;
                    QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);
                }

                foreach (FileEntry file in missingFiles)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    while (_activeGenerations.Count > 0)
                    {
                        await Task.Delay(100, token);
                    }

                    await GenerateThumbnailAsync(file, DefaultThumbnailSize, token);

                    lock (_queueLock)
                    {
                        _queueProcessed++;
                        QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);
                    }
                }

                lock (_queueLock)
                {
                    _queueProcessed = 0;
                    _queueTotal = 0;
                }
                QueueCompleted?.Invoke();
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                _isBgQueueRunning = false;
            }
        }, token);
    }

    public void CancelBackgroundGeneration()
    {
        _bgQueueCts?.Cancel();
    }
}