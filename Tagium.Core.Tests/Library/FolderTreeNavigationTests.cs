using AwesomeAssertions;
using Tagium.Core.Library;

namespace Tagium.Core.Tests.Library;

public class FolderTreeNavigationTests
{
    private static FolderNode Folder(string path, bool expanded = false, params FolderNode[] children)
    {
        return new FolderNode
        {
            Path = path,
            Name = Path.GetFileName(path),
            IsExpanded = expanded,
            Children = [.. children]
        };
    }

    [Fact]
    public void Flatten_CollapsedRoots_ReturnsOnlyRoots()
    {
        List<FolderNode> roots = [Folder("/library/photos"), Folder("/library/art")];

        IReadOnlyList<FolderRow> rows = FolderTreeNavigation.Flatten(roots);

        rows.Select(r => r.Path).Should().Equal("/library/photos", "/library/art");
        rows.Should().OnlyContain(r => r.Level == 1);
    }

    [Fact]
    public void Flatten_ExpandedRoot_IncludesChildrenAtLevelTwo()
    {
        FolderNode photos = Folder("/library/photos", expanded: true,
            Folder("/library/photos/2024"),
            Folder("/library/photos/2025"));

        IReadOnlyList<FolderRow> rows = FolderTreeNavigation.Flatten([photos]);

        rows.Select(r => r.Path).Should().Equal(
            "/library/photos",
            "/library/photos/2024",
            "/library/photos/2025");
        rows.Select(r => r.Level).Should().Equal(1, 2, 2);
    }

    [Fact]
    public void Flatten_CollapsedChild_HidesItsOwnChildren()
    {
        FolderNode photos = Folder("/library/photos", expanded: true,
            Folder("/library/photos/2024", expanded: false,
                Folder("/library/photos/2024/raw")));

        IReadOnlyList<FolderRow> rows = FolderTreeNavigation.Flatten([photos]);

        rows.Select(r => r.Path).Should().Equal("/library/photos", "/library/photos/2024");
    }

    [Fact]
    public void Flatten_ExpandedGrandchild_ReportsLevelThree()
    {
        FolderNode photos = Folder("/library/photos", expanded: true,
            Folder("/library/photos/2024", expanded: true,
                Folder("/library/photos/2024/raw")));

        IReadOnlyList<FolderRow> rows = FolderTreeNavigation.Flatten([photos]);

        rows.Select(r => r.Path).Should().Equal(
            "/library/photos",
            "/library/photos/2024",
            "/library/photos/2024/raw");
        rows[^1].Level.Should().Be(3);
    }

    [Fact]
    public void Flatten_EmptyLibrary_ReturnsNoRows()
    {
        FolderTreeNavigation.Flatten([]).Should().BeEmpty();
    }

    [Fact]
    public void Flatten_NullRoots_Throws()
    {
        Action act = () => FolderTreeNavigation.Flatten(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Neighbour_MovingDownAndUp_WalksVisibleOrder()
    {
        IReadOnlyList<FolderRow> rows = FlattenSample();

        FolderTreeNavigation.Neighbour(rows, "/library/photos", 1)!.Path
            .Should().Be("/library/photos/2024");
        FolderTreeNavigation.Neighbour(rows, "/library/photos/2024", 1)!.Path
            .Should().Be("/library/art");
        FolderTreeNavigation.Neighbour(rows, "/library/art", -1)!.Path
            .Should().Be("/library/photos/2024");
    }

    [Theory]
    [InlineData("/library/photos", -1)]
    [InlineData("/library/art", 1)]
    public void Neighbour_AtEitherEdge_ReturnsNull(string path, int offset)
    {
        FolderTreeNavigation.Neighbour(FlattenSample(), path, offset).Should().BeNull();
    }

    [Fact]
    public void Neighbour_PathThatIsNotVisible_ReturnsNull()
    {
        FolderTreeNavigation.Neighbour(FlattenSample(), "/library/missing", 1).Should().BeNull();
    }

    [Fact]
    public void Neighbour_MatchesPathCaseInsensitively()
    {
        FolderTreeNavigation.Neighbour(FlattenSample(), "/LIBRARY/PHOTOS", 1)!.Path
            .Should().Be("/library/photos/2024");
    }

    [Fact]
    public void FirstAndLast_ReturnTheEndsOfVisibleOrder()
    {
        IReadOnlyList<FolderRow> rows = FlattenSample();

        FolderTreeNavigation.First(rows)!.Path.Should().Be("/library/photos");
        FolderTreeNavigation.Last(rows)!.Path.Should().Be("/library/art");
    }

    [Fact]
    public void FirstAndLast_OnEmptyRows_ReturnNull()
    {
        FolderTreeNavigation.First([]).Should().BeNull();
        FolderTreeNavigation.Last([]).Should().BeNull();
    }

    [Fact]
    public void FirstChild_OfExpandedFolder_IsTheRowDirectlyBelow()
    {
        FolderTreeNavigation.FirstChild(FlattenSample(), "/library/photos")!.Path
            .Should().Be("/library/photos/2024");
    }

    [Fact]
    public void FirstChild_OfCollapsedFolder_IsNull()
    {
        // 2024 is collapsed: its hidden child means the next visible row is the sibling "art".
        FolderTreeNavigation.FirstChild(FlattenSample(), "/library/photos/2024").Should().BeNull();
    }

    [Fact]
    public void FirstChild_OfFolderWithoutChildren_IsNull()
    {
        FolderTreeNavigation.FirstChild(FlattenSample(), "/library/art").Should().BeNull();
    }

    [Fact]
    public void Parent_OfChild_IsTheOwningFolder()
    {
        FolderTreeNavigation.Parent(FlattenSample(), "/library/photos/2024")!.Path
            .Should().Be("/library/photos");
    }

    [Fact]
    public void Parent_OfRoot_IsNull()
    {
        FolderTreeNavigation.Parent(FlattenSample(), "/library/photos").Should().BeNull();
    }

    [Fact]
    public void Parent_OfGrandchild_SkipsSiblingsAtTheSameLevel()
    {
        FolderNode photos = Folder("/library/photos", expanded: true,
            Folder("/library/photos/2024", expanded: true,
                Folder("/library/photos/2024/raw"),
                Folder("/library/photos/2024/edited")),
            Folder("/library/photos/2025"));

        IReadOnlyList<FolderRow> rows = FolderTreeNavigation.Flatten([photos]);

        FolderTreeNavigation.Parent(rows, "/library/photos/2024/edited")!.Path
            .Should().Be("/library/photos/2024");
    }

    // photos (expanded) > 2024 (collapsed) > raw (hidden by the collapse), then art.
    private static IReadOnlyList<FolderRow> FlattenSample()
    {
        FolderNode photos = Folder("/library/photos", expanded: true,
            Folder("/library/photos/2024", expanded: false,
                Folder("/library/photos/2024/raw")));
        FolderNode art = Folder("/library/art");

        return FolderTreeNavigation.Flatten([photos, art]);
    }
}
