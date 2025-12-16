using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    // We need access to the repository to look up file paths by ID.
    // Since this is a static callback, we need a reference to the ServiceProvider.
    public static IServiceProvider? ServiceProvider { get; set; }

    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        // 1. Thumbnail Handler
        if (uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return HandleThumbnail(uri, out contentType);
        }

        // 2. Preview Handler
        return uri.Host.Equals("preview", StringComparison.OrdinalIgnoreCase) ? HandlePreview(uri, out contentType) : Stream.Null;
    }

    private static Stream HandleThumbnail(Uri uri, out string contentType)
    {
        contentType = "image/png";

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId}.png");

        if (!File.Exists(path))
        {
            return null!; // 404
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static Stream HandlePreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";

        if (ServiceProvider == null)
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        // Resolve the file path from the database
        var fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
        FileEntry? entry = fileRepo.GetById(fileId);

        if (entry == null || !File.Exists(entry.Path))
        {
            return null!; // 404
        }

        contentType = GetMimeType(Path.GetExtension(entry.Path));
        return new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska", // Browsers struggle with this, but WebView might try
            _ => "application/octet-stream"
        };
    }
}