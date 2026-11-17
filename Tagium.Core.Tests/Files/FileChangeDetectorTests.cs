using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class FileChangeDetectorTests
{
    private static FileEntry Entry(long size, long modified, bool isMissing = false) => new()
    {
        Id = Guid.NewGuid(),
        Path = "/lib/a.jpg",
        Name = "a.jpg",
        Size = size,
        ModifiedTime = modified,
        IsMissing = isMissing
    };

    [Fact]
    public void IsUnchanged_WhenSizeAndTimestampMatch_IsTrue()
    {
        FileEntry entry = Entry(1024, 1_700_000_000);

        FileChangeDetector.IsUnchanged(entry, new FileIdentity(1024, 1_700_000_000)).Should().BeTrue();
    }

    [Fact]
    public void IsUnchanged_WhenSizeDiffers_IsFalse()
    {
        FileEntry entry = Entry(1024, 1_700_000_000);

        FileChangeDetector.IsUnchanged(entry, new FileIdentity(2048, 1_700_000_000)).Should().BeFalse();
    }

    [Fact]
    public void IsUnchanged_WhenTimestampDiffers_IsFalse()
    {
        // Same size, different mtime: an in-place edit that did not change the length.
        FileEntry entry = Entry(1024, 1_700_000_000);

        FileChangeDetector.IsUnchanged(entry, new FileIdentity(1024, 1_700_000_001)).Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void IsUnchanged_OnDegenerateValues_StillComparesBoth(long size, long modified)
    {
        FileEntry entry = Entry(size, modified);

        FileChangeDetector.IsUnchanged(entry, new FileIdentity(size, modified)).Should().BeTrue();
        FileChangeDetector.IsUnchanged(entry, new FileIdentity(size, modified + 1)).Should().BeFalse();
    }

    [Fact]
    public void IsReturned_RequiresUnchangedContentAndMissingFlag()
    {
        FileIdentity identity = new(1024, 1_700_000_000);

        FileChangeDetector.IsReturned(Entry(1024, 1_700_000_000, isMissing: true), identity).Should().BeTrue();
        // Not missing: it was never gone, so nothing "returned".
        FileChangeDetector.IsReturned(Entry(1024, 1_700_000_000), identity).Should().BeFalse();
        // Missing but changed: this is an edit, not a return.
        FileChangeDetector.IsReturned(Entry(999, 1_700_000_000, isMissing: true), identity).Should().BeFalse();
    }
}

public class FileIdentityTests
{
    [Fact]
    public void Read_ReportsSizeAndTimestampInUnixSeconds()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[42]);
            DateTime utc = new(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, utc);

            FileIdentity identity = FileIdentity.Read(path);

            identity.Size.Should().Be(42);
            identity.ModifiedTimeUtc.Should().Be(new DateTimeOffset(utc).ToUnixTimeSeconds());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNullInsteadOfThrowing()
    {
        FileIdentity.TryRead(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.jpg"))
            .Should().BeNull();
    }

    [Fact]
    public void TryRead_ExistingFile_MatchesRead()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[7]);

            FileIdentity.TryRead(path).Should().Be(FileIdentity.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Read_MissingFile_Throws()
    {
        // The scan path relies on its own try/catch to log this, so the throwing behaviour is
        // part of the contract and must not be quietly turned into a null.
        Action act = () => FileIdentity.Read(
            Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.jpg"));

        act.Should().Throw<Exception>();
    }
}
