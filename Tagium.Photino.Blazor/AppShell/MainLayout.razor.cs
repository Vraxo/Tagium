using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Metadata;
using Tagium.Core.Settings;
using Tagium.Core.Thumbnails;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Notifications;
using Tagium.Photino.Blazor.Theming;

namespace Tagium.Photino.Blazor.AppShell;

public partial class MainLayout : IDisposable
{
    [Inject] public AppState AppState { get; set; } = default!;
    [Inject] public NavigationState NavigationState { get; set; } = default!;
    [Inject] public FileBrowser FileBrowser { get; set; } = default!;
    [Inject] public LibraryService Library { get; set; } = default!;
    [Inject] public SettingsManager SettingsManager { get; set; } = default!;
    [Inject] public ToastService Toast { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;
    [Inject] private AppearanceService Appearance { get; set; } = default!;
    [Inject] private WindowsAccentColorService WindowsAccentColor { get; set; } = default!;
    [Inject] private ILogger<MainLayout> Logger { get; set; } = default!;

    private bool _showZoomToast = false;
    private int _lastActiveZoom = 100;
    private CancellationTokenSource? _zoomToastCts;

    private DotNetObjectReference<MainLayout>? _objRef;
    private bool _isSubscribedToAccentChanges;
    private bool _appearanceInitialized;

    private AppToolbar? AppToolbarRef { get; set; }

    private ZoomController _zoom = default!;
    private ThumbnailWarmup _thumbnailWarmup = default!;

    protected override void OnInitialized()
    {
        _zoom = new ZoomController(SettingsManager, zoom => Program.MainWindow?.SetZoom(zoom));
        _thumbnailWarmup = new ThumbnailWarmup(AppState, Library, ThumbnailService, Logger);
        _lastActiveZoom = SettingsManager.Current.Zoom;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        WindowEventHub.SettingsSaved += OnExternalSettingsSaved;
        Appearance.AppearanceChanged += OnAppearanceChanged;
        ScanService.ScanCompleted += ShowScanStatus;
        NavigationState.OnChange += StateHasChanged;
        FileBrowser.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;
        Library.TagSyncFailed += OnTagSyncFailed;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            // Batches appended by infinite scroll start a fresh observation window.
            if (FileBrowser.IsInfiniteScroll)
            {
                await ObserveInfiniteScrollAsync();
            }

            return;
        }

        // Let the window paint first; the queue reuses AppState's already-backgrounded initial load.
        _thumbnailWarmup.Queue();

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            _objRef,
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
        await JS.InvokeVoidAsync("libraryScroll.setDotNetRef", _objRef);
        await JS.InvokeVoidAsync("tagiumNavigation.init", _objRef);
        await JS.InvokeVoidAsync("zoomTracker.init", _objRef, SettingsManager.Current.Zoom);

        // Safely boot theme configurations after the DOM is fully loaded and interactive.
        await ThemeService.InitializeAsync(JS);
        await ApplyAppearanceAsync();
        _appearanceInitialized = true;

        if (Program.UsesNativeMica)
        {
            WindowsAccentColor.ColorChanged += OnWindowsAccentColorChanged;
            _isSubscribedToAccentChanges = true;
        }
    }

