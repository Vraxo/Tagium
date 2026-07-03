using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Tagium.Core.Helpers;

namespace Tagium.Core.Services;

public class ThumbnailCache
{
    private readonly ConcurrentDictionary<string, bool> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ThumbnailCache> _logger;

    public string CacheDir { get; }

    public ThumbnailCache(ILogger<ThumbnailCache> logger)
    {
        _logger = logger;
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
                _logger.LogInformation("Purged legacy PNG thumbnails from cache directory");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purging old PNG thumbnails from cache");
            }
        }

        // Populate the cache with high-performance JPEG thumbnail files from disk
        if (Directory.Exists(CacheDir))
        {
            try
            {
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(CacheDir, "*.jpg"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _thumbnailCache.TryAdd(name, true);
                    count++;
                }
                _logger.LogInformation("Successfully indexed {Count} JPEG thumbnails in cache", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning and populating thumbnail cache from disk");
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