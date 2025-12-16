using Tagium.Core.Helpers;

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
            return Stream.Null; // 404 equivalent in some handlers, or use exception
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        // Use AppDataHelper to find the path directly without instantiating the service
        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId}.png");

        if (!File.Exists(path))
        {
            // IMPORTANT: Returning null or throwing exception signals 404/Error to WebView2
            return null!;
        }

        contentType = "image/png";
        // File.OpenRead throws if locked, but for thumbnails it's usually fine. 
        // Shared Read is safer.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}