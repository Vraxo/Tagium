using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tagium.Core.Library;

namespace Tagium.Core.Tests.Library;

public class FileScannerTests
{
    private static FileScanner Create() => new(NullLogger<FileScanner>.Instance);

    private static LibraryRoot Root(string path, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        Path = path,
        IsEnabled = enabled
    };

    [Fact]
    public void TopLevelRoots_DropsDisabledRoots()
    {
        List<LibraryRoot> roots = [Root("/lib/a"), Root("/lib/b", enabled: false)];

        Create().TopLevelRoots(roots).Select(r => r.Path).Should().Equal("/lib/a");
    }

    [Fact]
    public void TopLevelRoots_DropsRootsNestedInsideAnotherRoot()
    {
        // A root configured inside another would otherwise yield every file twice in one scan.
        List<LibraryRoot> roots = [Root("/lib"), Root("/lib/photos"), Root("/lib/photos/2024")];

        Create().TopLevelRoots(roots).Select(r => r.Path).Should().Equal("/lib");
    }

    [Fact]
    public void TopLevelRoots_KeepsSiblingRoots()
    {
        List<LibraryRoot> roots = [Root("/lib/photos"), Root("/lib/videos")];

        Create().TopLevelRoots(roots).Select(r => r.Path)
            .Should().BeEquivalentTo(new[] { "/lib/photos", "/lib/videos" });
    }

    [Fact]
    public void TopLevelRoots_DoesNotTreatALongerSiblingAsNested()
    {
        // "/photos2" starts with "/photos" but is not inside it; pruning it would silently
        // drop a whole library from the scan.
        List<LibraryRoot> roots = [Root("/photos"), Root("/photos2")];

        Create().TopLevelRoots(roots).Select(r => r.Path)
            .Should().BeEquivalentTo(new[] { "/photos", "/photos2" });
    }

    [Fact]
    public void TopLevelRoots_IsCaseInsensitive()
    {
        List<LibraryRoot> roots = [Root("/Lib"), Root("/lib/photos")];

        Create().TopLevelRoots(roots).Should().HaveCount(1);
    }

    [Fact]
    public void DiscoverFiles_ReturnsSupportedFilesOnly_Recursively()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-scan-").FullName;
        try
        {
            string nested = Path.Combine(dir, "nested");
            _ = Directory.CreateDirectory(nested);

            File.WriteAllText(Path.Combine(dir, "a.jpg"), "x");
            File.WriteAllText(Path.Combine(dir, "b.MP4"), "x");   // case must not matter
            File.WriteAllText(Path.Combine(nested, "c.png"), "x");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "x"); // unsupported
            File.WriteAllText(Path.Combine(dir, "noext"), "x");     // unsupported

            List<string> files = Create().DiscoverFiles([Root(dir)]);

            files.Select(Path.GetFileName).Should().BeEquivalentTo(new[] { "a.jpg", "b.MP4", "c.png" });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverFiles_MissingRoot_IsSkippedWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}");

        Create().DiscoverFiles([Root(missing)]).Should().BeEmpty();
    }

    [Fact]
    public void DiscoverFiles_SkipsFilesUnderANestedRootTwice()
    {
        string dir = Directory.CreateTempSubdirectory("tagium-scan-").FullName;
        try
        {
            string nested = Path.Combine(dir, "nested");
            _ = Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "a.jpg"), "x");

            // Both roots point at overlapping trees; the file must appear once.
            List<string> files = Create().DiscoverFiles([Root(dir), Root(nested)]);

            files.Should().HaveCount(1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TopLevelRoots_NullArgument_Throws()
    {
        Action act = () => Create().TopLevelRoots(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
