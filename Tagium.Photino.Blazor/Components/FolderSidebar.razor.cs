using Microsoft.AspNetCore.Components;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FolderSidebar : IDisposable
{
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;

    private List<FolderNode> _rootNodes = [];

    protected override void OnInitialized()
    {
        LoadRoots();
        AppState.OnChange += OnAppStateChanged;
        Library.LibraryChanged += OnLibraryChanged;
    }

    private void OnAppStateChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(() =>
        {
            LoadRoots();
            StateHasChanged();
        });
    }

    private void LoadRoots()
    {
        var roots = Library.GetRoots().Where(r => r.IsEnabled).ToList();
        List<FolderNode> newRootNodes = new();

        foreach (var root in roots)
        {
            var existing = _rootNodes.FirstOrDefault(n => string.Equals(n.Path, root.Path, StringComparison.OrdinalIgnoreCase));

            newRootNodes.Add(new FolderNode
            {
                Path = root.Path,
                Name = Path.GetFileName(root.Path) is string name && !string.IsNullOrEmpty(name) ? name : root.Path,
                IsExpanded = existing?.IsExpanded ?? false,
                Children = existing?.Children ?? [],
                ChildrenLoaded = existing?.ChildrenLoaded ?? false
            });
        }

        _rootNodes = newRootNodes;
    }

    private void SelectFolder(FolderNode node)
    {
        AppState.SetFolder(node.Path);
    }

    private void ToggleExpand(FolderNode node)
    {
        node.IsExpanded = !node.IsExpanded;

        if (node.IsExpanded && !node.ChildrenLoaded)
        {
            LoadChildren(node);
        }
    }

    private void LoadChildren(FolderNode node)
    {
        try
        {
            var subfolders = Library.GetSubfolders(node.Path);
            node.Children = subfolders.Select(p => new FolderNode
            {
                Path = p,
                Name = Path.GetFileName(p)
            }).ToList();
            node.ChildrenLoaded = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderSidebar] Failed to load subfolders for {node.Path}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChanged;
        Library.LibraryChanged -= OnLibraryChanged;
    }
}