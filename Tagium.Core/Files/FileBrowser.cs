using Tagium.Core.Settings;

namespace Tagium.Core.Files;

/// <summary>
/// The file list the views render, plus how far into it the user is: pages when paginated,
/// loaded batches when infinitely scrolling, the selection, and where focus lands next.
///
/// This type owns no filtering. It is handed a result set through <see cref="ApplyLoadedFiles"/>
/// and reports back through <see cref="NeedsReload"/> when a change to the browsing mode or
/// page size needs fresh data. Everything it decides is expressed in terms of the list it
/// already holds, which is what makes it testable without the library or the settings manager.
/// </summary>
public sealed class FileBrowser : IDisposable
{
    private readonly SelectionState _selection = new();
    private List<FileEntry> _currentFiles = [];

    /// <summary>
    /// Takes the settings snapshot it needs rather than the manager: this type only reads two
    /// values at construction and is then driven by <see cref="ApplySettings"/>, which keeps it
    /// constructible in tests without touching a settings file on disk.
    /// </summary>
    public FileBrowser(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        PageSize = settings.FilesPerPage;
        ScrollMode = settings.FileScrollMode;
        VisibleFileCount = PageSize;
    }

    // --- The result set -------------------------------------------------------------------

    public IReadOnlyList<FileEntry> Files => _currentFiles;

    public int TotalFilesCount => _currentFiles.Count;

    // --- Pagination -----------------------------------------------------------------------

    public int CurrentPage { get; private set; } = 1;

    public int PageSize { get; private set; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling((double)TotalFilesCount / PageSize);

    // --- Infinite scroll (PageSize doubles as the batch size) -----------------------------

    public FileScrollMode ScrollMode { get; private set; }

    public int VisibleFileCount { get; private set; }

    public bool IsInfiniteScroll => ScrollMode == FileScrollMode.Infinite;

    public bool HasMoreFiles => IsInfiniteScroll && FilePagination.HasMoreFiles(VisibleFileCount, TotalFilesCount);

    // --- Selection ------------------------------------------------------------------------

    /// <summary>
    /// The selected files. Read-only by contract: mutate selection through
    /// <see cref="SelectFile"/>, <see cref="SetSelection"/> or <see cref="ClearSelection"/> so
    /// the shift-range anchor cannot drift out of sync with the set.
    /// </summary>
    public IReadOnlySet<Guid> SelectedFileIds => _selection.Ids;

    // --- Focus hand-off -------------------------------------------------------------------

    /// <summary>
    /// Position within the files a view is about to render (not the result set) to focus after
    /// the next render; -1 when the view should not move focus. Infinite scroll can append the
    /// destination batch in the same render as the key press, so focus is placed after the DOM
    /// exists rather than assumed to be reachable already.
    /// </summary>
    public int PendingFocusIndex { get; private set; } = -1;

    public event Action? OnChange;

    public event Action? OnSelectionChange;

    /// <summary>
    /// Raised when the list needs to be re-queried because the browsing mode, page size or
    /// filter changed underneath it. <c>AppState</c> answers by reloading from the
    /// library; without that wiring this type still behaves correctly for the list it holds.
    /// </summary>
    public event Func<Task>? NeedsReload;

    // --- Result set -----------------------------------------------------------------------

    /// <summary>
    /// Installs a freshly queried result set, clamping the page and loaded-batch count to it.
    /// </summary>
    public void ApplyLoadedFiles(List<FileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        _currentFiles = files;

        // Adjust page if it goes out of bounds after a filter or library change.
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(TotalPages, 1));

        // Keep the loaded batches across refreshes of the same result set (thumbnail work,
        // ratings and tags reload the list); only clamp to the new total and guarantee at
        // least one batch. A filter change resets the count through ResetScrollPosition.
        VisibleFileCount = FilePagination.VisibleCountAfterBatchSizeChange(
            VisibleFileCount, PageSize, TotalFilesCount);

        // The reload itself is what the views are waiting for, so this has to notify — it is
        // the only event that fires when a filter change has finished querying.
        NotifyChanged();
    }

