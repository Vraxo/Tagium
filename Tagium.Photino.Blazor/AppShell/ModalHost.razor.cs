using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.AppShell;

public partial class ModalHost : IDisposable
{
    private ElementReference _dialog;
    private bool _focusDialogAfterRender;

    private bool IsVisible => ModalService.Content is not null;

    protected override void OnInitialized()
    {
        ModalService.OnShow += HandleShow;
        ModalService.OnClose += HandleClose;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_focusDialogAfterRender || !IsVisible)
        {
            return;
        }

        _focusDialogAfterRender = false;
        await _dialog.FocusAsync();
    }

    /// <summary>
    /// Moves focus into the dialog so Escape and Tab start from inside it rather than
    /// from whatever was focused behind the backdrop.
    /// </summary>
    private void HandleShow()
    {
        _focusDialogAfterRender = true;
        _ = InvokeAsync(StateHasChanged);
    }

    private void HandleClose()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            Close();
        }
    }

    private void Close()
    {
        ModalService.Close();
    }

    public void Dispose()
    {
        ModalService.OnShow -= HandleShow;
        ModalService.OnClose -= HandleClose;
    }
}
