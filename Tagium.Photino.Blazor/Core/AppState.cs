using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Files;

namespace Tagium.Photino.Blazor;

public enum AppView { Files, Folders, Tags }

public class AppState : IDisposable
{
    private readonly LibraryService _library;
    private readonly ModalService _modalService;
    private readonly SettingsManager _settings;
    private List<FileEntry> _currentFiles = [];

    public AppView CurrentView { get; private set; } = AppView.Files;
    public bool IsTagGraphView { get; private set; }
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public string? CurrentFolderPath { get; private set; }
    public HashSet<Guid> SelectedFileIds { get; private set; } = [];

    // Where shift-click ranges start from: the last item clicked without Shift.
    private Guid? _selectionAnchor;

    // Pagination State
    public int CurrentPage { get; private set; } = 1;
    public int PageSize { get; private set; }
    public int TotalFilesCount => _currentFiles.Count;
    public int TotalPages => (int)Math.Ceiling((double)TotalFilesCount / PageSize);

    // Infinite Scroll State (PageSize doubles as the batch size)
    public FileScrollMode ScrollMode { get; private set; }
    public int VisibleFileCount { get; private set; }
    public bool IsInfiniteScroll => ScrollMode == FileScrollMode.Infinite;
    public bool HasMoreFiles => IsInfiniteScroll && FilePagination.HasMoreFiles(VisibleFileCount, TotalFilesCount);

    // Position within the files a view is about to render (not the result set) to focus after
    // the next render; -1 when the view should not move focus. Infinite scroll can append the
    // destination batch in the same render as the key press or settings change, so focus is
    // placed after the DOM exists rather than assumed to be reachable already.
    public int PendingFocusIndex { get; private set; } = -1;

    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public Task InitialLoadTask { get; }
    public IReadOnlyList<FileEntry> InitialFiles { get; private set; } = [];

    public AppState(LibraryService library, ModalService modalService, SettingsManager settings)
    {
        _library = library;
        _modalService = modalService;
        _settings = settings;
        PageSize = _settings.Current.FilesPerPage;
        ScrollMode = _settings.Current.FileScrollMode;
        VisibleFileCount = PageSize;
        _settings.SettingsChanged += OnSettingsChanged;
        _library.LibraryChanged += OnLibraryChanged;
        _library.DuplicatesDetected += OnDuplicatesDetected;
        InitialLoadTask = LoadInitialFilesAsync();
    }

