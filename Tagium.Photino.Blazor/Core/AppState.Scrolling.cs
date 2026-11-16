using Tagium.Core.Files;

namespace Tagium.Photino.Blazor;

public partial class AppState
{
    /// <summary>Appends the next batch in infinite scroll mode. Returns <c>false</c> when there is nothing more to load.</summary>
    public bool LoadMoreFiles()
    {
        if (!HasMoreFiles)
        {
            return false;
        }

        VisibleFileCount = FilePagination.NextVisibleCount(VisibleFileCount, PageSize, TotalFilesCount);
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// A new result set always starts at the top: page one when paginated, first batch
    /// when infinitely scrolling, so the user never lands mid-scroll in a fresh filter.
    /// </summary>
    private void ResetScrollPosition()
    {
        CurrentPage = 1;
        VisibleFileCount = PageSize;
        PendingFocusIndex = -1;
    }

    public void SetPage(int page)
    {
        if (page < 1 || page > TotalPages)
        {
            return;
        }

        CurrentPage = page;
        NotifyStateChanged();
    }

    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            NotifyStateChanged();
        }
    }

    public void PrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            NotifyStateChanged();
        }
    }

    public void SetPageSize(int pageSize)
    {
        if (!FilePagination.IsSupportedPageSize(pageSize) || pageSize == PageSize)
        {
            return;
        }

        // The settings handler updates the current page or loaded batch count and notifies
        // both file views, so the two views never disagree about the page size.
        _settings.Update(settings => settings.FilesPerPage = pageSize);
    }

    /// <summary>
    /// Steps to the adjacent item across a viewport boundary — the arrow-key behaviour at
    /// the end of the rendered files. Paginated views move to the next/previous page;
    /// infinite scroll appends the next batch when it is needed. Shift extends the selection
    /// anchor across the complete filtered result set in both modes.
    /// </summary>
    public bool MovePage(int direction, bool extendSelection = false)
    {
        if (direction == 0 || TotalFilesCount == 0)
        {
            return false;
        }

        return IsInfiniteScroll
            ? MoveInfinite(direction, extendSelection, step: 1)
            : MovePaginatedPage(direction, extendSelection);
    }

    /// <summary>
    /// Moves one screenful — Page Up / Page Down. Paginated views move a whole page;
    /// infinite scroll moves <see cref="PageSize"/> files and loads what it needs.
    /// </summary>
    public bool MoveBatch(int direction, bool extendSelection = false)
    {
        if (direction == 0 || TotalFilesCount == 0)
        {
            return false;
        }

        return IsInfiniteScroll
            ? MoveInfinite(direction, extendSelection, step: PageSize)
            : MovePaginatedPage(direction, extendSelection);
    }

    private bool MovePaginatedPage(int direction, bool extendSelection)
    {
        int targetPage = Math.Clamp(CurrentPage + Math.Sign(direction), 1, TotalPages);
        if (targetPage == CurrentPage)
        {
            return false;
        }

        CurrentPage = targetPage;
        List<FileEntry> pageFiles = GetPagedFiles().ToList();
        FileEntry? target = direction > 0 ? pageFiles.FirstOrDefault() : pageFiles.LastOrDefault();

        if (target is null)
        {
            NotifyStateChanged();
            return false;
        }

        SelectFile(target.Id, multiSelect: false, rangeSelect: extendSelection);
        PendingFocusIndex = direction > 0 ? 0 : pageFiles.Count - 1;
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Moves <paramref name="step"/> files through the full result set, appending batches until
    /// the destination file is rendered. With nothing selected, a move enters the rendered
    /// files at the top (forward) or the bottom of what is loaded (backward).
    /// </summary>
    private bool MoveInfinite(int direction, bool extendSelection, int step)
    {
        List<FileEntry> visible = _currentFiles.Take(VisibleFileCount).ToList();
        int currentIndex = visible.FindIndex(f => SelectedFileIds.Contains(f.Id));

        int anchor = currentIndex >= 0
            ? currentIndex
            : (direction > 0 ? 0 : Math.Max(VisibleFileCount - 1, 0));
        int distance = currentIndex >= 0 ? Math.Sign(direction) * step : 0;
        int targetIndex = Math.Clamp(anchor + distance, 0, TotalFilesCount - 1);

        if (targetIndex == currentIndex)
        {
            return false;
        }

        // The destination must exist in the DOM before focus can move there.
        while (targetIndex >= VisibleFileCount
            && FilePagination.HasMoreFiles(VisibleFileCount, TotalFilesCount))
        {
            VisibleFileCount = FilePagination.NextVisibleCount(VisibleFileCount, PageSize, TotalFilesCount);
        }

        FileEntry target = _currentFiles[targetIndex];
        SelectFile(target.Id, multiSelect: false, rangeSelect: extendSelection);
        PendingFocusIndex = targetIndex;
        NotifyStateChanged();
        return true;
    }
}
