using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class FileDropZone
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    private bool _isDragging = false;
    private bool _isProcessing = false;
    private int _dragCounter = 0;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
    }

    private void OnDragEnter(DragEventArgs e)
    {
        _dragCounter++;

        if (_dragCounter != 1)
        {
            return;
        }

        _isDragging = true;
    }

    private void OnDragLeave(DragEventArgs e)
    {
        _dragCounter--;

        if (_dragCounter > 0)
        {
            return;
        }

        _dragCounter = 0;
        _isDragging = false;
    }

    [Obsolete]
    private async Task HandleFileDrop(InputFileChangeEventArgs e)
    {
        _isProcessing = true;
        StateHasChanged();

        var files = e.GetMultipleFiles(500);
        string[] importedPaths = await FileImportService.SaveDroppedFilesAsync(files);

        _dragCounter = 0;
        _isDragging = false;
        _isProcessing = false;
        StateHasChanged();

        if (importedPaths.Length == 0)
        {
            return;
        }

        await ImportService.ImportBatchAsync(importedPaths);
    }

    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
    }
}