using Microsoft.AspNetCore.Components;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class StatusBar : IDisposable
{
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;

    private bool _scanInProgress;
    private int _scanProcessed;
    private int _scanTotal;
    private double _scanProgress;

    private bool _thumbnailQueueActive;
    private int _thumbnailProcessed;
    private int _thumbnailTotal;
    private double _thumbnailProgress;

    private bool _tagSyncInProgress;
    private int _tagSyncProcessed;
    private int _tagSyncTotal;
    private double _tagSyncProgress;

    protected override void OnInitialized()
    {
        ScanService.ScanProgress += UpdateScanProgress;
        ScanService.ScanCompleted += HideScanProgress;
        ThumbnailService.QueueProgressChanged += UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted += HideThumbnailProgress;
        Library.TagSyncProgress += UpdateTagSyncProgress;
        Library.TagSyncCompleted += HideTagSyncProgress;
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

    private void HideScanProgress()
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            _scanProgress = 0;
            StateHasChanged();
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

    private void HideTagSyncProgress()
    {
        _ = InvokeAsync(() =>
        {
            _tagSyncInProgress = false;
            _tagSyncProcessed = 0;
            _tagSyncTotal = 0;
            _tagSyncProgress = 0;
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

    public void Dispose()
    {
        ScanService.ScanProgress -= UpdateScanProgress;
        ScanService.ScanCompleted -= HideScanProgress;
        ThumbnailService.QueueProgressChanged -= UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted -= HideThumbnailProgress;
        Library.TagSyncProgress -= UpdateTagSyncProgress;
        Library.TagSyncCompleted -= HideTagSyncProgress;
    }
}