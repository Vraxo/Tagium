using FFMpegCore;
using SkiaSharp;
using System.Drawing;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;

    private const int DefaultSize = 128;
    private const int PngQuality = 85;
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".mkv",
        ".mov",
        ".avi",
        ".wmv",
        ".webm"
    };

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

        _hasFFmpeg = File.Exists(ffmpegPath) && File.Exists(ffprobePath);

        if (!_hasFFmpeg)
        {
            Console.WriteLine("[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found. Video thumbnails disabled.");
            return;
        }

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = AppContext.BaseDirectory });
    }

    public string GetThumbnailPath(Guid fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId:N}.png");
    }

    public bool HasThumbnail(Guid fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultSize)
    {
        string destPath = GetThumbnailPath(file.Id);

        if (File.Exists(destPath))
        {
            return;
        }

        if (!File.Exists(file.Path))
        {
            return;
        }

        try
        {
            await TryGenerateThumbnailAsync(file.Path, destPath, size);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            GeneratePlaceholder(destPath, size, "ERR", SKColors.DarkRed);
        }
    }

    private static async Task TryGenerateThumbnailAsync(string sourcePath, string destPath, int size)
    {
        string extension = Path.GetExtension(sourcePath);

        bool success = extension.ToLowerInvariant() switch
        {
            var e when ImageExtensions.Contains(e) => await TryGenerateImageAsync(sourcePath, destPath, size),
            var e when VideoExtensions.Contains(e) => await TryGenerateVideoAsync(sourcePath, destPath, size),
            _ => false
        };

        if (!success)
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            GeneratePlaceholder(destPath, size, label, SKColors.Gray);
        }
    }

    private static async Task<bool> TryGenerateImageAsync(string sourcePath, string destPath, int size)
    {
        using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(sourcePath));

        if (original is null)
        {
            return false;
        }

        SaveResized(original, destPath, size);
        return true;
    }

    private static async Task<bool> TryGenerateVideoAsync(string sourcePath, string destPath, int size)
    {
        try
        {
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);

            if (duration is null)
            {
                return false;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            string? tempFramePath = await ExtractSnapshotAsync(sourcePath, size, captureTime);

            return tempFramePath is not null && await ProcessSnapshotAsync(tempFramePath, destPath, size);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] Video processing error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
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

    private static async Task<string?> ExtractSnapshotAsync(string sourcePath, int size, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".png";

        try
        {
            _ = await FFMpeg.SnapshotAsync(sourcePath, tempPath, new Size(size, size), captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch
        {
            CleanupTempFile(tempPath);
            return null;
        }
    }

    private static async Task<bool> ProcessSnapshotAsync(string tempPath, string destPath, int size)
    {
        try
        {
            using SKBitmap? original = await Task.Run(() => SKBitmap.Decode(tempPath));

            if (original is null)
            {
                return false;
            }

            SaveResized(original, destPath, size);
            return true;
        }
        finally
        {
            CleanupTempFile(tempPath);
        }
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

    [Obsolete]
    private static void GeneratePlaceholder(string destPath, int size, string text, SKColor color)
    {
        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(color);

        using SKPaint textPaint = new()
        {
            Color = SKColors.White,
            TextSize = size / 4.0f,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        float x = size / 2.0f;
        float y = (size / 2.0f) + (textPaint.TextSize / 2.0f) - 5;

        canvas.DrawText(text, x, y, textPaint);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, PngQuality);
        using FileStream stream = File.OpenWrite(destPath);

        data.SaveTo(stream);
    }

    [Obsolete]
    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        double ratio = Math.Min((double)size / original.Width, (double)size / original.Height);
        int width = (int)(original.Width * ratio);
        int height = (int)(original.Height * ratio);

        SKImageInfo info = new(width, height);
        using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

        if (resized is null)
        {
            return;
        }

        using SKImage image = SKImage.FromBitmap(resized);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, PngQuality);
        using FileStream stream = File.OpenWrite(destPath);

        data.SaveTo(stream);
    }
}