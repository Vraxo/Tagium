using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class MultiFileInspector
{
    [Parameter] public List<Tag> CommonTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private int _selectKey = 0;

    private void OnTagSelected(ChangeEventArgs e)
    {
        if (Guid.TryParse(e.Value?.ToString(), out Guid tid))
        {
            Library.AddTagToFiles(AppState.SelectedFileIds, tid);
        }
        _selectKey++;
    }

    private void BulkRemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFiles(AppState.SelectedFileIds, tagId);
    }
}