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
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId}.png");

        if (!File.Exists(path))
        {
            // Returning null here signals a "Not Found" to WebView2 in some contexts,
            // or we can return a 404 stream if the wrapper supports it. 
            // Photino: returning null usually results in an empty response or 404.
            return null!;
        }

        contentType = "image/png";
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}