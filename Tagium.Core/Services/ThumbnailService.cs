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

    private readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".webp"
    };

    private readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
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

        // Check for FFmpeg binaries
        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

        _hasFFmpeg = File.Exists(ffmpegPath) && File.Exists(ffprobePath);

        if (!_hasFFmpeg)
        {
            Console.WriteLine(
                "[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found." +
                "Video thumbnails will use placeholders.");
            return;
        }

        GlobalFFOptions.Configure(new FFOptions { BinaryFolder = AppContext.BaseDirectory });
    }

    public string GetThumbnailPath(long fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId}.png");
    }

    public bool HasThumbnail(long fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    [Obsolete]
    public async Task GenerateThumbnailAsync(FileEntry file, int size = 128)
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

        string ext = Path.GetExtension(file.Path);

        await Task.Run(async () =>
        {
            try
            {
                if (_imageExtensions.Contains(ext))
                {
                    GenerateImageThumbnail(file.Path, destPath, size);
                }
                else if (_videoExtensions.Contains(ext))
                {
                    if (_hasFFmpeg)
                    {
                        await GenerateVideoThumbnailAsync(file.Path, destPath, size);
                    }
                    else
                    {
                        GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
                    }
                }
                else
                {
                    GeneratePlaceholder(destPath, size, ext.TrimStart('.').ToUpper(), SKColors.Gray);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Error for {file.Path}: {ex.Message}");
                GeneratePlaceholder(destPath, size, "ERR", SKColors.DarkRed);
            }
        });
    }

    [Obsolete]
    private static void GenerateImageThumbnail(string sourcePath, string destPath, int size)
    {
        using SKBitmap? original = SKBitmap.Decode(sourcePath);
        if (original is null)
        {
            return;
        }

        SaveResized(original, destPath, size);
    }

    [Obsolete]
    private static async Task GenerateVideoThumbnailAsync(string sourcePath, string destPath, int size)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            TimeSpan duration = analysis.Duration;

            TimeSpan captureTime = duration.TotalSeconds > 10
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(duration.TotalSeconds * 0.1);

            string tempFrame = Path.GetTempFileName() + ".png";

            _ = await FFMpeg.SnapshotAsync(sourcePath, tempFrame, new Size(size, size), captureTime);

            if (!File.Exists(tempFrame))
            {
                return;
            }

            using SKBitmap? original = SKBitmap.Decode(tempFrame);

            if (original != null)
            {
                SaveResized(original, destPath, size);
            }

            File.Delete(tempFrame);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] FFMpeg error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
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
            TextSize = size / 4,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        float x = size / 2;
        float y = (size / 2) + (textPaint.TextSize / 2) - 5;

        canvas.DrawText(text, x, y, textPaint);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    [Obsolete]
    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        float ratio = (float)Math.Min((double)size / original.Width, (double)size / original.Height);
        int w = (int)(original.Width * ratio);
        int h = (int)(original.Height * ratio);

        SKImageInfo info = new(w, h);
        using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

        if (resized is null)
        {
            return;
        }

        using SKImage image = SKImage.FromBitmap(resized);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);

        data.SaveTo(stream);
    }
}