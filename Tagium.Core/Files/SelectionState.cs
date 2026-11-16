namespace Tagium.Core.Files;

/// <summary>
/// The current file selection and the anchor that shift-ranges grow from.
///
/// A plain click (or arrow) replaces the selection and sets the anchor; Ctrl toggles an item
/// and re-anchors; Shift extends an inclusive range from the anchor across the whole filtered
/// result set. The span maths itself lives in <see cref="SelectionRange"/>; this type owns the
/// resulting state and the rule that a range which cannot be spanned degrades to a plain
/// selection instead of silently selecting nothing.
///
/// Kept in Core so it can be unit-tested: the UI project has no test project, and the anchor
/// rules are exactly the part that is easy to break without noticing.
/// </summary>
public sealed class SelectionState
{
    private readonly HashSet<Guid> _selectedIds = [];
    private Guid? _anchor;

    /// <summary>The anchor a shift-range would grow from, if one is set.</summary>
    public Guid? Anchor => _anchor;

    public int Count => _selectedIds.Count;

    public bool Contains(Guid id) => _selectedIds.Contains(id);

    /// <summary>
    /// Applies a click or arrow-key selection over the ordered, filtered result set.
    /// A range that cannot be spanned falls back to a plain selection rather than selecting
    /// nothing, which is what makes shift-clicking after a filter change feel predictable.
    /// </summary>
    public void Select(Guid id, bool multiSelect, bool rangeSelect, IReadOnlyList<Guid> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        if (rangeSelect && TryExtendRange(id, multiSelect, orderedIds))
        {
            return;
        }

        if (multiSelect)
        {
            if (!_selectedIds.Remove(id))
            {
                _ = _selectedIds.Add(id);
            }
        }
        else
        {
            _selectedIds.Clear();
            _ = _selectedIds.Add(id);
        }

        // Toggling an item off with Ctrl still re-anchors: the next shift-range grows from here.
        _anchor = id;
    }

    /// <summary>Replaces the selection wholesale, dropping the anchor (a programmatic set, not a gesture).</summary>
    public void Set(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        _selectedIds.Clear();
        _anchor = null;

        foreach (Guid id in ids)
        {
            _ = _selectedIds.Add(id);
        }
    }

    public bool Clear()
    {
        _anchor = null;

        if (_selectedIds.Count == 0)
        {
            return false;
        }

        _selectedIds.Clear();
        return true;
    }

    /// <summary>
    /// The selection as a read-only set. Exposed for O(1) membership checks and counts; callers
    /// mutate selection through this type so the anchor can never drift out of sync.
    /// </summary>
    public IReadOnlySet<Guid> Ids => _selectedIds;

    /// <summary>
    /// Shift-click: selects everything between the anchor and <paramref name="id"/> in display
    /// order, including files on other pages. Returns <c>false</c> when there is nothing to
    /// span — no anchor yet, or the anchor is no longer in the result set — so the caller falls
    /// back to a plain single selection.
    /// </summary>
    private bool TryExtendRange(Guid id, bool addToSelection, IReadOnlyList<Guid> orderedIds)
    {
        if (_anchor is not { } anchor)
        {
            return false;
        }

        IReadOnlyList<Guid> range = SelectionRange.Between(orderedIds, anchor, id);
        if (range.Count == 0)
        {
            return false;
        }

        if (!addToSelection)
        {
            _selectedIds.Clear();
        }

        foreach (Guid rangeId in range)
        {
            _ = _selectedIds.Add(rangeId);
        }

        // The anchor stays where it was, so repeated shift-clicks extend from the same origin.
        return true;
    }
}