    // Only the JSInterop call needs guarding; a render must never throw because of it.
    private async Task ObserveInfiniteScrollAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("libraryScroll.observe");
        }
        catch (JSException)
        {
            // The observer is progressive enhancement: without it the user can still
            // page with Page Down and the arrow keys.
        }
    }

    [JSInvokable]
    public async Task HandleGlobalKeyDown(string key, bool ctrlOrMeta, bool shiftKey)
    {
        if (!ctrlOrMeta)
        {
            return;
        }

        switch (key.ToLower())
        {
            case "z":
                Library.Undo();
                break;
            case "f":
                if (AppToolbarRef != null)
                {
                    await AppToolbarRef.FocusSearchAsync();
                }
                break;
            case "t":
                NavigationState.SwitchView(NavigationState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "1":
                NavigationState.SwitchView(AppView.Files);
                await JS.InvokeVoidAsync("libraryNav.focusView", "files");
                break;
            case "2":
                NavigationState.SwitchView(AppView.Folders);
                await JS.InvokeVoidAsync("libraryNav.focusView", "folders");
                break;
            case "3":
                NavigationState.SwitchView(AppView.Tags);
                await JS.InvokeVoidAsync("libraryNav.focusView", "tags");
                break;
            case "r":
                await ScanService.ScanAllAsync();
                break;
            case "+":
            case "=":
                _zoom.Adjust(1);
                break;
            case "-":
                _zoom.Adjust(-1);
                break;
            case "0":
                _zoom.Reset();
                break;
        }
    }

    [JSInvokable]
    public void HandleWheelZoom(int direction)
    {
        _zoom.Adjust(direction);
    }

    [JSInvokable]
    public void ShowKeyboardShortcuts()
    {
        if (ModalService.Content is not null)
        {
            return;
        }

        ModalService.Show("Keyboard Shortcuts", builder =>
        {
            builder.OpenComponent<KeyboardShortcutsModal>(0);
            builder.CloseComponent();
        });
    }

    [JSInvokable]
    public void PrepareTagPicker(string? focusedFileId)
    {
        if (Guid.TryParse(focusedFileId, out Guid fileId)
            && !FileBrowser.SelectedFileIds.Contains(fileId))
        {
            FileBrowser.SelectFile(fileId, multiSelect: false, rangeSelect: false);
        }
    }

    [JSInvokable]
    public bool NavigateFilePage(int direction, bool extendSelection)
    {
        return FileBrowser.MoveBatch(direction, extendSelection);
    }

    /// <summary>
    /// Infinite scroll: the sentinel entered the viewport. Loading is idempotent — the
    /// observer can fire again before the appended batch has rendered.
    /// </summary>
    [JSInvokable]
    public bool LoadMoreFiles()
    {
        return FileBrowser.LoadMoreFiles();
    }

    [JSInvokable]
    public void SetQuickRating(int rating, string? focusedFileId)
    {
        if (rating is < 0 or > 5)
        {
            return;
        }

        if (Guid.TryParse(focusedFileId, out Guid fileId)
            && !FileBrowser.SelectedFileIds.Contains(fileId))
        {
            FileBrowser.SelectFile(fileId, multiSelect: false, rangeSelect: false);
        }

        if (FileBrowser.SelectedFileIds.Count == 0)
        {
            return;
        }

        Library.SetRatings(
            FileBrowser.SelectedFileIds.ToList(),
            rating == 0 ? null : rating);
    }

    [JSInvokable]
    public void ReturnToTagGraph()
    {
        NavigationState.ReturnToTagGraph();
    }

    [JSInvokable]
    public void RestoreTagBrowse(string query)
    {
        NavigationState.SetSearch(query);
    }

    [JSInvokable]
    public void SaveLeftSidebarWidth(int width)
    {
        SettingsManager.Update(s => s.LeftSidebarWidth = width);
    }

    [JSInvokable]
    public void SaveRightSidebarWidth(int width)
    {
        SettingsManager.Update(s => s.RightSidebarWidth = width);
    }

    [JSInvokable]
    public void OnNativeZoomChanged(int rawZoom) => _zoom.OnNativeZoomChanged(rawZoom);

    /// <summary>
    /// The Settings window saved: re-read the file so the browsing mode and page/batch size
    /// apply to the live workspace without a restart.
    /// </summary>
    private void OnExternalSettingsSaved()
    {
        _ = InvokeAsync(() =>
        {
            SettingsManager.Reload();
            StateHasChanged();
        });
    }

    private void OnSettingsChanged()
    {
        if (SettingsManager.Current.Zoom != _lastActiveZoom)
        {
            _lastActiveZoom = SettingsManager.Current.Zoom;
            TriggerZoomToast();
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private void TriggerZoomToast()
    {
        _showZoomToast = true;

        _zoomToastCts?.Cancel();
        _zoomToastCts = new CancellationTokenSource();
        CancellationToken token = _zoomToastCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
                await InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _showZoomToast = false;
                        StateHasChanged();
                    }
                });
            }
            catch (TaskCanceledException)
            {
                // Gracefully catch cancellation
            }
        }, token);
    }

    private void ShowScanStatus()
    {
        _ = InvokeAsync(() =>
        {
            Toast.Show("Library scan complete.", ToastType.Success);
            Library.Refresh();
            StateHasChanged();
        });

        // Automatically queue newly cataloged files without blocking the renderer.
        _thumbnailWarmup.Queue(refreshLibrary: true);
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// The catalog is authoritative either way, but the files themselves kept their old embedded
    /// metadata, so the user is told rather than left believing the write landed.
    /// </summary>
    private void OnTagSyncFailed(int failedCount, FileSyncBatch _)
    {
        // The batch kind is already in the status bar's wording; the toast only needs the count.
        _ = InvokeAsync(() => Toast.Show(
            failedCount == 1
                ? "Embedded metadata could not be written to 1 file. The catalog was updated; the file itself was not."
                : $"Embedded metadata could not be written to {failedCount} files. The catalog was updated; the files themselves were not.",
            ToastType.Error,
            durationMs: 8000));
    }

    private void ReloadCurrentView()
    {
        NavigationState.SetSearch("");
        FileBrowser.ClearSelection();
        Library.Refresh();
    }

    private void OnAppearanceChanged()
    {
        if (!_appearanceInitialized)
        {
            return;
        }

        _ = InvokeAsync(ApplyAppearanceAsync);
    }

    private async Task ApplyAppearanceAsync()
    {
        await ThemeService.ApplyThemeAsync(JS, SettingsManager.Current.SelectedTheme);
        await JS.InvokeVoidAsync("themeManager.applyCornerStyle", SettingsManager.Current.CornerStyle.ToString());
        await JS.InvokeVoidAsync("themeManager.applyInterfaceStyle", Program.ActiveInterfaceStyle.ToString());
        await JS.InvokeVoidAsync("themeManager.applyNativeMica", Program.UsesNativeMica && WindowsMicaWindow.IsActive);

        if (Program.UsesNativeMica)
        {
            await JS.InvokeVoidAsync("themeManager.applyWindowsAccentColor", WindowsAccentColor.CurrentColor);
        }
    }

    private void OnWindowsAccentColorChanged(string color)
    {
        _ = InvokeAsync(async () =>
        {
            if (Program.UsesNativeMica)
            {
                await JS.InvokeVoidAsync("themeManager.applyWindowsAccentColor", color);
            }
        });
    }

    public void Dispose()
    {
        if (_isSubscribedToAccentChanges)
        {
            WindowsAccentColor.ColorChanged -= OnWindowsAccentColorChanged;
        }

        SettingsManager.SettingsChanged -= OnSettingsChanged;
        WindowEventHub.SettingsSaved -= OnExternalSettingsSaved;
        Appearance.AppearanceChanged -= OnAppearanceChanged;
        ScanService.ScanCompleted -= ShowScanStatus;
        NavigationState.OnChange -= StateHasChanged;
        FileBrowser.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;
        Library.TagSyncFailed -= OnTagSyncFailed;

        _zoomToastCts?.Cancel();
        _zoomToastCts?.Dispose();

        _thumbnailWarmup?.Dispose();

        _objRef?.Dispose();
    }
}