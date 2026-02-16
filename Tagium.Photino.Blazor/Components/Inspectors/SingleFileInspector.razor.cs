using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class SingleFileInspector
{
    [Parameter, EditorRequired] public required FileEntry File { get; set; }
    [Parameter] public List<Tag> AssignedTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private bool IsImage => CheckExtension([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"]);
    private bool IsVideo => CheckExtension([".mp4", ".webm", ".mov", ".mkv"]);

    private bool CheckExtension(string[] exts)
    {
        string ext = Path.GetExtension(File.Path);
        return exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void AddTag(Guid tagId)
    {
        Library.AddTagToFile(File.Id, tagId);
    }

    private void RemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFile(File.Id, tagId);
    }

    private void OpenFile()
    {
        SystemLauncherService.OpenFile(File.Path);
    }

    private void ShowInExplorer()
    {
        SystemLauncherService.ShowInExplorer(File.Path);
    }

    private void RemoveFile()
    {
        Library.RemoveFile(File.Id);
        AppState.ClearSelection();
    }
}