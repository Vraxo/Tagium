using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Components.Modals;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Inject] private ModalService ModalService { get; set; } = default!;

    private void OpenLibraryFolders()
    {
        ModalService.Show("Library Folders", builder =>
        {
            builder.OpenComponent<LibraryRootsModal>(0);
            builder.CloseComponent();
        });
    }

    private void ToggleTagView()
    {
        AppView view = AppState.CurrentView == AppView.Tags
            ? AppView.Files
            : AppView.Tags;

        AppState.SwitchView(view);
    }
}