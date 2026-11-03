using Microsoft.AspNetCore.Components;
using Tagium.Core.Files;
using Tagium.Core.Library;
using Tagium.Core.Maintenance;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class StatsModal
{
    [Inject] private LibraryService Library { get; set; } = default!;

    private LibraryStats? _stats;

    protected override void OnInitialized()
    {
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        LibraryStats stats = await Task.Run(() => Library.GetStats());
        await InvokeAsync(() =>
        {
            _stats = stats;
            StateHasChanged();
        });
    }

    private void Refresh()
    {
        _ = LoadAsync();
    }

    private void Close()
    {
        ModalService.Close();
    }

    private static string KindLabel(string extension)
    {
        if (extension.Length == 0)
        {
            return "—";
        }

        return SupportedFormats.GetKind(extension) switch
        {
            MediaKind.Image => "Image",
            MediaKind.Video => "Video",
            _ => "Other"
        };
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
