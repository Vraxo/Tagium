using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;

namespace Tagium.Photino.Blazor.Files;

public partial class SingleFileInspector
{
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    [Parameter, EditorRequired] public required FileEntry File { get; set; }

    private Guid[] _fileIds = [];

    private bool IsImage => CheckExtension([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"]);
    private bool IsVideo => CheckExtension([".mp4", ".webm", ".mov", ".mkv"]);

    protected override void OnParametersSet()
    {
        _fileIds = [File.Id];
    }

    private bool CheckExtension(string[] exts)
    {
        string ext = Path.GetExtension(File.Path);
        return exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void OpenFile()
    {
        Launcher.OpenFile(File.Path);
    }

    private void ShowInExplorer()
    {
        Launcher.ShowInExplorer(File.Path);
    }
}