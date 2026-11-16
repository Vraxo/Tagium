using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class RenderedWindowTests
{
    [Theory]
    [InlineData(1, 100, 1000, 0, 100)]
    [InlineData(2, 100, 1000, 100, 200)]
    [InlineData(10, 100, 1000, 900, 1000)]
    public void RenderedWindow_Paginated_ReturnsTheCurrentPage(
        int page, int pageSize, int total, int expectedStart, int expectedEnd)
    {
        FilePagination.RenderedWindow(isInfiniteScroll: false, page, pageSize, visibleCount: pageSize, total)
            .Should().Be((expectedStart, expectedEnd));
    }

    [Fact]
    public void RenderedWindow_Paginated_LastPageIsShort()
    {
        FilePagination.RenderedWindow(false, currentPage: 3, pageSize: 100, visibleCount: 100, totalFiles: 250)
            .Should().Be((200, 250));
    }

    [Fact]
    public void RenderedWindow_Paginated_StalePageClampsToTheEndInsteadOfOverrunning()
    {
        // The result set shrank under a page number that is no longer valid.
        FilePagination.RenderedWindow(false, currentPage: 9, pageSize: 100, visibleCount: 100, totalFiles: 150)
            .Should().Be((150, 150));
    }

    [Theory]
    [InlineData(100, 1000, 0, 100)]
    [InlineData(300, 1000, 0, 300)]
    [InlineData(1000, 1000, 0, 1000)]
    public void RenderedWindow_Infinite_ReturnsEverythingLoaded(
        int visibleCount, int total, int expectedStart, int expectedEnd)
    {
        FilePagination.RenderedWindow(true, currentPage: 7, pageSize: 100, visibleCount, total)
            .Should().Be((expectedStart, expectedEnd));
    }

    [Fact]
    public void RenderedWindow_Infinite_ClampsLoadedCountToTotal()
    {
        FilePagination.RenderedWindow(true, currentPage: 1, pageSize: 100, visibleCount: 500, totalFiles: 120)
            .Should().Be((0, 120));
    }

    [Fact]
    public void RenderedWindow_EmptyResultSet_IsEmptyAtZero()
    {
        FilePagination.RenderedWindow(false, currentPage: 1, pageSize: 100, visibleCount: 100, totalFiles: 0)
            .Should().Be((0, 0));
        FilePagination.RenderedWindow(true, currentPage: 1, pageSize: 100, visibleCount: 100, totalFiles: 0)
            .Should().Be((0, 0));
    }

    [Fact]
    public void RenderedWindow_NonPositivePageSize_Throws()
    {
        Action act = () => FilePagination.RenderedWindow(false, 1, 0, 0, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}

public class AnchorIndexTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    private static readonly List<Guid> Order = [A, B, C, D];

    [Fact]
    public void AnchorIndex_NoSelection_ReturnsWindowStart()
    {
        FilePagination.AnchorIndex(Order, new HashSet<Guid>(), (1, 3)).Should().Be(1);
    }

    [Fact]
    public void AnchorIndex_ReturnsFirstSelectedFileInsideTheWindow()
    {
        FilePagination.AnchorIndex(Order, new HashSet<Guid> { C, D }, (0, 2)).Should().Be(0);
        FilePagination.AnchorIndex(Order, new HashSet<Guid> { C, D }, (0, 4)).Should().Be(2);
    }

    [Fact]
    public void AnchorIndex_IgnoresSelectionOutsideTheWindow()
    {
        // A file selected on another page must not drag the view back to it.
        FilePagination.AnchorIndex(Order, new HashSet<Guid> { A }, (2, 4)).Should().Be(2);
    }

    [Fact]
    public void AnchorIndex_WindowPastTheEnd_FallsBackToStart()
    {
        FilePagination.AnchorIndex(Order, new HashSet<Guid> { D }, (4, 4)).Should().Be(4);
    }

    [Fact]
    public void AnchorIndex_ClampsNegativeWindowStart()
    {
        FilePagination.AnchorIndex(Order, new HashSet<Guid> { A }, (-5, 2)).Should().Be(0);
    }
}
