using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Library;
using Tagium.Photino.Blazor;
using Tagium.Photino.Blazor.Files;
using Tagium.Photino.Blazor.Notifications;

namespace Tagium.Photino.Blazor.AppShell;

public partial class ContextMenuHost : IDisposable
{
    [Inject] private ContextMenuService ContextMenu { get; set; } = default!;
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _menu;
    private bool _focusMenuAfterRender;
    private bool _restoreFocusAfterRender;
    private bool _skipFocusRestore;

    protected override void OnInitialized()
    {
        ContextMenu.OnShow += HandleShow;
        ContextMenu.OnClose += HandleClose;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusMenuAfterRender && ContextMenu.IsOpen)
        {
            _focusMenuAfterRender = false;
            try
            {
                await JS.InvokeVoidAsync(
                    "libraryContextMenu.position",
                    _menu,
                    ContextMenu.Kind.ToString().ToLowerInvariant(),
                    ContextMenu.TargetKey,
                    ContextMenu.ClientX,
                    ContextMenu.ClientY,
                    ContextMenu.IsKeyboardInvocation);
                await JS.InvokeVoidAsync("libraryContextMenu.focusFirst", _menu);
            }
            catch (JSException)
            {
                // The menu remains usable through its normal tab sequence if positioning fails.
            }

            return;
        }

        if (_restoreFocusAfterRender)
        {
            _restoreFocusAfterRender = false;
            try
            {
                bool restored = await JS.InvokeAsync<bool>(
                    "libraryContextMenu.focusTarget",
                    ContextMenu.Kind.ToString().ToLowerInvariant(),
                    ContextMenu.TargetKey);

                if (!restored && ContextMenu.Kind == ContextMenuKind.File)
                {
                    await JS.InvokeVoidAsync("libraryNav.focusSelectedFile");
                }
            }
            catch (JSException)
            {
                // The action completed; failed focus restoration does not undo it.
            }
        }
    }

    private void HandleShow()
    {
        _restoreFocusAfterRender = false;
        _focusMenuAfterRender = true;
        _ = InvokeAsync(StateHasChanged);
    }

    private void HandleClose()
    {
        _restoreFocusAfterRender = !_skipFocusRestore;
        _skipFocusRestore = false;
        _ = InvokeAsync(StateHasChanged);
    }

    private string GetMenuLabel()
    {
        return ContextMenu.Kind switch
        {
            ContextMenuKind.File => "File actions",
            ContextMenuKind.Folder => "Folder actions",
            ContextMenuKind.Tag => "Tag actions",
            _ => "Context menu"
        };
    }

    private string GetMenuHeading()
    {
        string heading = ContextMenu.Kind switch
        {
            ContextMenuKind.File => ContextMenu.SelectedFileIds.Count > 1
                ? $"{ContextMenu.SelectedFileIds.Count} selected files"
                : ContextMenu.Name,
            ContextMenuKind.Folder => ContextMenu.Name,
            ContextMenuKind.Tag => ContextMenu.Name,
            _ => string.Empty
        };

        return heading.Length > 42 ? $"{heading[..39]}…" : heading;
    }

    private async Task HandleMenuKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
                await JS.InvokeVoidAsync("libraryContextMenu.moveFocus", _menu, 1);
                break;
            case "ArrowUp":
                await JS.InvokeVoidAsync("libraryContextMenu.moveFocus", _menu, -1);
                break;
            case "Home":
                await JS.InvokeVoidAsync("libraryContextMenu.focusEdge", _menu, "first");
                break;
            case "End":
                await JS.InvokeVoidAsync("libraryContextMenu.focusEdge", _menu, "last");
                break;
            case "Escape":
                Close();
                break;
        }
    }

    private void OpenFile()
    {
        if (!ContextMenu.IsMissing)
        {
            Launcher.OpenFile(ContextMenu.Path);
        }

        Close();
    }

    private void ShowFileInExplorer()
    {
        if (!ContextMenu.IsMissing)
        {
            Launcher.ShowInExplorer(ContextMenu.Path);
        }

        Close();
    }

    private void OpenFolderInExplorer()
    {
        Launcher.OpenFolder(ContextMenu.Path);
        Close();
    }

    private void BrowseFolder()
    {
        AppState.SetFolder(ContextMenu.Path);
        Close();
    }

    private void FilterFilesByTag()
    {
        AppState.SetSearch(ContextMenu.Name);
        Close();
    }

    private void ManageTags()
    {
        AppState.SwitchView(AppView.Tags);
        Close();
    }

    private void SetRating(int rating)
    {
        if (ContextMenu.SelectedFileIds.Count > 0)
        {
            Library.SetRatings(
                ContextMenu.SelectedFileIds,
                rating == 0 ? null : rating);
        }

        Close();
    }

    private async Task OpenTagPicker()
    {
        _skipFocusRestore = true;
        ContextMenu.Close();

        try
        {
            await JS.InvokeVoidAsync("libraryNav.focusTagPicker");
        }
        catch (JSException)
        {
            // The selected files remain selected; T can open the picker again.
        }
    }

    private async Task CopyPath()
    {
        await CopyText(ContextMenu.Path, "Path copied.");
    }

    private async Task CopyTagName()
    {
        await CopyText(ContextMenu.Name, "Tag name copied.");
    }

    private async Task CopyText(string value, string successMessage)
    {
        try
        {
            await JS.InvokeVoidAsync("libraryContextMenu.copyText", value);
            Toast.Show(successMessage, ToastType.Success);
        }
        catch (JSException)
        {
            Toast.Show("Could not access the clipboard.", ToastType.Error);
        }

        Close();
    }

    private void Close()
    {
        ContextMenu.Close();
    }

    public void Dispose()
    {
        ContextMenu.OnShow -= HandleShow;
        ContextMenu.OnClose -= HandleClose;
    }
}
