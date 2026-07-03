using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Tagium.Core.Services;

public class ThumbnailGenerator
{
    private const int JpegQuality = 75;
    private readonly ILogger<ThumbnailGenerator> _logger;

    private static readonly SKColor PlaceholderBackgroundColor = new(30, 30, 30);
    public static readonly SKColor ErrorColor = SKColors.DarkRed;
    public static readonly SKColor VideoColor = SKColors.DarkSlateBlue;
    public static readonly SKColor GenericColor = SKColors.Gray;

    public ThumbnailGenerator(ILogger<ThumbnailGenerator> logger)
    {
        _logger = logger;
    }

    public async Task<bool> GenerateImageThumbnailAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using SKCodec? codec = SKCodec.Create(sourcePath);
                if (codec is null)
                {
                    using SKBitmap? fallbackBitmap = SKBitmap.Decode(sourcePath);
                    if (fallbackBitmap is null)
                    {
                        _logger.LogWarning("Failed to decode image using fallback decoder for path: {Path}", sourcePath);
                        return false;
                    }
                    SaveResized(fallbackBitmap, destPath, size);
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
                        _logger.LogWarning("Failed to decode image with sub-sampling for path: {Path}", sourcePath);
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
                else
                {
                    using SKBitmap? original = SKBitmap.Decode(sourcePath);
                    if (original is null)
                    {
                        _logger.LogWarning("Failed to decode full image for path: {Path}", sourcePath);
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sub-sampled decode failed for: {Path}", sourcePath);
                return false;
            }
        }, cancellationToken);
    }

    public void GeneratePlaceholder(string destPath, int size, string text, SKColor backgroundColor)
    {
        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating placeholder thumbnail for path: {Path}", destPath);
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

    private static void EncodeAndSave(SKBitmap bitmap, string destPath)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }
}