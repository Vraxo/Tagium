using Microsoft.AspNetCore.Components;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FolderBreadcrumbs : IDisposable
{
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;

    private readonly List<BreadcrumbSegment> _segments = [];

    protected override void OnInitialized()
    {
        AppState.OnChange += OnAppStateChanged;
        UpdateSegments();
    }

    private void OnAppStateChanged()
    {
        UpdateSegments();
        StateHasChanged();
    }

    private void UpdateSegments()
    {
        _segments.Clear();
        string? currentPath = AppState.CurrentFolderPath;
        if (string.IsNullOrEmpty(currentPath))
        {
            return;
        }

        var roots = Library.GetRoots().Where(r => r.IsEnabled).ToList();
        var matchingRoot = roots.FirstOrDefault(r => currentPath.StartsWith(r.Path, StringComparison.OrdinalIgnoreCase));

        if (matchingRoot == null)
        {
            string[] parts = currentPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string accum = "";
            foreach (string part in parts)
            {
                accum = string.IsNullOrEmpty(accum) ? part : accum + Path.DirectorySeparatorChar + part;
                _segments.Add(new BreadcrumbSegment { Name = part, Path = accum });
            }
            return;
        }

        _segments.Add(new BreadcrumbSegment
        {
            Name = Path.GetFileName(matchingRoot.Path) is string name && !string.IsNullOrEmpty(name) ? name : matchingRoot.Path,
            Path = matchingRoot.Path
        });

        if (currentPath.Length > matchingRoot.Path.Length)
        {
            string rel = currentPath[(matchingRoot.Path.Length + 1)..];
            string[] parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            string accum = matchingRoot.Path;

            foreach (string part in parts)
            {
                accum = accum + Path.DirectorySeparatorChar + part;
                _segments.Add(new BreadcrumbSegment { Name = part, Path = accum });
            }
        }
    }

    private void NavigateToSegment(string path)
    {
        AppState.SetFolder(path);
    }

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChanged;
    }

    private class BreadcrumbSegment
    {
        public required string Name { get; set; }
        public required string Path { get; set; }
    }
}