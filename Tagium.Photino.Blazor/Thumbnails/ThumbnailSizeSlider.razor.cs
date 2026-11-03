using Microsoft.AspNetCore.Components;

using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Thumbnails;

public partial class ThumbnailSizeSlider
{
    private void OnInput(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out int val))
        {
            return;
        }

        Settings.Update(s => s.ThumbnailSize = val, save: false);
    }

    private void OnCommit(ChangeEventArgs e)
    {
        Settings.Save();
    }
}