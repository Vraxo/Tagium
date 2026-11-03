namespace Tagium.Core.Services;

internal static class FFmpegLocator
{
    public static string? Find()
    {
        string? ffmpegPath = FindInAppDirectory() ?? FindInPath();

        return ffmpegPath is not null && HasCompanionFfprobe(ffmpegPath)
            ? ffmpegPath : null;
    }

    private static string? FindInAppDirectory()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        return File.Exists(ffmpeg)
            ? ffmpeg
            : null;
    }

    private static string? FindInPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string dir in paths)
        {
            string candidate = Path.Combine(dir, "ffmpeg.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasCompanionFfprobe(string ffmpegPath)
    {
        string dir = Path.GetDirectoryName(ffmpegPath)!;
        string ffprobe = Path.Combine(dir, "ffprobe.exe");

        return File.Exists(ffprobe);
    }
}