namespace Tagium.Core.Files;

public static class SelectionRange
{
    /// <summary>
    /// The inclusive span between <paramref name="anchorId"/> and <paramref name="targetId"/> in
    /// display order, used by shift-click selection. The span works in either direction, so
    /// shift-clicking above the anchor selects the same rows as shift-clicking below it.
    /// Returns an empty list when either id is not part of <paramref name="orderedIds"/>, which
    /// is the caller's signal to fall back to a plain single selection (the anchor has scrolled
    /// off the current page, been filtered out, or was never set).
    /// </summary>
    public static IReadOnlyList<Guid> Between(IReadOnlyList<Guid> orderedIds, Guid anchorId, Guid targetId)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        int anchorIndex = IndexOf(orderedIds, anchorId);
        int targetIndex = IndexOf(orderedIds, targetId);

        if (anchorIndex < 0 || targetIndex < 0)
        {
            return [];
        }

        int start = Math.Min(anchorIndex, targetIndex);
        int end = Math.Max(anchorIndex, targetIndex);

        Guid[] range = new Guid[end - start + 1];
        for (int i = start; i <= end; i++)
        {
            range[i - start] = orderedIds[i];
        }

        return range;
    }

    private static int IndexOf(IReadOnlyList<Guid> ids, Guid id)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
