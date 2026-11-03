using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class DuplicatesModal
{
    [Inject] private LibraryService Library { get; set; } = default!;

    private List<List<FileEntry>> _groups = [];

    private int TotalDuplicateFiles => _groups.Sum(g => g.Count);

    protected override void OnInitialized()
    {
        _ = Task.Run(async () =>
        {
            List<List<FileEntry>> groups = await Task.Run(() => Library.GetDuplicateGroups());
            await InvokeAsync(() =>
            {
                _groups = groups;
                StateHasChanged();
            });
        });
    }

    private void ShowFile(FileEntry file)
    {
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);
        AppState.SelectFile(file.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }

    private static string ShortHash(FileEntry file)
    {
        string? hash = file.Hash;
        if (string.IsNullOrEmpty(hash))
        {
            return "";
        }

        return hash.Length <= 12 ? hash : hash[..12];
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:0.##} {units[unit]}";
    }
}