    // --- Rendered slice -------------------------------------------------------------------

    public IEnumerable<FileEntry> GetPagedFiles()
    {
        if (PageSize <= 0)
        {
            return [];
        }

        return _currentFiles
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);
    }

    /// <summary>
    /// The files the current view should render: the current page when paginated, or
    /// the loaded batches from the top when infinite scroll is on.
    /// </summary>
    public IEnumerable<FileEntry> GetVisibleFiles()
    {
        if (IsInfiniteScroll)
        {
            return _currentFiles.Take(Math.Max(VisibleFileCount, 0));
        }

        return GetPagedFiles();
    }

    /// <summary>The [start, end) slice of the filtered result set the views are rendering.</summary>
    public (int Start, int End) RenderedWindow() => FilePagination.RenderedWindow(
        IsInfiniteScroll, CurrentPage, PageSize, VisibleFileCount, TotalFilesCount);

    /// <summary>
    /// Index of the file the user is focused on inside the rendered window, computed in the
    /// same coordinate space <see cref="MovePage"/> and the anchors use.
    /// </summary>
    public int CurrentAnchorIndex() =>
        FilePagination.AnchorIndex(OrderedFileIds(), _selection.Ids, RenderedWindow());

    // --- Navigation -----------------------------------------------------------------------

    /// <summary>
    /// A new result set always starts at the top: page one when paginated, first batch
    /// when infinitely scrolling, so the user never lands mid-scroll in a fresh filter.
    /// </summary>
    public void ResetScrollPosition()
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
        NotifyChanged();
    }

    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            NotifyChanged();
        }
    }

    public void PrevPage()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            NotifyChanged();
        }
    }

    /// <summary>
    /// Asks for a new page size. The size is a shared setting, so this does not apply it
    /// directly: it raises <see cref="NeedsPageSizeChange"/> for <c>AppState</c> to
    /// write through the settings manager, which then calls back into
    /// <see cref="ApplySettings"/>. Applying it in two places would let the two file views
    /// disagree about the page size.
    /// </summary>
    public void SetPageSize(int pageSize)
    {
        if (!FilePagination.IsSupportedPageSize(pageSize) || pageSize == PageSize)
        {
            return;
        }

        NeedsPageSizeChange?.Invoke(pageSize);
    }

    /// <summary>Raised by <see cref="SetPageSize"/>; the subscriber persists and re-applies it.</summary>
    public event Action<int>? NeedsPageSizeChange;

    /// <summary>Appends the next batch in infinite scroll mode. Returns <c>false</c> when there is nothing more to load.</summary>
    public bool LoadMoreFiles()
    {
        if (!HasMoreFiles)
        {
            return false;
        }

        VisibleFileCount = FilePagination.NextVisibleCount(VisibleFileCount, PageSize, TotalFilesCount);
        NotifyChanged();
        return true;
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
            NotifyChanged();
            return false;
        }

        SelectFile(target.Id, multiSelect: false, rangeSelect: extendSelection);
        PendingFocusIndex = direction > 0 ? 0 : pageFiles.Count - 1;
        NotifyChanged();
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
        NotifyChanged();
        return true;
    }

    // --- Selection ------------------------------------------------------------------------

    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
    {
        // Notify unconditionally: a Ctrl-click on an already-selected file changes nothing the
        // views render but does re-anchor, and the inspector mirrors this event.
        _selection.Select(id, multiSelect, rangeSelect, OrderedFileIds());
        NotifySelectionChanged();
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        _selection.Set(ids);
        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (!_selection.Clear())
        {
            return;
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Drops the selection without raising <see cref="OnSelectionChange"/>: a filter change
    /// already re-renders the whole list through <see cref="OnChange"/>, and firing both would
    /// repaint every row twice per keystroke while the user types a search.
    /// </summary>
    public void ClearSelectionSilently() => _ = _selection.Clear();

    /// <summary>The filtered result set in display order — the coordinate space shift-ranges use.</summary>
    private List<Guid> OrderedFileIds()
    {
        List<Guid> ids = new(_currentFiles.Count);
        foreach (FileEntry file in _currentFiles)
        {
            ids.Add(file.Id);
        }

        return ids;
    }

    // --- Focus ----------------------------------------------------------------------------

    /// <summary>
    /// Reads and clears the index the view should focus after a cross-batch page move.
    /// Returns <c>-1</c> when the visible viewport already contains the target.
    /// </summary>
    public int ConsumePendingFocusIndex()
    {
        int index = PendingFocusIndex;
        PendingFocusIndex = -1;
        return index;
    }

    // --- Settings reaction ----------------------------------------------------------------

    /// <summary>
    /// Applies a new scroll mode and/or page size, keeping the user where they were.
    ///
    /// The anchor is the first selected file that is rendered — the file they are looking at —
    /// or, with nothing selected there, the top of the rendered viewport. Returning to
    /// pagination lands on the page holding the anchor; switching to infinite scroll renders
    /// whole batches up to and including the anchor's batch, so the page they came from is
    /// still filled below. Returns <c>true</c> when anything changed.
    /// </summary>
    public bool ApplySettings(FileScrollMode newMode, int newPageSize)
    {
        bool changed = false;
        int modeAnchorIndex = -1;

        if (newMode != ScrollMode)
        {
            modeAnchorIndex = CurrentAnchorIndex();

            // Compute the landing position with the mode still describing where we came from,
            // then flip it, so the maths for each direction uses the correct rendering model.
            ScrollMode = newMode;

            if (newMode == FileScrollMode.Infinite)
            {
                VisibleFileCount = FilePagination.VisibleCountContaining(
                    modeAnchorIndex, PageSize, TotalFilesCount);
            }
            else
            {
                CurrentPage = FilePagination.PageForIndex(modeAnchorIndex, PageSize, TotalFilesCount);
            }

            changed = true;
        }

        if (newPageSize != PageSize)
        {
            if (ScrollMode == FileScrollMode.Infinite)
            {
                VisibleFileCount = FilePagination.VisibleCountAfterBatchSizeChange(
                    VisibleFileCount, newPageSize, TotalFilesCount);
            }
            else
            {
                CurrentPage = FilePagination.PageAfterSizeChange(CurrentPage, PageSize, newPageSize, TotalFilesCount);
            }

            PageSize = newPageSize;
            changed = true;
        }

        // Do this last: a page-size change above can move the rendered window, and the
        // anchor has to be focusable in whatever the views are about to render.
        if (modeAnchorIndex >= 0)
        {
            ApplyAnchorAfterModeChange(modeAnchorIndex);
            changed = true;
        }

        if (changed)
        {
            NotifyChanged();
        }

        return changed;
    }

    /// <summary>
    /// A mode change re-renders the list from scratch. When the user has no selection, put the
    /// anchor — the file they were looking at — back on the keyboard path so the roving tab
    /// stop and arrow keys resume there. An existing selection is left untouched: it already
    /// drives the tab stop, and silently replacing it would discard the user's work.
    /// </summary>
    private void ApplyAnchorAfterModeChange(int anchorIndex)
    {
        PendingFocusIndex = -1;

        if (SelectedFileIds.Count > 0 || _currentFiles.Count == 0)
        {
            return;
        }

        // A page-size change can move the window off the anchor; fall back to its first file.
        (int start, int end) = RenderedWindow();
        int targetIndex = Math.Clamp(anchorIndex, start, Math.Max(end - 1, start));

        SelectFile(_currentFiles[targetIndex].Id, multiSelect: false, rangeSelect: false);
        // PendingFocusIndex is relative to the rendered items, not the result set.
        PendingFocusIndex = targetIndex - start;
    }

    private void NotifyChanged() => OnChange?.Invoke();

    private void NotifySelectionChanged() => OnSelectionChange?.Invoke();

    public void Dispose()
    {
        NeedsReload = null;
        NeedsPageSizeChange = null;
    }
}
