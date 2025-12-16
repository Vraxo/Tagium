using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public static class FileHasher
{
    public static string? ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileHasher] Error: {ex.Message}");
            return null;
        }
    }
}