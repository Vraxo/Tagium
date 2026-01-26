using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class MultiFileInspector
{
    [Parameter] public List<Tag> CommonTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private void BulkAddTag(long tagId)
    {
        Library.AddTagToFiles(AppState.SelectedFileIds, tagId);
    }

    private void BulkRemoveTag(long tagId)
    {
        Library.RemoveTagFromFiles(AppState.SelectedFileIds, tagId);
    }

    private void RemoveMultipleFiles()
    {
        Library.RemoveFiles(AppState.SelectedFileIds);
        AppState.ClearSelection();
    }
}