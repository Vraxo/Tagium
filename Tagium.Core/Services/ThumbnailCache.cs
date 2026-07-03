using System.Collections.Concurrent;
using Tagium.Core.Helpers;

namespace Tagium.Core.Services;

public class ThumbnailCache
{
    private readonly ConcurrentDictionary<string, bool> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    public string CacheDir { get; }

    public ThumbnailCache()
    {
        CacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Automatically purge old, bloated PNG thumbnails to migrate to high-performance JPEGs
        if (Directory.Exists(CacheDir))
        {
            try
            {
                foreach (string pngFile in Directory.EnumerateFiles(CacheDir, "*.png"))
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
        if (Directory.Exists(CacheDir))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(CacheDir, "*.jpg"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _thumbnailCache.TryAdd(name, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ThumbnailCache] Error scanning cache: {ex.Message}");
            }
        }
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return Path.Combine(CacheDir, $"{key}.jpg");
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return _thumbnailCache.ContainsKey(key);
    }

    public void AddToCache(string key)
    {
        _thumbnailCache.TryAdd(key, true);
    }
}