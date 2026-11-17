using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class DuplicateFileModal
{
    [Inject] private NavigationState Navigation { get; set; } = default!;
    [Inject] private FileBrowser Browser { get; set; } = default!;

    [Parameter, EditorRequired]
    public required FileEntry ExistingFile { get; set; }

    private void ShowExisting()
    {
        Navigation.SetSearch("");
        Navigation.SetMissingOnly(false);
        Browser.SelectFile(ExistingFile.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }
}