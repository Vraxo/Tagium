using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class DuplicateGrouperTests
{
    private static FileEntry File(string path, string? hash, long size = 1024)
    {
        return new FileEntry
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = Path.GetFileName(path),
            Hash = hash,
            Size = size
        };
    }

    [Fact]
    public void Group_FilesWithUniqueHashes_ReturnsNoGroups()
    {
        List<FileEntry> files =
        [
            File("/a/one.jpg", "aaa"),
            File("/b/two.jpg", "bbb"),
            File("/c/three.jpg", "ccc")
        ];

        DuplicateGrouper.Group(files).Should().BeEmpty();
    }

    [Fact]
    public void Group_FilesSharingHash_ReturnsSingleGroup()
    {
        List<FileEntry> files =
        [
            File("/a/one.jpg", "same-hash"),
            File("/b/copy.jpg", "same-hash"),
            File("/c/unique.jpg", "other-hash")
        ];

        List<List<FileEntry>> groups = DuplicateGrouper.Group(files);

        groups.Should().ContainSingle();
        groups[0].Should().HaveCount(2);
        groups[0].Select(f => f.Path).Should().Equal("/a/one.jpg", "/b/copy.jpg");
    }

    [Fact]
    public void Group_SkipsFilesWithoutHash()
    {
        List<FileEntry> files =
        [
            File("/a/nohash1.jpg", null),
            File("/b/nohash2.jpg", ""),
            File("/c/one.jpg", "aaa"),
            File("/d/two.jpg", "aaa")
        ];

        List<List<FileEntry>> groups = DuplicateGrouper.Group(files);

        groups.Should().ContainSingle();
        groups[0].Should().OnlyContain(f => f.Hash == "aaa");
    }

    [Fact]
    public void Group_MultipleGroups_AreOrderedByDescendingSize()
    {
        List<FileEntry> files =
        [
            File("/a/1.jpg", "h1"),
            File("/b/2.jpg", "h1"),
            File("/c/3.jpg", "h2"),
            File("/d/4.jpg", "h2"),
            File("/e/5.jpg", "h2")
        ];

        List<List<FileEntry>> groups = DuplicateGrouper.Group(files);

        groups.Should().HaveCount(2);
        groups[0].Should().HaveCount(3);
        groups[0][0].Hash.Should().Be("h2");
        groups[1].Should().HaveCount(2);
        groups[1][0].Hash.Should().Be("h1");
    }

    [Fact]
    public void Group_WithinGroup_FilesAreOrderedByPath()
    {
        List<FileEntry> files =
        [
            File("/z/last.jpg", "h"),
            File("/m/mid.jpg", "h"),
            File("/a/first.jpg", "h")
        ];

        List<List<FileEntry>> groups = DuplicateGrouper.Group(files);

        groups.Should().ContainSingle();
        groups[0].Select(f => f.Name).Should().Equal("first.jpg", "mid.jpg", "last.jpg");
    }

    [Fact]
    public void Group_EmptyInput_ReturnsEmpty()
    {
        DuplicateGrouper.Group([]).Should().BeEmpty();
    }
}
