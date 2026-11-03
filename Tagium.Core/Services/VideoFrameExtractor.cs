using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace Tagium.Core.Services;

public class VideoFrameExtractor
{
    private const int DefaultCaptureSeconds = 3;
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
            TimeSpan captureTime = TimeSpan.FromSeconds(DefaultCaptureSeconds);
            _logger.LogInformation("Extracting snapshot frame for video {Path} at default position {Position}", sourcePath, captureTime);
            return await ExtractSnapshotAsync(sourcePath, captureTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video processing error during frame extraction for file: {Path}", sourcePath);
            return null;
        }
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