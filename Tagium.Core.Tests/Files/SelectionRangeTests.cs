using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class SelectionRangeTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private static readonly Guid[] Ordered = [A, B, C, D, E];

    [Fact]
    public void Between_ForwardsSpan_ReturnsInclusiveRange()
    {
        SelectionRange.Between(Ordered, B, D).Should().Equal(B, C, D);
    }

    [Fact]
    public void Between_BackwardsSpan_ReturnsSameRangeInDisplayOrder()
    {
        SelectionRange.Between(Ordered, D, B).Should().Equal(B, C, D);
    }

    [Fact]
    public void Between_AcrossPageBoundary_ReturnsContinuousInclusiveRange()
    {
        Guid[] firstPage = [A, B];
        Guid[] secondPage = [C, D, E];
        Guid[] orderedResults = [.. firstPage, .. secondPage];

        SelectionRange.Between(orderedResults, B, D).Should().Equal(B, C, D);
    }

    [Fact]
    public void Between_SameIdTwice_ReturnsSingleItem()
    {
        SelectionRange.Between(Ordered, C, C).Should().Equal(C);
    }

    [Fact]
    public void Between_AdjacentItems_ReturnsBoth()
    {
        SelectionRange.Between(Ordered, A, B).Should().Equal(A, B);
    }

    [Fact]
    public void Between_WholeList_ReturnsEverything()
    {
        SelectionRange.Between(Ordered, E, A).Should().Equal(Ordered);
    }

    [Fact]
    public void Between_AnchorOffPage_ReturnsEmpty()
    {
        Guid anchor = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        SelectionRange.Between(Ordered, anchor, C).Should().BeEmpty();
    }

    [Fact]
    public void Between_TargetNotInList_ReturnsEmpty()
    {
        Guid target = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

        SelectionRange.Between(Ordered, A, target).Should().BeEmpty();
    }

    [Fact]
    public void Between_EmptyList_ReturnsEmpty()
    {
        SelectionRange.Between([], A, B).Should().BeEmpty();
    }

    [Fact]
    public void Between_NullOrderedIds_Throws()
    {
        Action act = () => SelectionRange.Between(null!, A, B);

        act.Should().Throw<ArgumentNullException>();
    }
}
