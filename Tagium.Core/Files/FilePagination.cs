namespace Tagium.Core.Files;

/// <summary>
/// Shared page-size options and navigation rules for the file views, including the
/// batch maths used by infinite scroll (where <see cref="PageSizes"/> are batch sizes).
/// </summary>
public static class FilePagination
{
    public const int DefaultPageSize = 100;

    public static IReadOnlyList<int> PageSizes { get; } = Array.AsReadOnly(new[] { 50, DefaultPageSize, 250 });

    public static bool IsSupportedPageSize(int pageSize) => PageSizes.Contains(pageSize);

    public static int NormalizePageSize(int pageSize) =>
        IsSupportedPageSize(pageSize) ? pageSize : DefaultPageSize;

    /// <summary>
    /// Keep the first item of the old page in view when changing page size. A stale page is
    /// clamped to the last item, and an empty result set returns to page one.
    /// </summary>
    public static int PageAfterSizeChange(int currentPage, int oldPageSize, int newPageSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(oldPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newPageSize);

        if (totalFiles <= 0)
        {
            return 1;
        }

        long firstItemIndex = (long)(Math.Max(currentPage, 1) - 1) * oldPageSize;
        long lastItemIndex = (long)totalFiles - 1;
        return (int)(Math.Min(firstItemIndex, lastItemIndex) / newPageSize) + 1;
    }

    /// <summary>
    /// How many files infinite scroll shows before the first batch is exhausted.
    /// An empty result set shows nothing.
    /// </summary>
    public static int InitialVisibleCount(int batchSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        if (totalFiles <= 0)
        {
            return 0;
        }

        return Math.Min(batchSize, totalFiles);
    }

    /// <summary>
    /// Grows the visible set by one batch, clamped to the total. A stale visible count
    /// (for example after files were removed) still lands on the final file.
    /// </summary>
    public static int NextVisibleCount(int visibleCount, int batchSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        if (totalFiles <= 0)
        {
            return 0;
        }

        long next = (long)Math.Max(visibleCount, 0) + batchSize;
        return (int)Math.Min(next, totalFiles);
    }

    public static bool HasMoreFiles(int visibleCount, int totalFiles) => visibleCount < totalFiles;

    /// <summary>
    /// Which file is "in view" while browsing — the item at the top of the rendered viewport.
    /// Switching between pagination and infinite scroll keeps the user on this file instead of
    /// throwing away their position.
    /// </summary>
    public static int TopIndex(int page, int pageSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        if (totalFiles <= 0)
        {
            return 0;
        }

        long top = (long)(Math.Max(page, 1) - 1) * pageSize;
        return (int)Math.Min(top, totalFiles - 1);
    }

    /// <summary>
    /// The page that starts on (or contains) <paramref name="itemIndex"/>, so returning to
    /// pagination lands on the item the user was looking at.
    /// </summary>
    public static int PageForIndex(int itemIndex, int pageSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        if (totalFiles <= 0 || itemIndex <= 0)
        {
            return 1;
        }

        return (int)(Math.Min(itemIndex, totalFiles - 1) / pageSize) + 1;
    }

    /// <summary>
    /// How many files infinite scroll must render to include <paramref name="itemIndex"/> —
    /// whole batches, clamped to the total. Keeping the anchored item on its batch boundary
    /// means the page the user returns from is the one still filled below it.
    /// </summary>
    public static int VisibleCountContaining(int itemIndex, int batchSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        if (totalFiles <= 0)
        {
            return 0;
        }

        long index = Math.Clamp(itemIndex, 0, totalFiles - 1);
        long batches = (index / batchSize) + 1;
        return (int)Math.Min(batches * batchSize, totalFiles);
    }

    /// <summary>
    /// The [start, end) slice of the filtered result set a view is rendering: one page when
    /// paginated, every loaded batch when infinitely scrolling. An empty result set returns
    /// (0, 0), and a stale page clamps to the end rather than reporting items that do not exist.
    /// </summary>
    public static (int Start, int End) RenderedWindow(
        bool isInfiniteScroll, int currentPage, int pageSize, int visibleCount, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        if (totalFiles <= 0)
        {
            return (0, 0);
        }

        if (isInfiniteScroll)
        {
            return (0, Math.Clamp(visibleCount, 0, totalFiles));
        }

        long start = (long)(Math.Max(currentPage, 1) - 1) * pageSize;
        int clampedStart = (int)Math.Min(start, totalFiles);
        return (clampedStart, Math.Min(clampedStart + pageSize, totalFiles));
    }

    /// <summary>
    /// The file the user is focused on within the rendered window: the first selected file in
    /// that window, otherwise its first file. This is the anchor a mode switch preserves, so a
    /// selection on another page does not drag the user back to it.
    /// </summary>
    public static int AnchorIndex(
        IReadOnlyList<Guid> orderedIds, IReadOnlySet<Guid> selectedIds, (int Start, int End) window)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        ArgumentNullException.ThrowIfNull(selectedIds);

        for (int index = Math.Max(window.Start, 0); index < Math.Min(window.End, orderedIds.Count); index++)
        {
            if (selectedIds.Contains(orderedIds[index]))
            {
                return index;
            }
        }

        return Math.Max(window.Start, 0);
    }

    /// <summary>
    /// Keeps already-loaded files when the batch size changes so the scroll position is
    /// preserved, but always shows at least one batch and never more than the total.
    /// </summary>
    public static int VisibleCountAfterBatchSizeChange(int visibleCount, int newBatchSize, int totalFiles)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newBatchSize);

        if (totalFiles <= 0)
        {
            return 0;
        }

        int minimum = Math.Min(newBatchSize, totalFiles);
        return Math.Clamp(visibleCount, minimum, totalFiles);
    }
}
