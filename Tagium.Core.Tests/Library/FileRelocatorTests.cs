using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Files;
using Tagium.Core.Library;

namespace Tagium.Core.Tests.Library;

/// <summary>
/// Covers the matching rules only. The persistence call is a single <c>_fileRepo.Update</c> that
/// needs a live database, so these tests assert the entry's state after relocation rather than
/// the write — the write is one line and has no branches.
/// </summary>
public class FileRelocatorTests
{
    private static string Sha256Hex(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static FileRelocator Create() => new(
        new FileHasher(NullLogger<FileHasher>.Instance),
        NullLogger<FileRelocator>.Instance);

    [Fact]
    public async Task Relocate_RePointsAnEntryWhenSizeTimestampAndHashAllMatch()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-relocate-").FullName;
        try
        {
            string newPath = Path.Combine(dir, "new-name.jpg");
            File.WriteAllText(newPath, "content");
            FileIdentity identity = FileIdentity.Read(newPath);

            FileEntry missing = new()
            {
                Id = Guid.NewGuid(),
                Path = Path.Combine(dir, "old-name.jpg"),
                Name = "old-name.jpg",
                Hash = Sha256Hex("content"),
                Size = identity.Size,
                ModifiedTime = identity.ModifiedTimeUtc,
                IsMissing = true
            };

            HashSet<string> relocated = [];
            int progress = 0;

            IReadOnlyList<FileEntry> count = await Create().RelocateAsync(
                [missing], [newPath], relocated, _ => progress++);

            count.Should().HaveCount(1);
            missing.Path.Should().Be(newPath);
            missing.Name.Should().Be("new-name.jpg");
            missing.IsMissing.Should().BeFalse();
            relocated.Should().Contain(newPath);
            progress.Should().Be(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Relocate_LeavesTheEntryWhenContentDiffers()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-relocate-").FullName;
        try
        {
            string newPath = Path.Combine(dir, "other.jpg");
            File.WriteAllText(newPath, "different");
            FileIdentity identity = FileIdentity.Read(newPath);

            FileEntry missing = new()
            {
                Id = Guid.NewGuid(),
                Path = Path.Combine(dir, "old.jpg"),
                Name = "old.jpg",
                // Same size and timestamp, different content: only the hash can catch this.
                Hash = Sha256Hex("original-content"),
                Size = identity.Size,
                ModifiedTime = identity.ModifiedTimeUtc,
                IsMissing = true
            };

            HashSet<string> relocated = [];
            int progress = 0;

            IReadOnlyList<FileEntry> count = await Create().RelocateAsync(
                [missing], [newPath], relocated, _ => progress++);

            count.Should().BeEmpty();
            missing.IsMissing.Should().BeTrue();
            missing.Path.Should().Be(Path.Combine(dir, "old.jpg"));
            relocated.Should().BeEmpty();
            progress.Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Relocate_IgnoresCandidatesWithADifferentTimestamp()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-relocate-").FullName;
        try
        {
            string newPath = Path.Combine(dir, "touched.jpg");
            File.WriteAllText(newPath, "content");
            FileIdentity identity = FileIdentity.Read(newPath);

            FileEntry missing = new()
            {
                Id = Guid.NewGuid(),
                Path = "/lib/old.jpg",
                Name = "old.jpg",
                Hash = Sha256Hex("content"),
                Size = identity.Size,
                ModifiedTime = identity.ModifiedTimeUtc + 1,
                IsMissing = true
            };

            IReadOnlyList<FileEntry> count = await Create().RelocateAsync(
                [missing], [newPath], new HashSet<string>(), _ => { });

            count.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Relocate_SkipsEntriesWithNoStoredHash()
    {
        FileEntry missing = new()
        {
            Id = Guid.NewGuid(),
            Path = "/lib/old.jpg",
            Name = "old.jpg",
            Hash = null,
            Size = 3,
            ModifiedTime = 1,
            IsMissing = true
        };

        int progress = 0;
        IReadOnlyList<FileEntry> count = await Create().RelocateAsync(
            [missing], ["/lib/candidate.jpg"], new HashSet<string>(), _ => progress++);

        count.Should().BeEmpty();
        progress.Should().Be(0);
    }

    [Fact]
    public async Task Relocate_LetsOnlyOneEntryClaimAMovedFile()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-relocate-").FullName;
        try
        {
            string newPath = Path.Combine(dir, "shared.jpg");
            File.WriteAllText(newPath, "shared");
            FileIdentity identity = FileIdentity.Read(newPath);
            string hash = Sha256Hex("shared");

            FileEntry Make() => new()
            {
                Id = Guid.NewGuid(),
                Path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jpg"),
                Name = "x.jpg",
                Hash = hash,
                Size = identity.Size,
                ModifiedTime = identity.ModifiedTimeUtc,
                IsMissing = true
            };

            FileEntry a = Make();
            FileEntry b = Make();

            IReadOnlyList<FileEntry> count = await Create().RelocateAsync([a, b], [newPath], new HashSet<string>(), _ => { });

            count.Should().HaveCount(1);
            // Two entries must not end up pointing at the same path: Path is UNIQUE in the cache.
            (a.IsMissing ^ b.IsMissing).Should().BeTrue("exactly one entry may claim the moved file");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Relocate_EmptyInputsDoNothing()
    {
        IReadOnlyList<FileEntry> count = await Create().RelocateAsync([], [], new HashSet<string>(), _ => { });

        count.Should().BeEmpty();
    }
}
