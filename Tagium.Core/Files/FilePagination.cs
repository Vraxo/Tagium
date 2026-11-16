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
