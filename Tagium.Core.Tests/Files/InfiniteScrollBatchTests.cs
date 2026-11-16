using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class InfiniteScrollBatchTests
{
    [Theory]
    [InlineData(100, 0, 0)]
    [InlineData(100, 1, 1)]
    [InlineData(100, 99, 99)]
    [InlineData(100, 100, 100)]
    [InlineData(100, 101, 100)]
    [InlineData(50, 1000, 50)]
    public void InitialVisibleCount_UsesOneBatchAndNeverOvershoots(int batchSize, int totalFiles, int expected)
    {
        FilePagination.InitialVisibleCount(batchSize, totalFiles).Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 100, 250, 200)]
    [InlineData(100, 100, 150, 150)]
    [InlineData(100, 100, 100, 100)]
    [InlineData(100, 99, 100, 100)]
    [InlineData(0, 100, 1000, 100)]
    [InlineData(250, 50, 1000, 300)]
    public void NextVisibleCount_AppendsOneBatchAndClampsToTheTotal(
        int visibleCount, int batchSize, int totalFiles, int expected)
    {
        FilePagination.NextVisibleCount(visibleCount, batchSize, totalFiles).Should().Be(expected);
    }

    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(100, 101, true)]
    [InlineData(0, 1, true)]
    [InlineData(0, 0, false)]
    public void HasMoreFiles_ComparesLoadedFilesToTheTotal(int visibleCount, int totalFiles, bool expected)
    {
        FilePagination.HasMoreFiles(visibleCount, totalFiles).Should().Be(expected);
    }

    [Theory]
    [InlineData(300, 50, 1000, 300)] // already loaded more than the new batch: keep it
    [InlineData(300, 250, 1000, 300)] // still above the new batch size
    [InlineData(100, 250, 1000, 250)] // grows to at least one new batch
    [InlineData(100, 50, 40, 40)] // result set smaller than a batch
    [InlineData(0, 100, 0, 0)] // empty result set
    public void VisibleCountAfterBatchSizeChange_KeepsLoadedBatchesAndGuaranteesOne(
        int visibleCount, int newBatchSize, int totalFiles, int expected)
    {
        FilePagination.VisibleCountAfterBatchSizeChange(visibleCount, newBatchSize, totalFiles)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public void BatchMaths_RejectsNonPositiveBatchSizes(int batchSize)
    {
        Action initial = () => FilePagination.InitialVisibleCount(batchSize, 100);
        Action next = () => FilePagination.NextVisibleCount(100, batchSize, 1000);
        Action resize = () => FilePagination.VisibleCountAfterBatchSizeChange(100, batchSize, 1000);

        initial.Should().Throw<ArgumentOutOfRangeException>();
        next.Should().Throw<ArgumentOutOfRangeException>();
        resize.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ScrollingToTheEnd_LoadsEveryFileExactlyOnce()
    {
        const int total = 475;
        int visible = FilePagination.InitialVisibleCount(100, total);
        List<int> batches = [visible];

        while (FilePagination.HasMoreFiles(visible, total))
        {
            int next = FilePagination.NextVisibleCount(visible, 100, total);
            batches.Add(next - visible);
            visible = next;
        }

        visible.Should().Be(total);
        batches.Should().Equal(100, 100, 100, 100, 75);
    }
}
