using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static IServiceProvider? ServiceProvider { get; set; }

    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        if (uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return HandleThumbnail(uri, out contentType);
        }

        return uri.Host.Equals("preview", StringComparison.OrdinalIgnoreCase) ? HandlePreview(uri, out contentType) : Stream.Null;
    }

    private static Stream HandleThumbnail(Uri uri, out string contentType)
    {
        contentType = "image/png";

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId:N}.png");

        return !File.Exists(path) ? null! : (Stream)new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static Stream HandlePreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        var fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
        FileEntry? entry = fileRepo.GetById(fileId);

        if (entry is null || !File.Exists(entry.Path))
        {
            return null!;
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
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }
}