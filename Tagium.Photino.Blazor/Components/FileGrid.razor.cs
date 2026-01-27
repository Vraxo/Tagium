using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileGrid : IDisposable
{
    private List<FileEntry> Files = [];
    private DotNetObjectReference<FileGrid>? _objRef;

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
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
        LoadFiles();
        _ = InvokeAsync(StateHasChanged);
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
    }

    private void OnCardClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private static void OnCardDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        SystemLauncherService.OpenFile(file.Path);
    }

    [JSInvokable]
    public void SelectFile(string idStr)
    {
        if (Guid.TryParse(idStr, out var id))
        {
            AppState.SelectFile(id, false, false);
        }
    }

    [JSInvokable]
    public void SelectFiles(string[] idStrs)
    {
        var ids = idStrs.Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null).Where(g => g.HasValue).Select(g => g!.Value);
        AppState.SetSelection(ids);
    }

    [JSInvokable]
    public void SelectAll()
    {
        AppState.SetSelection(Files.Select(f => f.Id));
    }

    [JSInvokable]
    public void ClearSelection()
    {
        AppState.ClearSelection();
    }

    [JSInvokable]
    public void OnEnterKey()
    {
        if (AppState.SelectedFileIds.Count != 1)
        {
            return;
        }

        Guid id = AppState.SelectedFileIds.First();
        FileEntry? file = Files.FirstOrDefault(f => f.Id == id);

        if (file is null || file.IsMissing)
        {
            return;
        }

        SystemLauncherService.OpenFile(file.Path);
    }

    [JSInvokable]
    public void OnDeleteKey()
    {
        if (AppState.SelectedFileIds.Count == 0)
        {
            return;
        }

        Library.RemoveFiles(AppState.SelectedFileIds);
        AppState.ClearSelection();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;

        _objRef?.Dispose();
    }
}