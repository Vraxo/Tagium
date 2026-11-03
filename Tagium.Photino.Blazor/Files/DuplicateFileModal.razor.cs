using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class DuplicateFileModal
{
    [Parameter, EditorRequired]
    public required FileEntry ExistingFile { get; set; }

    private void ShowExisting()
    {
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);
        AppState.SelectFile(ExistingFile.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }
}