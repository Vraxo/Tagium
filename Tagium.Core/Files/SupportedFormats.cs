namespace Tagium.Core.Files;

public enum MediaKind
{
    Image,
    Video,
    Other
}

/// <summary>
/// Single source of truth for the file extensions Tagium indexes,
/// split by media kind so stats and the scanner share one definition.
/// </summary>
public static class SupportedFormats
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase);

    static SupportedFormats()
    {
        foreach (string ext in ImageExtensions)
        {
            All.Add(ext);
        }
        foreach (string ext in VideoExtensions)
        {
            All.Add(ext);
        }
    }

    public static MediaKind GetKind(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return MediaKind.Other;
        }

        if (ImageExtensions.Contains(extension))
        {
            return MediaKind.Image;
        }

        if (VideoExtensions.Contains(extension))
        {
            return MediaKind.Video;
        }

        return MediaKind.Other;
    }
}
