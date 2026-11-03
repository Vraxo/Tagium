using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.AppShell;

public partial class ModalHost
{
    private bool IsVisible => ModalService.Content is not null;

    protected override void OnInitialized()
    {
        ModalService.OnShow += StateHasChanged;
        ModalService.OnClose += StateHasChanged;
    }

    private void Close()
    {
        ModalService.Close();
    }

    public void Dispose()
    {
        ModalService.OnShow -= StateHasChanged;
        ModalService.OnClose -= StateHasChanged;
    }
}