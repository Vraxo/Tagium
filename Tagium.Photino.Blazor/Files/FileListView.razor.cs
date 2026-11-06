using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Files;
using Tagium.Core.Thumbnails;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class FileListView : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    private bool _isRenderPending = false;
    private readonly object _renderLock = new();

    protected override void OnInitialized()
    {
        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    private void ReloadData()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnThumbnailGenerated(Guid fileId)
    {
        lock (_renderLock)
        {
            if (_isRenderPending)
            {
                return;
            }
            _isRenderPending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lock (_renderLock)
            {
                _isRenderPending = false;
            }
            await InvokeAsync(StateHasChanged);
        });
    }

    private void TriggerThumbnailIfMissing(FileEntry file, bool hasThumb)
    {
        if (!hasThumb)
        {
            _ = Task.Run(() => ThumbnailService.GenerateThumbnailAsync(file));
        }
    }

    private void OnRowClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnRowDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    /// <summary>
    /// The single tab stop for the grid: the first selected row, otherwise the first row on
    /// the page. Every other row is reachable with the arrow keys.
    /// </summary>
    private Guid? GetTabStopId(IReadOnlyList<FileEntry> files)
    {
        if (files.Count == 0)
        {
            return null;
        }

        foreach (FileEntry file in files)
        {
            if (AppState.SelectedFileIds.Contains(file.Id))
            {
                return file.Id;
            }
        }

        return files[0].Id;
    }

    private void OnRowKeyDown(KeyboardEventArgs e, FileEntry file)
    {
        switch (e.Key)
        {
            case "Enter":
                if (!file.IsMissing)
                {
                    Launcher.OpenFile(file.Path);
                }
                break;

            case " ":
                AppState.SelectFile(file.Id, true, false);
                break;

            case "Escape":
                AppState.ClearSelection();
                break;

            case "ArrowUp":
            case "ArrowDown":
            case "ArrowLeft":
            case "ArrowRight":
            case "Home":
            case "End":
                _ = MoveFocusAsync(e.Key);
                break;
        }
    }

    private async Task MoveFocusAsync(string key)
    {
        string? targetId;

        try
        {
            targetId = await JS.InvokeAsync<string?>("libraryNav.move", key, "#file-list-container", ".file-row");
        }
        catch (JSException)
        {
            // Interop is only used to move DOM focus; losing it must not break selection.
            return;
        }

        if (Guid.TryParse(targetId, out Guid id))
        {
            AppState.SelectFile(id, false, false);
        }
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToShortDateString();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;
    }
}