    private void OnSettingsChanged()
    {
        bool changed = false;
        FileScrollMode newMode = _settings.Current.FileScrollMode;
        int newPageSize = _settings.Current.FilesPerPage;

        int modeAnchorIndex = -1;

        if (newMode != ScrollMode)
        {
            // Keep the user where they were. The anchor is the first selected file that is
            // rendered — the file they are looking at — or, with nothing selected there, the
            // top of the rendered viewport. Returning to pagination lands on the page holding
            // the anchor; switching to infinite scroll renders whole batches up to and
            // including the anchor's batch, so the page they came from is still filled below.
            modeAnchorIndex = CurrentAnchorIndex();

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
        }

        if (changed)
        {
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// The [start, end) slice of the filtered result set the views are currently rendering.
    /// </summary>
    private (int Start, int End) RenderedWindow()
    {
        if (IsInfiniteScroll)
        {
            return (0, Math.Clamp(VisibleFileCount, 0, TotalFilesCount));
        }

        int start = (int)Math.Min((long)(CurrentPage - 1) * PageSize, TotalFilesCount);
        return (start, Math.Min(start + PageSize, TotalFilesCount));
    }

    /// <summary>
    /// Index of the file the user is focused on: the first selected file inside the rendered
    /// window, otherwise the first rendered file. Returns <c>0</c> for an empty result set.
    /// </summary>
    private int CurrentAnchorIndex()
    {
        (int start, int end) = RenderedWindow();

        for (int index = start; index < end; index++)
        {
            if (SelectedFileIds.Contains(_currentFiles[index].Id))
            {
                return index;
            }
        }

        return start;
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

    private void OnLibraryChanged()
    {
        _ = RefreshFilesAsync();
    }

    private void OnDuplicatesDetected(IReadOnlyList<FileEntry> duplicates)
    {
        if (duplicates.Count == 1)
        {
            _modalService.Show("Duplicate File", builder =>
            {
                builder.OpenComponent<DuplicateFileModal>(0);
                builder.AddAttribute(1, nameof(DuplicateFileModal.ExistingFile), duplicates[0]);
                builder.CloseComponent();
            });
        }
        else
        {
            _modalService.Show("Duplicate Files", builder =>
            {
                builder.OpenComponent<DuplicatesModal>(0);
                builder.CloseComponent();
            });
        }
    }

    private async Task LoadInitialFilesAsync()
    {
        // Offload the initial library query to a background thread so the first render is not blocked.
        List<FileEntry> files = await Task.Run(() => _library.GetFiles(SearchText, IsMissingOnly, CurrentFolderPath));
        InitialFiles = files;
        ApplyLoadedFiles(files);
    }

    public async Task RefreshFilesAsync()
    {
        // Offload heavy query rendering to a background thread to prevent UI freezes.
        List<FileEntry> files = await Task.Run(() => _library.GetFiles(SearchText, IsMissingOnly, CurrentFolderPath));
        ApplyLoadedFiles(files);
    }

    private void ApplyLoadedFiles(List<FileEntry> files)
    {
        _currentFiles = files;

        // Adjust page if it goes out of bounds after a filter or library change.
        CurrentPage = Math.Clamp(CurrentPage, 1, Math.Max(TotalPages, 1));

        // Keep the loaded batches across refreshes of the same result set (thumbnail work,
        // ratings and tags reload the list); only clamp to the new total and guarantee at
        // least one batch. A filter change resets the count through ResetScrollPosition.
        VisibleFileCount = FilePagination.VisibleCountAfterBatchSizeChange(
            VisibleFileCount, PageSize, TotalFilesCount);

        NotifyStateChanged();
    }

    public IEnumerable<FileEntry> GetPagedFiles()
    {
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

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
    }

    public void SetTagGraphView(bool isGraphView)
    {
        if (IsTagGraphView == isGraphView)
        {
            return;
        }

        IsTagGraphView = isGraphView;
        NotifyStateChanged();
    }

    public void ReturnToTagGraph()
    {
        bool hasChanged = CurrentView != AppView.Tags || !IsTagGraphView;
        CurrentView = AppView.Tags;
        IsTagGraphView = true;

        if (hasChanged)
        {
            NotifyStateChanged();
        }
    }

    public void Undo()
    {
        _library.Undo();
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        CurrentFolderPath = null;
        SelectedFileIds.Clear();
        _selectionAnchor = null;
        ResetScrollPosition();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        _ = RefreshFilesAsync();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        CurrentFolderPath = null;
        SelectedFileIds.Clear();
        _selectionAnchor = null;
        ResetScrollPosition();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        _ = RefreshFilesAsync();
    }

    public void SetFolder(string? path)
    {
        CurrentFolderPath = path;
        SearchText = "";
        IsMissingOnly = false;
        SelectedFileIds.Clear();
        _selectionAnchor = null;
        ResetScrollPosition();

        if (CurrentView != AppView.Folders)
        {
            CurrentView = AppView.Folders;
        }

        _ = RefreshFilesAsync();
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

    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
    {
        if (rangeSelect && TrySelectRange(id, addToSelection: multiSelect))
        {
            NotifySelectionChanged();
            return;
        }

        if (!multiSelect)
        {
            SelectedFileIds.Clear();
            _ = SelectedFileIds.Add(id);
        }
        else
        {
            _ = SelectedFileIds.Contains(id)
                ? SelectedFileIds.Remove(id)
                : SelectedFileIds.Add(id);
        }

        _selectionAnchor = id;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Shift-click: selects everything between the anchor and <paramref name="id"/> in the order the
    /// current filtered result set is showing, including files on other pages. Returns <c>false</c>
    /// when there is nothing to span — no anchor yet or the anchor has been filtered out — so the
    /// caller falls back to treating the action as a plain single selection.
    /// </summary>
    private bool TrySelectRange(Guid id, bool addToSelection)
    {
        if (_selectionAnchor is not { } anchor)
        {
            return false;
        }

        List<Guid> orderedIds = _currentFiles.Select(f => f.Id).ToList();
        IReadOnlyList<Guid> range = SelectionRange.Between(orderedIds, anchor, id);

        if (range.Count == 0)
        {
            return false;
        }

        if (!addToSelection)
        {
            SelectedFileIds.Clear();
        }

        foreach (Guid rangeId in range)
        {
            _ = SelectedFileIds.Add(rangeId);
        }

        // The anchor stays where it was, so repeated shift-clicks extend from the same origin.
        return true;
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        SelectedFileIds.Clear();
        _selectionAnchor = null;

        foreach (Guid id in ids)
        {
            _ = SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        _selectionAnchor = null;

        if (SelectedFileIds.Count <= 0)
        {
            return;
        }

        SelectedFileIds.Clear();
        NotifySelectionChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    private void NotifySelectionChanged()
    {
        OnSelectionChange?.Invoke();
    }

    public void Dispose()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _library.LibraryChanged -= OnLibraryChanged;
        _library.DuplicatesDetected -= OnDuplicatesDetected;
    }
}