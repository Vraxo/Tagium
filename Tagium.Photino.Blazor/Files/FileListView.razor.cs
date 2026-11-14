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
    [Inject] private ContextMenuService ContextMenu { get; set; } = default!;

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
        if ((e.Key == "F10" && e.ShiftKey) || e.Key == "ContextMenu")
        {
            ShowContextMenu(file, 0, 0, isKeyboardInvocation: true);
            return;
        }

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
                _ = MoveFocusAsync(e.Key, e.ShiftKey);
                break;
        }
    }

    public void OpenContextMenu(MouseEventArgs e, FileEntry file)
    {
        ShowContextMenu(file, e.ClientX, e.ClientY, isKeyboardInvocation: false);
    }

    private void ShowContextMenu(FileEntry file, double clientX, double clientY, bool isKeyboardInvocation)
    {
        if (!AppState.SelectedFileIds.Contains(file.Id))
        {
            AppState.SelectFile(file.Id, multiSelect: false, rangeSelect: false);
        }

        IReadOnlyList<Guid> selectedFileIds = AppState.SelectedFileIds.ToList();
        ContextMenu.ShowFile(
            file.Id,
            file.Name,
            file.Path,
            file.IsMissing,
            selectedFileIds,
            clientX,
            clientY,
            isKeyboardInvocation);
    }

    private async Task MoveFocusAsync(string key, bool extendSelection)
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

        if (targetId == "__NEXT_PAGE__" || targetId == "__PREVIOUS_PAGE__")
        {
            int direction = targetId == "__NEXT_PAGE__" ? 1 : -1;
            if (AppState.MovePage(direction, extendSelection))
            {
                try
                {
                    await JS.InvokeVoidAsync("libraryNav.focusPageFile", direction);
                }
                catch (JSException)
                {
                    // The page and selection have moved; focus can be recovered with Tab.
                }
            }

            return;
        }

        if (Guid.TryParse(targetId, out Guid id))
        {
            // Shift+arrow extends the same range a shift-click would, from the same anchor.
            AppState.SelectFile(id, false, extendSelection);
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