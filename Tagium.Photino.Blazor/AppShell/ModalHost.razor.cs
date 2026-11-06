using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.AppShell;

public partial class ModalHost : IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ElementReference _dialog;
    private bool _focusDialogAfterRender;
    private bool _restoreFocusAfterRender;

    private bool IsVisible => ModalService.Content is not null;

    protected override void OnInitialized()
    {
        ModalService.OnShow += HandleShow;
        ModalService.OnClose += HandleClose;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusDialogAfterRender)
        {
            _focusDialogAfterRender = false;

            if (!IsVisible)
            {
                return;
            }

            // Trap first, then focus: there is no moment where Tab can walk out of
            // the dialog because the trap is not installed yet.
            try
            {
                await JS.InvokeVoidAsync("modalFocus.trap", _dialog);
                await _dialog.FocusAsync();
            }
            catch (JSException)
            {
                // The script or the dialog element went away between render and interop.
            }

            return;
        }

        if (_restoreFocusAfterRender)
        {
            _restoreFocusAfterRender = false;

            if (IsVisible)
            {
                return;
            }

            await ReleaseFocusAsync();
        }
    }

    /// <summary>
    /// Remembers what had focus before the dialog opened and moves focus into the
    /// dialog, so Escape and Tab start from inside it rather than from whatever was
    /// focused behind the backdrop. The capture has to happen before the re-render,
    /// because rendering the dialog can replace the node that opened it.
    /// </summary>
    private void HandleShow()
    {
        _ = InvokeAsync(async () =>
        {
            try
            {
                await JS.InvokeVoidAsync("modalFocus.capture");
            }
            catch (JSException)
            {
                // Losing the opener only costs the focus restore, never the dialog.
                // JSDisconnectedException derives from JSException, so shutdown is covered too.
            }

            _focusDialogAfterRender = true;
            StateHasChanged();
        });
    }

    private void HandleClose()
    {
        _restoreFocusAfterRender = true;
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

    private async Task ReleaseFocusAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("modalFocus.release");
        }
        catch (JSException)
        {
            // Includes JSDisconnectedException: the only cost is a dialog that closes
            // without handing focus back, which never happens while the app is alive.
        }
    }

    public async ValueTask DisposeAsync()
    {
        ModalService.OnShow -= HandleShow;
        ModalService.OnClose -= HandleClose;

        // Untrack the keydown listener if the host is torn down while a dialog is open.
        await ReleaseFocusAsync();
    }
}
