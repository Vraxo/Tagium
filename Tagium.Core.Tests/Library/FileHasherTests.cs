using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Library;

namespace Tagium.Core.Tests.Library;

public class FileHasherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileHasher _hasher = new(NullLogger<FileHasher>.Instance);

    public FileHasherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tagium-hasher-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the temp directory
        }
    }

    [Fact]
    public async Task ComputeSha256Async_MissingFile_ReturnsNull()
    {
        string? hash = await _hasher.ComputeSha256Async(Path.Combine(_tempDir, "missing.bin"));

        hash!.Should().BeNull();
    }

    [Fact]
    public async Task ComputeSha256Async_SmallFile_ReturnsFullLowercaseHexHash()
    {
        string path = Path.Combine(_tempDir, "hello.txt");
        await File.WriteAllTextAsync(path, "hello world");

        string hash = (await _hasher.ComputeSha256Async(path))!;

        // Well-known SHA-256 of "hello world"
        hash.Should().Be("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9");
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_ReturnsHashOfEmptyContent()
    {
        string path = Path.Combine(_tempDir, "empty.bin");
        await File.WriteAllBytesAsync(path, []);

        string hash = (await _hasher.ComputeSha256Async(path))!;

        // Well-known SHA-256 of the empty input
        hash.Should().Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task ComputeSha256Async_FileAtSizeThreshold_StillUsesFullHash()
    {
        // 256 KiB exactly still takes the full-file path (length <= 256 * 1024)
        byte[] data = CreatePatternedData(256 * 1024);
        string path = Path.Combine(_tempDir, "threshold.bin");
        await File.WriteAllBytesAsync(path, data);

        string hash = (await _hasher.ComputeSha256Async(path))!;

        hash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(data)));
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task ComputeSha256Async_LargeFile_ReturnsLengthPrefixedSampledHash()
    {
        const int size = 300 * 1024; // Above the 256 KiB threshold
        byte[] data = CreatePatternedData(size);
        string path = Path.Combine(_tempDir, "large.bin");
        await File.WriteAllBytesAsync(path, data);

        string hash = (await _hasher.ComputeSha256Async(path))!;

        // Large files are hashed as first 128 KiB + last 128 KiB, prefixed with the hex file length
        byte[] expectedSample = data[..(128 * 1024)].Concat(data[^(128 * 1024)..]).ToArray();
        string expected = $"{size:X}-{Convert.ToHexStringLower(SHA256.HashData(expectedSample))}";

        hash.Should().Be(expected);
        hash.Should().StartWith("4B000-");
        hash.Should().MatchRegex("^[0-9A-F]+-[0-9a-f]{64}$");
    }

    private static byte[] CreatePatternedData(int size)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }
        return data;
    }
}
