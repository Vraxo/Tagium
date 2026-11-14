using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Files;
using Tagium.Core.Settings;
using Tagium.Core.Thumbnails;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class FileGrid : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    private DotNetObjectReference<FileGrid>? _objRef;
    private bool _isRenderPending = false;
    private readonly object _renderLock = new();

    protected override void OnInitialized()
    {
        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Settings.SettingsChanged += ReloadData;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gridInteractions.init", _objRef, "file-grid-container");
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

    private void OnCardClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnCardDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    [JSInvokable]
    public void SelectFile(string idStr)
    {
        if (!Guid.TryParse(idStr, out Guid id))
        {
            return;
        }

        AppState.SelectFile(id, false, false);
    }

    [JSInvokable]
    public void SelectFiles(string[] idStrs)
    {
        IEnumerable<Guid> ids = idStrs.Select(s => Guid.TryParse(s, out Guid g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);

        AppState.SetSelection(ids);
    }

    [JSInvokable]
    public void SelectAll()
    {
        AppState.SetSelection(AppState.GetPagedFiles().Select(f => f.Id));
    }

    [JSInvokable]
    public void ClearSelection()
    {
        AppState.ClearSelection();
    }

    /// <summary>
    /// The single tab stop for the whole listbox: the first selected card, otherwise the first
    /// card on the page. Every other card is reachable with the arrow keys.
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

    private static string GetCardLabel(FileEntry file)
    {
        return file.IsMissing ? $"{file.Name} (missing)" : file.Name;
    }

    private void OnCardKeyDown(KeyboardEventArgs e, FileEntry file)
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
                _ = MoveFocusAsync(e.Key, e.ShiftKey);
                break;
        }
    }

    private async Task MoveFocusAsync(string key, bool extendSelection)
    {
        string? targetId;

        try
        {
            targetId = await JS.InvokeAsync<string?>("libraryNav.move", key, "#file-grid-container", ".file-card");
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
                await JS.InvokeVoidAsync("libraryNav.focusPageFile", direction);
            }

            return;
        }

        if (Guid.TryParse(targetId, out Guid id))
        {
            // Shift+arrow extends the same range a shift-click would, from the same anchor.
            AppState.SelectFile(id, false, extendSelection);
        }
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Settings.SettingsChanged -= ReloadData;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;

        _objRef?.Dispose();
    }
}