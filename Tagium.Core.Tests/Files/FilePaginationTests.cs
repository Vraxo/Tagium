using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class FilePaginationTests
{
    [Fact]
    public void PageSizes_OffersTheSupportedChoices()
    {
        FilePagination.PageSizes.Should().Equal(50, 100, 250);
        FilePagination.DefaultPageSize.Should().Be(100);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(250)]
    public void NormalizePageSize_KeepsSupportedValues(int pageSize)
    {
        FilePagination.IsSupportedPageSize(pageSize).Should().BeTrue();
        FilePagination.NormalizePageSize(pageSize).Should().Be(pageSize);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(120)]
    [InlineData(500)]
    [InlineData(int.MaxValue)]
    public void NormalizePageSize_UsesDefaultForUnsupportedValues(int pageSize)
    {
        FilePagination.IsSupportedPageSize(pageSize).Should().BeFalse();
        FilePagination.NormalizePageSize(pageSize).Should().Be(FilePagination.DefaultPageSize);
    }

    [Theory]
    [InlineData(1, 100, 50, 0, 1)]
    [InlineData(1, 100, 50, 1, 1)]
    [InlineData(2, 100, 50, 260, 3)]
    [InlineData(3, 100, 50, 260, 5)]
    [InlineData(3, 100, 250, 260, 1)]
    [InlineData(4, 50, 100, 175, 2)]
    [InlineData(5, 50, 100, 230, 3)]
    [InlineData(50, 50, 100, 230, 3)] // stale page clamps to the final file
    [InlineData(0, 100, 250, 1000, 1)]
    public void PageAfterSizeChange_StaysOnThePageContainingTheOldFirstFile(
        int currentPage, int oldPageSize, int newPageSize, int totalFiles, int expected)
    {
        FilePagination.PageAfterSizeChange(currentPage, oldPageSize, newPageSize, totalFiles)
            .Should().Be(expected);
    }

    [Fact]
    public void PageAfterSizeChange_ContainsOldFirstFileForEverySupportedSize()
    {
        int[] totals = [1, 49, 50, 51, 100, 101, 249, 250, 251, 999];

        foreach (int total in totals)
        {
            foreach (int oldSize in FilePagination.PageSizes)
            {
                int oldPageCount = (int)Math.Ceiling((double)total / oldSize);
                for (int oldPage = 1; oldPage <= oldPageCount; oldPage++)
                {
                    int firstOldItem = (oldPage - 1) * oldSize;
                    foreach (int newSize in FilePagination.PageSizes)
                    {
                        int newPage = FilePagination.PageAfterSizeChange(oldPage, oldSize, newSize, total);
                        int firstNewItem = (newPage - 1) * newSize;
                        int lastNewItem = Math.Min(newPage * newSize, total) - 1;
                        Assert.InRange(firstOldItem, firstNewItem, lastNewItem);
                    }
                }
            }
        }
    }

    [Fact]
    public void PageAfterSizeChange_DoesNotOverflowForLargePages()
    {
        FilePagination.PageAfterSizeChange(int.MaxValue, 250, 50, int.MaxValue)
            .Should().Be(42_949_673);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(50, 0)]
    [InlineData(50, -1)]
    public void PageAfterSizeChange_RejectsNonPositivePageSizes(int oldSize, int newSize)
    {
        Action act = () => FilePagination.PageAfterSizeChange(1, oldSize, newSize, 100);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
