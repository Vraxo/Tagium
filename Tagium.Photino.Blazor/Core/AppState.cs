using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor;

public enum AppView { Files, Folders, Tags }

public partial class AppState : IDisposable
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
    private readonly SelectionState _selection = new();

    /// <summary>
    /// The selected files. Read-only by contract: mutate selection through
    /// <see cref="SelectFile"/>, <see cref="SetSelection"/> or <see cref="ClearSelection"/> so
    /// the shift-range anchor cannot drift out of sync with the set.
    /// </summary>
    public IReadOnlySet<Guid> SelectedFileIds => _selection.Ids;

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