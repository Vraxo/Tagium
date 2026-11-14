using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Library;
using Tagium.Photino.Blazor;

namespace Tagium.Photino.Blazor.Files;

public partial class FolderSidebar : IDisposable
{
    [Inject] private AppState AppState { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private ContextMenuService ContextMenu { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private List<FolderNode> _rootNodes = [];

    // The row that owns the tree's single tab stop. Null means "fall back to the selected
    // folder, then to the first row", so the tree is always reachable with one Tab.
    private string? _focusedPath;

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

    private IReadOnlyList<FolderRow> Rows => FolderTreeNavigation.Flatten(_rootNodes);

    /// <summary>
    /// The only row with <c>tabindex="0"</c>. It must land on a row that is actually rendered,
    /// otherwise collapsing a subtree would leave the whole tree unreachable by Tab.
    /// </summary>
    private string? TabStopPath
    {
        get
        {
            IReadOnlyList<FolderRow> rows = Rows;

            if (rows.Count == 0)
            {
                return null;
            }

            if (_focusedPath is not null && Contains(rows, _focusedPath))
            {
                return _focusedPath;
            }

            if (AppState.CurrentFolderPath is { } current && Contains(rows, current))
            {
                return current;
            }

            return rows[0].Path;
        }
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
        _focusedPath = node.Path;
        AppState.SetFolder(node.Path);
    }

    /// <summary>
    /// A folder that has been expanded once and came back empty is a leaf: it keeps neither a
    /// chevron nor <c>aria-expanded</c>, so it must not be expandable from the keyboard either.
    /// </summary>
    private static bool CanExpand(FolderNode node)
    {
        return node.Children.Count > 0 || !node.ChildrenLoaded;
    }

    private void ToggleExpand(FolderNode node)
    {
        if (!node.IsExpanded && !CanExpand(node))
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;

        if (node.IsExpanded)
        {
            if (!node.ChildrenLoaded)
            {
                LoadChildren(node);
            }

            return;
        }

        // Collapsing hides the subtree, so the roving tab stop cannot stay on a hidden row.
        if (_focusedPath is not null && !PathEquals(_focusedPath, node.Path) && IsUnder(_focusedPath, node.Path))
        {
            _focusedPath = node.Path;
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

    /// <summary>
    /// The tree key contract: arrows walk the visible rows, Right expands or steps into a child,
    /// Left collapses or steps out to the parent, Home/End jump to the ends, Enter/Space select.
    /// The row maths lives in <see cref="FolderTreeNavigation"/> so it is unit-tested in Core.
    /// </summary>
    private void OnRowKeyDown(KeyboardEventArgs e, FolderNode node)
    {
        if ((e.Key == "F10" && e.ShiftKey) || e.Key == "ContextMenu")
        {
            ContextMenu.ShowFolder(node.Path, node.Name, 0, 0, isKeyboardInvocation: true);
            return;
        }

        IReadOnlyList<FolderRow> rows = Rows;

        switch (e.Key)
        {
            case "ArrowDown":
                FocusRow(FolderTreeNavigation.Neighbour(rows, node.Path, 1));
                break;

            case "ArrowUp":
                FocusRow(FolderTreeNavigation.Neighbour(rows, node.Path, -1));
                break;

            case "Home":
                FocusRow(FolderTreeNavigation.First(rows));
                break;

            case "End":
                FocusRow(FolderTreeNavigation.Last(rows));
                break;

            case "ArrowRight":
                if (!node.IsExpanded)
                {
                    if (CanExpand(node))
                    {
                        ToggleExpand(node);
                    }
                }
                else
                {
                    FocusRow(FolderTreeNavigation.FirstChild(rows, node.Path));
                }

                break;

            case "ArrowLeft":
                if (node.IsExpanded)
                {
                    ToggleExpand(node);
                }
                else
                {
                    FocusRow(FolderTreeNavigation.Parent(rows, node.Path));
                }

                break;

            case "Enter":
            case " ":
                SelectFolder(node);
                break;
        }
    }

    public void OpenContextMenu(MouseEventArgs e, FolderNode node)
    {
        _focusedPath = node.Path;
        ContextMenu.ShowFolder(
            node.Path,
            node.Name,
            e.ClientX,
            e.ClientY,
            isKeyboardInvocation: false);
    }

    private void FocusRow(FolderRow? row)
    {
        if (row is null)
        {
            return;   // already at an edge, or the row is gone: nothing to move to
        }

        _focusedPath = row.Path;
        _ = MoveDomFocusAsync(row.Path);
    }

    /// <summary>
    /// Moves DOM focus and the tab stop together. Rows are rendered in exactly the order the
    /// tree navigates them, so the row index is enough — no element references are needed.
    /// </summary>
    private async Task MoveDomFocusAsync(string path)
    {
        int index = -1;

        IReadOnlyList<FolderRow> rows = Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (PathEquals(rows[i].Path, path))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("libraryNav.focusItem", ".folder-list", ".folder-node", index);
        }
        catch (JSException)
        {
            // Losing interop only costs the caret; the tab stop has still moved to this row.
        }
    }

    private static bool Contains(IReadOnlyList<FolderRow> rows, string path)
    {
        foreach (FolderRow row in rows)
        {
            if (PathEquals(row.Path, path))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnder(string candidate, string parent)
    {
        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        AppState.OnChange -= OnAppStateChanged;
        Library.LibraryChanged -= LoadRoots;
    }
}
