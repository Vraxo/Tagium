using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SortControls
{
    private void OnSortByChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortBy>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortBy = result);
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortDirection>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortDirection = result);
    }
}