using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Obsolete]
    private async Task ImportFiles()
    {
        string[]? paths = FileDialogService.OpenFiles();

        if (paths is { Length: > 0 })
        {
            await ImportService.ImportBatchAsync(paths);
        }
    }

    private void ToggleTagView()
    {
        AppView view = AppState.CurrentView == AppView.Tags
            ? AppView.Files
            : AppView.Tags;

        AppState.SwitchView(view);
    }
}