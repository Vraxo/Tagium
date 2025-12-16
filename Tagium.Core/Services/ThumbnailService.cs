using SkiaSharp;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ThumbnailService
{
    private readonly string _cacheDir;

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
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

        // Run on thread pool to avoid blocking UI
        await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(file.Path))
                {
                    return;
                }

                using SKBitmap? original = SKBitmap.Decode(file.Path);

                if (original is null)
                {
                    return;
                }

                // Resize
                SKImageInfo info = new(size, size);
                using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);

                if (resized is null)
                {
                    return;
                }

                using SKImage image = SKImage.FromBitmap(resized);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

                using FileStream stream = File.OpenWrite(destPath);
                data.SaveTo(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Error generating for {file.Path}: {ex.Message}");
            }
        });
    }
}