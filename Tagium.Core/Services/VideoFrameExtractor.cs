using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace Tagium.Core.Services;

public class VideoFrameExtractor
{
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;
    private readonly ILogger<VideoFrameExtractor> _logger;

    public bool HasFFmpeg { get; }

    public VideoFrameExtractor(ILogger<VideoFrameExtractor> logger)
    {
        _logger = logger;
        string? ffmpegPath = FFmpegLocator.Find();
        HasFFmpeg = ffmpegPath is not null;

        if (HasFFmpeg)
        {
            string binaryFolder = Path.GetDirectoryName(ffmpegPath!)!;
            _logger.LogInformation("FFmpeg companion binary folder discovered at: {Path}", binaryFolder);
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
        }
        else
        {
            _logger.LogWarning("ffmpeg.exe or ffprobe.exe not found in app paths. Video snapshot extraction is disabled");
        }
    }

    public async Task<string?> ExtractFrameAsync(string sourcePath)
    {
        try
        {
            _logger.LogDebug("Analyzing duration for video frame extraction: {Path}", sourcePath);
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);
            if (duration is null)
            {
                _logger.LogWarning("Unable to read video length duration for: {Path}", sourcePath);
                return null;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            _logger.LogInformation("Extracting snapshot frame for video {Path} at position {Position}", sourcePath, captureTime);
            return await ExtractSnapshotAsync(sourcePath, captureTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video processing error during frame extraction for file: {Path}", sourcePath);
            return null;
        }
    }

    private async Task<TimeSpan?> AnalyzeDurationAsync(string sourcePath)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            return analysis.Duration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFProbe duration analysis failed for file: {Path}", sourcePath);
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

    private async Task<string?> ExtractSnapshotAsync(string sourcePath, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".jpg";

        try
        {
            await FFMpeg.SnapshotAsync(sourcePath, tempPath, null, captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFMpeg snapshot operation failed for: {Path}", sourcePath);
            CleanupTempFile(tempPath);
            return null;
        }
    }

    public void CleanupTempFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        const int maxRetries = 3;
        const int delayMs = 100;

        for (int i = 1; i <= maxRetries; i++)
        {
            try
            {
                File.Delete(path);
                _logger.LogDebug("Successfully cleaned up temporary video thumbnail file: {Path}", path);
                return;
            }
            catch (IOException ex) when (i < maxRetries)
            {
                _logger.LogDebug("Antivirus/OS file lock detected on {Path}. Retrying cleanup (Attempt {Current}/{Max})... Reason: {Reason}", path, i, maxRetries, ex.Message);
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temporary video thumbnail file {Path}", path);
                break;
            }
        }
    }
}