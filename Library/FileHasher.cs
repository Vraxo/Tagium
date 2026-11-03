using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public class FileHasher
{
    private readonly ILogger<FileHasher> _logger;

    public FileHasher(ILogger<FileHasher> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ComputeSha256Async(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File does not exist for hashing: {FilePath}", filePath);
            return null;
        }

        using LogTiming timer = LogTiming.StartDebug(_logger, "SHA256 computation for {FilePath}", filePath);

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            long length = stream.Length;

            if (length <= 256 * 1024)
            {
                using SHA256 sha256 = SHA256.Create();
                byte[] hash = await sha256.ComputeHashAsync(stream);
                return Convert.ToHexStringLower(hash);
            }

            byte[] buffer = new byte[256 * 1024];

            int bytesReadStart = await stream.ReadAsync(buffer.AsMemory(0, 128 * 1024));

            stream.Seek(-128 * 1024, SeekOrigin.End);
            int bytesReadEnd = await stream.ReadAsync(buffer.AsMemory(128 * 1024, 128 * 1024));

            using SHA256 sha256Combined = SHA256.Create();
            byte[] hashBytes = sha256Combined.ComputeHash(buffer, 0, bytesReadStart + bytesReadEnd);

            return $"{length:X}-{Convert.ToHexStringLower(hashBytes)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing SHA256 for file: {FilePath}", filePath);
            return null;
        }
    }
}