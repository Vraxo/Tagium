using FFMpegCore;

namespace Tagium.Core.Services;

public class VideoFrameExtractor
{
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;

    public bool HasFFmpeg { get; }

    public VideoFrameExtractor()
    {
        string? ffmpegPath = FFmpegLocator.Find();
        HasFFmpeg = ffmpegPath is not null;

        if (HasFFmpeg)
        {
            string binaryFolder = Path.GetDirectoryName(ffmpegPath!)!;
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
        }
        else
        {
            Console.WriteLine("[VideoFrameExtractor] WARNING: ffmpeg.exe or ffprobe.exe not found. Video extraction disabled.");
        }
    }

    public async Task<string?> ExtractFrameAsync(string sourcePath)
    {
        try
        {
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);
            if (duration is null)
            {
                return null;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            return await ExtractSnapshotAsync(sourcePath, captureTime);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoFrameExtractor] Video processing error: {ex.Message}");
            return null;
        }
    }

    private static async Task<TimeSpan?> AnalyzeDurationAsync(string sourcePath)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            return analysis.Duration;
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan CalculateCaptureTime(TimeSpan duration)
    {
        bool isLongVideo = duration.TotalSeconds > VideoDurationThresholdSeconds;

        double seconds = isLongVideo
            ? VideoEarlyCaptureSeconds
            : duration.TotalSeconds * VideoLateCaptureRatio;

        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<string?> ExtractSnapshotAsync(string sourcePath, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".jpg";

        try
        {
            await FFMpeg.SnapshotAsync(sourcePath, tempPath, null, captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch
        {
            CleanupTempFile(tempPath);
            return null;
        }
    }

    public static void CleanupTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoFrameExtractor] Failed to cleanup temp file {path}: {ex.Message}");
        }
    }
}