using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

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

    private bool _scanInProgress = false;
    private int _scanProcessed = 0;
    private int _scanTotal = 0;
    private double _scanProgress = 0;

    private bool _thumbnailQueueActive = false;
    private int _thumbnailProcessed = 0;
    private int _thumbnailTotal = 0;
    private double _thumbnailProgress = 0;

    private bool _tagSyncInProgress = false;
    private int _tagSyncProcessed = 0;
    private int _tagSyncTotal = 0;
    private double _tagSyncProgress = 0;

    private bool _showZoomToast = false;
    private int _lastActiveZoom = 100;
    private CancellationTokenSource? _zoomToastCts;

    private DotNetObjectReference<MainLayout>? _objRef;

    private AppToolbar? AppToolbarRef { get; set; }

    private static readonly int[] ZoomSteps = [75, 90, 100, 110, 120, 130, 150];

    protected override void OnInitialized()
    {
        _lastActiveZoom = SettingsManager.Current.Zoom;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        ScanService.ScanProgress += UpdateScanProgress;
        ScanService.ScanCompleted += ShowScanStatus;
        ThumbnailService.QueueProgressChanged += UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted += HideThumbnailProgress;
        Library.TagSyncProgress += UpdateTagSyncProgress;
        Library.TagSyncCompleted += ShowTagSyncStatus;
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;

        // Queue automatic background pre-generation of all missing thumbnails on startup
        List<FileEntry> allFiles = Library.GetFiles(null, false);
        ThumbnailService.QueueBackgroundGeneration(allFiles);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            _objRef,
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
        await JS.InvokeVoidAsync("zoomTracker.init", _objRef, SettingsManager.Current.Zoom);

        // Safely boot theme configurations after the DOM is fully loaded and interactive
        await ThemeService.InitializeAsync(JS);
        await ThemeService.ApplyThemeAsync(JS, SettingsManager.Current.SelectedTheme);
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
                AppState.SwitchView(AppState.CurrentView == AppView.Files
                    ? AppView.Files
                    : AppView.Tags);
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

    private void UpdateScanProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = true;
            _scanProcessed = current;
            _scanTotal = total;
            _scanProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void ShowScanStatus()
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = false;
            Toast.Show("Library scan complete.", ToastType.Success);
            Library.Refresh();
            StateHasChanged();

            // Automatically queue background pre-generation of newly cataloged files
            List<FileEntry> allFiles = Library.GetFiles(null, false);
            ThumbnailService.QueueBackgroundGeneration(allFiles);
        });
    }

    private void UpdateThumbnailProgress(int processed, int total)
    {
        _ = InvokeAsync(() =>
        {
            _thumbnailQueueActive = true;
            _thumbnailProcessed = processed;
            _thumbnailTotal = total;
            _thumbnailProgress = total > 0 ? (double)processed / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void HideThumbnailProgress()
    {
        _ = InvokeAsync(() =>
        {
            _thumbnailQueueActive = false;
            _thumbnailProcessed = 0;
            _thumbnailTotal = 0;
            _thumbnailProgress = 0;
            StateHasChanged();
        });
    }

    private void UpdateTagSyncProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _tagSyncInProgress = true;
            _tagSyncProcessed = current;
            _tagSyncTotal = total;
            _tagSyncProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void ShowTagSyncStatus()
    {
        _ = InvokeAsync(() =>
        {
            _tagSyncInProgress = false;
            Toast.Show("Tag metadata synchronization complete.", ToastType.Success);
            StateHasChanged();
        });
    }

    private int GetThumbnailBottomOffset()
    {
        return _scanInProgress ? 24 : 0;
    }

    private int GetTagSyncBottomOffset()
    {
        int offset = 0;
        if (_scanInProgress)
        {
            offset += 24;
        }

        if (_thumbnailQueueActive)
        {
            offset += 24;
        }

        return offset;
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

    public void Dispose()
    {
        SettingsManager.SettingsChanged -= OnSettingsChanged;
        ScanService.ScanProgress -= UpdateScanProgress;
        ScanService.ScanCompleted -= ShowScanStatus;
        ThumbnailService.QueueProgressChanged -= UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted -= HideThumbnailProgress;
        Library.TagSyncProgress -= UpdateTagSyncProgress;
        Library.TagSyncCompleted -= ShowTagSyncStatus;
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;

        _zoomToastCts?.Cancel();
        _zoomToastCts?.Dispose();

        // Cancel background task on dispose to prevent thread leaks during shutdown
        ThumbnailService.CancelBackgroundGeneration();

        _objRef?.Dispose();
    }
}