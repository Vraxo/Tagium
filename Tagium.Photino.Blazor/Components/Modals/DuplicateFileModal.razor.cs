using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class DuplicateFileModal
{
    [Parameter, EditorRequired]
    public required FileEntry ExistingFile { get; set; }

    private void ShowExisting()
    {
        // Clear filters to ensure the file is visible
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);

        // Select the file
        AppState.SelectFile(ExistingFile.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }
}