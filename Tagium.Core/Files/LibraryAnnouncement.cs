namespace Tagium.Core.Files;

/// <summary>
/// Wording for the polite live region that reports library state to screen readers.
/// Kept out of the component so the exact phrasing and its edge cases are unit-tested
/// instead of only being eyeballed at runtime — the UI project has no test project.
/// </summary>
public static class LibraryAnnouncement
{
    /// <summary>
    /// How much of the current library the selection covers, e.g. "5 of 12 selected".
    /// Counts are clamped, so a stale selection can never report more than the library holds.
    /// </summary>
    public static string Selection(int selectedCount, int totalCount)
    {
        if (totalCount <= 0)
        {
            return "No files";
        }

        int selected = Math.Clamp(selectedCount, 0, totalCount);

        return selected == 0
            ? $"Nothing selected, {Files(totalCount)}"
            : $"{selected} of {totalCount} selected";
    }

    /// <summary>
    /// The size of the current library after search and filters, e.g. "12 files".
    /// </summary>
    public static string Library(int totalCount)
    {
        return totalCount <= 0 ? "No files" : Files(totalCount);
    }

    private static string Files(int count)
    {
        return count == 1 ? "1 file" : $"{count} files";
    }
}
