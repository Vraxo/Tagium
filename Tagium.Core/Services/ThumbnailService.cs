using FFMpegCore;
using SkiaSharp;
using System.Collections.Concurrent;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public sealed class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;
    private readonly ConcurrentDictionary<string, bool> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _activeGenerations = new(StringComparer.OrdinalIgnoreCase);

    // Dynamic concurrency throttle configured based on physical logical core counts
    private readonly SemaphoreSlim _generationSemaphore;

    private int _queueTotal = 0;
    private int _queueProcessed = 0;
    private readonly object _queueLock = new();

    private const int DefaultThumbnailSize = 300;
    private const int JpegQuality = 75;
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;

    private static readonly SKColor PlaceholderBackgroundColor = new(30, 30, 30);
    private static readonly SKColor ErrorColor = SKColors.DarkRed;
    private static readonly SKColor VideoColor = SKColors.DarkSlateBlue;
    private static readonly SKColor GenericColor = SKColors.Gray;

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
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Calculate maximum concurrency scale based on logical processors
        int concurrencyLimit = Math.Max(2, Environment.ProcessorCount / 2);
        _generationSemaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);

        // Automatically purge old, bloated PNG thumbnails to migrate to high-performance JPEGs
        if (Directory.Exists(_cacheDir))
        {
            try
            {
                foreach (string pngFile in Directory.EnumerateFiles(_cacheDir, "*.png"))
                {
                    File.Delete(pngFile);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThumbnailService] Error purging old PNGs: {ex.Message}");
            }
        }

        // Populate the cache with high-performance JPEG thumbnail files from disk
        if (Directory.Exists(_cacheDir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(_cacheDir, "*.jpg"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _thumbnailCache.TryAdd(name, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThumbnailService] Error scanning cache: {ex.Message}");
            }
        }

        string? ffmpegPath = FFmpegLocator.Find();
        _hasFFmpeg = ffmpegPath is not null;

        if (!_hasFFmpeg)
        {
            Console.WriteLine("[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found. Video thumbnails disabled.");
            return;
        }

        string binaryFolder = Path.GetDirectoryName(ffmpegPath)!;
        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return Path.Combine(_cacheDir, $"{key}.jpg");
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return _thumbnailCache.ContainsKey(key);
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultThumbnailSize, CancellationToken cancellationToken = default)
    {
        string key = !string.IsNullOrEmpty(file.Hash) ? file.Hash : file.Id.ToString("N");
        string destPath = GetThumbnailPath(file.Hash, file.Id);

        // Skip immediately if the thumbnail exists or the source file was removed
        if (_thumbnailCache.ContainsKey(key) || !File.Exists(file.Path))
        {
            return;
        }

        // Avoid triggering duplicate concurrent generations for the same file
        if (!_activeGenerations.TryAdd(key, true))
        {
            return;
        }

        lock (_queueLock)
        {
            _queueTotal++;
            QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);
        }

        // Limit concurrent generations globally to preserve system resources
        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(destPath))
            {
                await TryGenerateThumbnailAsync(file.Path, destPath, size, cancellationToken);
            }
            _thumbnailCache.TryAdd(key, true);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            GeneratePlaceholder(destPath, size, "ERR", ErrorColor);
            _thumbnailCache.TryAdd(key, true);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        finally
        {
            _generationSemaphore.Release();
            _activeGenerations.TryRemove(key, out _);

            lock (_queueLock)
            {
                _queueProcessed++;
                QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);

                if (_queueProcessed >= _queueTotal)
                {
                    _queueProcessed = 0;
                    _queueTotal = 0;
                    QueueCompleted?.Invoke();
                }
            }
        }
    }

    private async Task TryGenerateThumbnailAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        bool success = extension switch
        {
            var e when ImageExtensions.Contains(e) => await TryGenerateImageAsync(sourcePath, destPath, size, cancellationToken),
            var e when VideoExtensions.Contains(e) => await TryGenerateVideoAsync(sourcePath, destPath, size, cancellationToken),
            _ => false
        };

        if (!success)
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            GeneratePlaceholder(destPath, size, label, GenericColor);
        }
    }

    private static async Task<bool> TryGenerateImageAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using SKCodec? codec = SKCodec.Create(sourcePath);
                if (codec is null)
                {
                    // Graceful fallback to standard bitmap decoder if SKCodec cannot instantiate
                    using SKBitmap? fallbackBitmap = SKBitmap.Decode(sourcePath);
                    if (fallbackBitmap is null)
                    {
                        return false;
                    }
                    SaveResized(fallbackBitmap, destPath, size);
                    return true;
                }

                float scale = (float)size / Math.Max(codec.Info.Width, codec.Info.Height);
                if (scale < 1.0f)
                {
                    // Compute hardware-accelerated scaled intermediate dimensions (e.g. 1/8 scale)
                    SKSizeI scaledSize = codec.GetScaledDimensions(scale);
                    SKImageInfo nearestInfo = new(
                        scaledSize.Width,
                        scaledSize.Height,
                        codec.Info.ColorType,
                        codec.Info.AlphaType);

                    // Decode only the sub-sampled coefficients into memory to save massive RAM and CPU allocations
                    using SKBitmap? original = SKBitmap.Decode(codec, nearestInfo);
                    if (original is null)
                    {
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
                else
                {
                    // Fallback to standard bitmap decoder if original dimensions are already within target bounds
                    using SKBitmap? original = SKBitmap.Decode(sourcePath);
                    if (original is null)
                    {
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Sub-sampled decode failed for {sourcePath}: {ex.Message}");
                return false;
            }
        }, cancellationToken);
    }

    private async Task<bool> TryGenerateVideoAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        if (!_hasFFmpeg)
        {
            GeneratePlaceholder(destPath, size, "VIDEO", VideoColor);
            return true;
        }

        try
        {
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);
            if (duration is null)
            {
                return false;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            string? tempFramePath = await ExtractSnapshotAsync(sourcePath, captureTime);

            return tempFramePath is not null
                && await ProcessSnapshotAsync(tempFramePath, destPath, size, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Video processing error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", VideoColor);
            return true;
        }
    }

    private static async Task<TimeSpan?> AnalyzeDurationAsync(string sourcePath)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            return analysis.Duration;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan CalculateCaptureTime(TimeSpan duration)
    {
        bool isLongVideo = duration.TotalSeconds > VideoDurationThresholdSeconds;

        double seconds = isLongVideo
            ? VideoEarlyCaptureSeconds
            : duration.TotalSeconds * VideoLateCaptureRatio;

        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<string?> ExtractSnapshotAsync(string sourcePath, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".jpg";

        try
        {
            await FFMpeg.SnapshotAsync(sourcePath, tempPath, null, captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch
        {
            CleanupTempFile(tempPath);
            return null;
        }
    }

    private static async Task<bool> ProcessSnapshotAsync(string tempPath, string destPath, int size, CancellationToken cancellationToken)
    {
        try
        {
            using SKCodec? codec = SKCodec.Create(tempPath);
            if (codec is null)
            {
                using SKBitmap? fallback = SKBitmap.Decode(tempPath);
                if (fallback is null)
                {
                    return false;
                }
                SaveResized(fallback, destPath, size);
                return true;
            }

            float scale = (float)size / Math.Max(codec.Info.Width, codec.Info.Height);
            if (scale < 1.0f)
            {
                SKSizeI scaledSize = codec.GetScaledDimensions(scale);
                SKImageInfo nearestInfo = new(
                    scaledSize.Width,
                    scaledSize.Height,
                    codec.Info.ColorType,
                    codec.Info.AlphaType);

                using SKBitmap? original = SKBitmap.Decode(codec, nearestInfo);
                if (original is null)
                {
                    return false;
                }

                SaveResized(original, destPath, size);
                return true;
            }
            else
            {
                using SKBitmap? original = SKBitmap.Decode(tempPath);
                if (original is null)
                {
                    return false;
                }

                SaveResized(original, destPath, size);
                return true;
            }
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
    }

    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        using SKBitmap square = new(size, size);
        using SKCanvas canvas = new(square);

        canvas.Clear(PlaceholderBackgroundColor);

        double ratio = Math.Min((double)size / original.Width, (double)size / original.Height);
        int drawWidth = (int)(original.Width * ratio);
        int drawHeight = (int)(original.Height * ratio);
        int x = (size - drawWidth) / 2;
        int y = (size - drawHeight) / 2;

        SKImageInfo info = new(drawWidth, drawHeight);
        using SKBitmap? resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest));

        if (resized is null)
        {
            return;
        }

        canvas.DrawBitmap(resized, x, y);
        EncodeAndSave(square, destPath);
    }

    private static void GeneratePlaceholder(string destPath, int size, string text, SKColor backgroundColor)
    {
        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(backgroundColor);

        using SKPaint textPaint = new()
        {
            Color = SKColors.White,
            IsAntialias = true
        };

        float fontSize = size / 4.0f;
        using SKFont font = new()
        {
            Size = fontSize
        };

        float x = size / 2.0f;
        float y = (size / 2.0f) + (fontSize / 2.0f) - 5;

        canvas.DrawText(text, x, y, SKTextAlign.Center, font, textPaint);
        EncodeAndSave(bitmap, destPath);
    }

    private static void EncodeAndSave(SKBitmap bitmap, string destPath)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    private static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Failed to cleanup temp file {path}: {ex.Message}");
        }
    }
}