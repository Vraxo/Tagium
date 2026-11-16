using AwesomeAssertions;
using Tagium.Core.Files;

namespace Tagium.Core.Tests.Files;

public class SelectionStateTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid Missing = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private static readonly List<Guid> Order = [A, B, C, D];

    [Fact]
    public void Select_PlainClick_ReplacesSelectionAndAnchors()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Select(B, multiSelect: false, rangeSelect: false, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { B });
        selection.Anchor.Should().Be(B);
    }

    [Fact]
    public void Select_CtrlClick_TogglesWithoutDroppingOthers_AndReanchors()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Select(C, multiSelect: true, rangeSelect: false, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { A, C });
        selection.Anchor.Should().Be(C);

        selection.Select(C, multiSelect: true, rangeSelect: false, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { A });
        // Toggling off still re-anchors to the clicked item: the next shift-click grows from here.
        selection.Anchor.Should().Be(C);
    }

    [Fact]
    public void Select_ShiftClick_ExtendsInclusivelyFromAnchor()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Select(C, multiSelect: false, rangeSelect: true, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { A, B, C });
        // The anchor stays put so repeated shift-clicks extend from the same origin.
        selection.Anchor.Should().Be(A);
    }

    [Fact]
    public void Select_ShiftClickBackwards_SelectsTheSameSpan()
    {
        SelectionState selection = new();
        selection.Select(D, multiSelect: false, rangeSelect: false, Order);
        selection.Select(B, multiSelect: false, rangeSelect: true, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { B, C, D });
    }

    [Fact]
    public void Select_ShiftClickWithCtrl_AddsToExistingSelection()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Select(D, multiSelect: true, rangeSelect: false, Order);
        selection.Select(B, multiSelect: true, rangeSelect: true, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { A, B, C, D });
    }

    [Fact]
    public void Select_ShiftClickWithoutAnchor_FallsBackToSingleSelection()
    {
        SelectionState selection = new();
        selection.Select(B, multiSelect: false, rangeSelect: true, Order);

        selection.Ids.Should().BeEquivalentTo(new[] { B });
        selection.Anchor.Should().Be(B);
    }

    [Fact]
    public void Select_ShiftClickAfterAnchorWasFilteredOut_FallsBackToSingleSelection()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);

        // A filter narrows the result set so the anchor is gone; the span cannot be built.
        selection.Select(D, multiSelect: false, rangeSelect: true, new[] { B, C, D });

        selection.Ids.Should().BeEquivalentTo(new[] { D });
        selection.Anchor.Should().Be(D);
    }

    [Fact]
    public void Select_EmptyOrderedIdsWithShift_ClickSelectsJustTheClickedFile()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Select(C, multiSelect: false, rangeSelect: true, Array.Empty<Guid>());

        selection.Ids.Should().BeEquivalentTo(new[] { C });
    }

    [Fact]
    public void Set_ReplacesEverythingAndClearsAnchor()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);
        selection.Set([C, D]);

        selection.Ids.Should().BeEquivalentTo(new[] { C, D });
        // A programmatic set is not a gesture, so the next shift-click must not span from A.
        selection.Anchor.Should().BeNull();
    }

    [Fact]
    public void Clear_DropsSelectionAndAnchor_AndReportsWhetherAnythingChanged()
    {
        SelectionState selection = new();
        selection.Select(A, multiSelect: false, rangeSelect: false, Order);

        selection.Clear().Should().BeTrue();
        selection.Count.Should().Be(0);
        selection.Anchor.Should().BeNull();

        selection.Clear().Should().BeFalse();
    }

    [Fact]
    public void Select_UnknownId_IsStillSelectable()
    {
        // Selection is by id and does not require the id to be in the current result set;
        // views filter what they render, so a stale id is harmless and must not throw.
        SelectionState selection = new();
        selection.Select(Missing, multiSelect: false, rangeSelect: false, Order);

        selection.Contains(Missing).Should().BeTrue();
        selection.Count.Should().Be(1);
    }
}
