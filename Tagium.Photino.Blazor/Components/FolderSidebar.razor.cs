using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
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
        Library.LibraryChanged += LoadRoots;
    }

    private void OnAppStateChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void LoadRoots()
    {
        _ = Task.Run(async () =>
        {
            List<LibraryRoot> roots = Library.GetRoots().Where(r => r.IsEnabled).OrderBy(r => r.Path.Length).ToList();
            List<LibraryRoot> pruned = [];

            foreach (LibraryRoot root in roots)
            {
                bool isNested = pruned.Any(p =>
                    root.Path.StartsWith(p.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(root.Path, p.Path, StringComparison.OrdinalIgnoreCase));

                if (!isNested)
                {
                    pruned.Add(root);
                }
            }

            List<FolderNode> newRootNodes = [];

            foreach (LibraryRoot root in pruned)
            {
                FolderNode? existing = _rootNodes.FirstOrDefault(n => string.Equals(n.Path, root.Path, StringComparison.OrdinalIgnoreCase));

                newRootNodes.Add(new FolderNode
                {
                    Path = root.Path,
                    Name = Path.GetFileName(root.Path) is string name && !string.IsNullOrEmpty(name) ? name : root.Path,
                    IsExpanded = existing?.IsExpanded ?? false,
                    Children = existing?.Children ?? [],
                    ChildrenLoaded = existing?.ChildrenLoaded ?? false
                });
            }

            await InvokeAsync(() =>
            {
                _rootNodes = newRootNodes;
                StateHasChanged();
            });
        });
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
        _ = Task.Run(async () =>
        {
            try
            {
                List<string> subfolders = Library.GetSubfolders(node.Path);

                await InvokeAsync(() =>
                {
                    node.Children = subfolders.Select(p => new FolderNode
                    {
                        Path = p,
                        Name = Path.GetFileName(p)
                    }).ToList();
                    node.ChildrenLoaded = true;
                    StateHasChanged();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FolderSidebar] Failed to load subfolders for {node.Path}: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChanged;
        Library.LibraryChanged -= LoadRoots;
    }
}