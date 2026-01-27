using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    private async Task ImportFiles()
    {
        var paths = FileDialogService.OpenFiles();
        if (paths is { Length: > 0 })
        {
            await Library.ImportBatchAsync(paths);
        }
    }

    private void ToggleTagView()
    {
        AppState.SwitchView(
            AppState.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }
}