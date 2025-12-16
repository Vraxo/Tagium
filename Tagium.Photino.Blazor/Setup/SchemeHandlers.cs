using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        // Format: tagium://thumbnail/{fileId}
        if (!uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return new MemoryStream();
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return new MemoryStream();
        }
        // We need to resolve the service from the app container. 
        // Since this handler is static and outside DI scope, we rely on the fact 
        // that Photino doesn't easily expose the provider here. 
        // However, we can access singleton services if we store the provider in Program. 
        // For simplified migration, we will instantiate a new ThumbnailService helper or 
        // assume the cache path logic is static/safe.

        // Better approach: Re-instantiate the path helper since it's stateless file IO.
        ThumbnailService thumbnailService = new();
        string path = thumbnailService.GetThumbnailPath(fileId);

        if (!File.Exists(path))
        {
            return new MemoryStream();
        }

        contentType = "image/png";
        return File.OpenRead(path);
    }
}