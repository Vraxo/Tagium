using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Files;

namespace Tagium.Photino.Blazor;

/// <summary>
/// The coordinator between the two halves of the workspace state, and the only type that
/// knows they are related.
///
/// <see cref="NavigationState"/> owns what is being shown — the active view, the search text,
/// the missing-only and folder filters. <see cref="FileBrowser"/> owns the file list underneath
/// it — pages, batches, selection and focus. Neither references the other. This class is where
/// they meet, because exactly three things cross the seam:
///
///  * a filter change has to re-query the library and send the list back to the top;
///  * a change to the browsing mode or page size is a persisted setting, so it leaves through
///    the settings manager and comes back through <see cref="OnSettingsChanged"/>;
///  * a library change (scan, tag edit, undo) invalidates the loaded list.
///
/// Components that only navigate bind to <see cref="NavigationState"/>; components that only
/// render files bind to <see cref="FileBrowser"/>. Both are injected directly, so a component
/// that does not care about the file list cannot accidentally depend on it.
/// </summary>
public sealed class AppState : IDisposable
{
    private readonly LibraryService _library;
    private readonly ModalService _modalService;
    private readonly SettingsManager _settings;

    public AppState(
        LibraryService library,
        ModalService modalService,
        SettingsManager settings,
        NavigationState navigation,
        FileBrowser browser)
    {
        _library = library;
        _modalService = modalService;
        _settings = settings;
        Navigation = navigation;
        Browser = browser;

        // A new result set always starts at the top, so a filter change resets the viewport
        // before the reload lands.
        navigation.OnFilterChanged += OnFilterChanged;
        navigation.OnFolderChanged += OnFolderChanged;
        browser.NeedsReload += RefreshFilesAsync;
        browser.NeedsPageSizeChange += OnPageSizeRequested;

        _settings.SettingsChanged += OnSettingsChanged;
        _library.LibraryChanged += OnLibraryChanged;
        _library.DuplicatesDetected += OnDuplicatesDetected;

        InitialLoadTask = LoadInitialFilesAsync();
    }

    /// <summary>What is being shown. Injected separately by most components.</summary>
    public NavigationState Navigation { get; }

    /// <summary>The file list underneath the view. Injected separately by the file views.</summary>
    public FileBrowser Browser { get; }

    /// <summary>Completes when the first query has been applied; used by the thumbnail warm-up.</summary>
    public Task InitialLoadTask { get; }

    /// <summary>The result set as first loaded, for callers that want it without re-querying.</summary>
    public IReadOnlyList<FileEntry> InitialFiles { get; private set; } = [];

    // --- Library wiring -------------------------------------------------------------------

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
        List<FileEntry> files = await Task.Run(() => QueryLibrary()).ConfigureAwait(false);
        InitialFiles = files;
        Browser.ApplyLoadedFiles(files);
    }

    public async Task RefreshFilesAsync()
    {
        // Offload heavy query rendering to a background thread to prevent UI freezes.
        List<FileEntry> files = await Task.Run(() => QueryLibrary()).ConfigureAwait(false);
        Browser.ApplyLoadedFiles(files);
    }

    private List<FileEntry> QueryLibrary() =>
        _library.GetFiles(Navigation.SearchText, Navigation.IsMissingOnly, Navigation.CurrentFolderPath);

    // --- Crossing the seam ----------------------------------------------------------------

    private void OnFilterChanged() => ResetAndReload();

    private void OnFolderChanged() => ResetAndReload();

    private void ResetAndReload()
    {
        Browser.ClearSelectionSilently();
        Browser.ResetScrollPosition();
        _ = RefreshFilesAsync();
    }

    /// <summary>
    /// The page size is a persisted setting shared by both file views, so a request to change
    /// it goes through the settings manager. <see cref="OnSettingsChanged"/> then applies the
    /// new size exactly once, which is what keeps the two views in step.
    /// </summary>
    private void OnPageSizeRequested(int pageSize) =>
        _settings.Update(settings => settings.FilesPerPage = pageSize);

    private void OnSettingsChanged()
    {
        FileScrollMode newMode = _settings.Current.FileScrollMode;
        int newPageSize = _settings.Current.FilesPerPage;

        // ApplySettings is idempotent and notifies only when something actually changed, so a
        // settings save that touched unrelated preferences is a no-op here.
        _ = Browser.ApplySettings(newMode, newPageSize);
    }

    public void Dispose()
    {
        Navigation.OnFilterChanged -= OnFilterChanged;
        Navigation.OnFolderChanged -= OnFolderChanged;
        Browser.NeedsReload -= RefreshFilesAsync;
        Browser.NeedsPageSizeChange -= OnPageSizeRequested;

        _settings.SettingsChanged -= OnSettingsChanged;
        _library.LibraryChanged -= OnLibraryChanged;
        _library.DuplicatesDetected -= OnDuplicatesDetected;
    }
}
