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
        _settings.SettingsChanged += OnSettingsChanged;
        _library.LibraryChanged += OnLibraryChanged;
        _library.DuplicatesDetected += OnDuplicatesDetected;
        InitialLoadTask = LoadInitialFilesAsync();
    }

    private void OnSettingsChanged()
    {
        int newPageSize = _settings.Current.FilesPerPage;
        if (newPageSize == PageSize)
        {
            return;
        }

        CurrentPage = FilePagination.PageAfterSizeChange(CurrentPage, PageSize, newPageSize, TotalFilesCount);
        PageSize = newPageSize;
        NotifyStateChanged();
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

        NotifyStateChanged();
    }

    public IEnumerable<FileEntry> GetPagedFiles()
    {
        return _currentFiles
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize);
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

    /// <summary>
    /// Moves to the adjacent page and selects its first or last file so keyboard focus can
    /// continue naturally across pagination boundaries. Shift-navigation extends the existing
    /// selection anchor across the complete filtered result set.
    /// </summary>
    public bool MovePage(int direction, bool extendSelection = false)
    {
        if (direction == 0 || TotalPages == 0)
        {
            return false;
        }

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
        NotifyStateChanged();
        return true;
    }

    public void SetPageSize(int pageSize)
    {
        if (!FilePagination.IsSupportedPageSize(pageSize) || pageSize == PageSize)
        {
            return;
        }

        // The settings change handler updates the current page and notifies both file views.
        _settings.Update(settings => settings.FilesPerPage = pageSize);
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
        CurrentPage = 1;

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
        CurrentPage = 1;

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
        CurrentPage = 1;

        if (CurrentView != AppView.Folders)
        {
            CurrentView = AppView.Folders;
        }

        _ = RefreshFilesAsync();
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