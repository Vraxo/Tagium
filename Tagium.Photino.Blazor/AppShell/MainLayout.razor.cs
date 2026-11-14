using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Tagium.Core;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Settings;
using Tagium.Core.Thumbnails;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Notifications;
using Tagium.Photino.Blazor.Theming;

namespace Tagium.Photino.Blazor.AppShell;

public partial class MainLayout : IDisposable
{
    [Inject] public AppState AppState { get; set; } = default!;
    [Inject] public LibraryService Library { get; set; } = default!;
    [Inject] public SettingsManager SettingsManager { get; set; } = default!;
    [Inject] public ToastService Toast { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
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

    private static readonly int[] ZoomSteps = [50, 67, 75, 90, 100, 110, 120, 133, 150, 175, 200];

    protected override void OnInitialized()
    {
        _lastActiveZoom = SettingsManager.Current.Zoom;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        Appearance.AppearanceChanged += OnAppearanceChanged;
        ScanService.ScanCompleted += ShowScanStatus;
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        // Let the window paint first; the queue reuses AppState's already-backgrounded initial load.
        QueueThumbnailPreGenerationInBackground();

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            _objRef,
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
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
                AppState.Undo();
                break;
            case "f":
                if (AppToolbarRef != null)
                {
                    await AppToolbarRef.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Tags
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "1":
                AppState.SwitchView(AppView.Files);
                await JS.InvokeVoidAsync("libraryNav.focusView", "files");
                break;
            case "2":
                AppState.SwitchView(AppView.Folders);
                await JS.InvokeVoidAsync("libraryNav.focusView", "folders");
                break;
            case "3":
                AppState.SwitchView(AppView.Tags);
                await JS.InvokeVoidAsync("libraryNav.focusView", "tags");
                break;
            case "r":
                await ScanService.ScanAllAsync();
                break;
            case "+":
            case "=":
                AdjustZoom(1);
                break;
            case "-":
                AdjustZoom(-1);
                break;
            case "0":
                ResetZoom();
                break;
        }
    }

    [JSInvokable]
    public void HandleWheelZoom(int direction)
    {
        AdjustZoom(direction);
    }

    [JSInvokable]
    public void PrepareTagPicker(string? focusedFileId)
    {
        if (Guid.TryParse(focusedFileId, out Guid fileId)
            && !AppState.SelectedFileIds.Contains(fileId))
        {
            AppState.SelectFile(fileId, multiSelect: false, rangeSelect: false);
        }
    }

    [JSInvokable]
    public bool NavigateFilePage(int direction)
    {
        return AppState.MovePage(direction);
    }

    [JSInvokable]
    public void SetQuickRating(int rating, string? focusedFileId)
    {
        if (rating is < 0 or > 5)
        {
            return;
        }

        if (Guid.TryParse(focusedFileId, out Guid fileId)
            && !AppState.SelectedFileIds.Contains(fileId))
        {
            AppState.SelectFile(fileId, multiSelect: false, rangeSelect: false);
        }

        if (AppState.SelectedFileIds.Count == 0)
        {
            return;
        }

        Library.SetRatings(
            AppState.SelectedFileIds.ToList(),
            rating == 0 ? null : rating);
    }

    [JSInvokable]
    public void ReturnToTagGraph()
    {
        AppState.ReturnToTagGraph();
    }

    [JSInvokable]
    public void RestoreTagBrowse(string query)
    {
        AppState.SetSearch(query);
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
    public void OnNativeZoomChanged(int rawZoom)
    {
        int closestZoom = 100;
        int minDiff = int.MaxValue;
        foreach (int step in ZoomSteps)
        {
            int diff = Math.Abs(step - rawZoom);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestZoom = step;
            }
        }

        if (SettingsManager.Current.Zoom != closestZoom)
        {
            SettingsManager.Update(s => s.Zoom = closestZoom);
        }
    }

    private void AdjustZoom(int direction)
    {
        int currentZoom = SettingsManager.Current.Zoom;
        int currentIndex = Array.IndexOf(ZoomSteps, currentZoom);

        if (currentIndex == -1)
        {
            int closestIndex = 0;
            int minDiff = int.MaxValue;
            for (int i = 0; i < ZoomSteps.Length; i++)
            {
                int diff = Math.Abs(ZoomSteps[i] - currentZoom);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closestIndex = i;
                }
            }
            currentIndex = closestIndex;
        }

        int newIndex = currentIndex + direction;
        if (newIndex >= 0 && newIndex < ZoomSteps.Length)
        {
            int targetZoom = ZoomSteps[newIndex];
            SettingsManager.Update(s => s.Zoom = targetZoom);
            Program.MainWindow?.SetZoom(targetZoom);
        }
    }

    private void ResetZoom()
    {
        SettingsManager.Update(s => s.Zoom = 100);
        Program.MainWindow?.SetZoom(100);
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
        QueueThumbnailPreGenerationInBackground(refreshLibrary: true);
    }

    private void QueueThumbnailPreGenerationInBackground(bool refreshLibrary = false)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                string timingMessage = refreshLibrary
                    ? "Loading library files for thumbnail pre-generation"
                    : "Waiting for initial library load before thumbnail pre-generation";
                using LogTiming timer = LogTiming.Start(Logger, timingMessage);

                IReadOnlyList<FileEntry> allFiles;
                if (refreshLibrary)
                {
                    allFiles = Library.GetFiles(null, false);
                }
                else
                {
                    await AppState.InitialLoadTask.ConfigureAwait(false);
                    allFiles = AppState.InitialFiles;
                }

                Logger.LogInformation("Loaded {Count} files for background thumbnail pre-generation", allFiles.Count);
                ThumbnailService.QueueBackgroundGeneration(allFiles);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to queue background thumbnail pre-generation");
            }
        });
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void ReloadCurrentView()
    {
        AppState.SetSearch("");
        AppState.ClearSelection();
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
        Appearance.AppearanceChanged -= OnAppearanceChanged;
        ScanService.ScanCompleted -= ShowScanStatus;
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;

        _zoomToastCts?.Cancel();
        _zoomToastCts?.Dispose();

        // Cancel background task on dispose to prevent thread leaks during shutdown
        ThumbnailService.CancelBackgroundGeneration();

        _objRef?.Dispose();
    }
}