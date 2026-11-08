namespace Tagium.Core.Files;

/// <summary>
/// Shared page-size options and navigation rules for the file views.
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
}
