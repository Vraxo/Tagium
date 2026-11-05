using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;

namespace Tagium.Photino.Blazor.Files;

public partial class SingleFileInspector
{
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;
    [Inject] private LocalVideoServer VideoServer { get; set; } = default!;

    [Parameter, EditorRequired] public required FileEntry File { get; set; }

    private string VideoPreviewUrl => VideoServer.GetVideoUrl(File.Id);

    private Guid[] _fileIds = [];

    private MediaKind Kind => SupportedFormats.GetKind(Path.GetExtension(File.Path));
    private bool IsImage => Kind == MediaKind.Image;
    private bool IsVideo => Kind == MediaKind.Video;

    private string TypeLabel
    {
        get
        {
            string ext = Path.GetExtension(File.Path);
            string kindWord = Kind switch
            {
                MediaKind.Image => "image",
                MediaKind.Video => "video",
                _ => "file"
            };
            return ext.Length == 0 ? kindWord : $"{ext.TrimStart('.').ToUpperInvariant()} {kindWord}";
        }
    }

    private static string ShortHash(string hash) => hash.Length <= 12 ? hash : hash[..12];

    protected override void OnParametersSet()
    {
        _fileIds = [File.Id];
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