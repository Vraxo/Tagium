using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public static class FileHasher
{
    public static async Task<string?> ComputeSha256Async(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = await sha256.ComputeHashAsync(stream);

            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileHasher] Error: {ex.Message}");
            return null;
        }
    }
}