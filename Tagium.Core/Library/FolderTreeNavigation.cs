namespace Tagium.Core.Library;

/// <summary>
/// Visible-order maths for the folder sidebar tree. The component owns expansion state and
/// rendering; this decides what "next row", "first child" and "parent" mean, so the arrow-key
/// contract can be unit-tested without a running app.
/// </summary>
public static class FolderTreeNavigation
{
    /// <summary>
    /// The rows a user can reach, depth-first: collapsed folders contribute themselves but
    /// nothing beneath them. Levels are one-based, matching <c>aria-level</c>.
    /// </summary>
    public static IReadOnlyList<FolderRow> Flatten(IReadOnlyList<FolderNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        List<FolderRow> rows = [];
        Stack<(FolderNode Node, int Level)> pending = new();

        for (int i = roots.Count - 1; i >= 0; i--)
        {
            pending.Push((roots[i], 1));
        }

        while (pending.Count > 0)
        {
            (FolderNode node, int level) = pending.Pop();
            rows.Add(new FolderRow(node.Path, level));

            if (!node.IsExpanded)
            {
                continue;
            }

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push((node.Children[i], level + 1));
            }
        }

        return rows;
    }

    public static FolderRow? First(IReadOnlyList<FolderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count > 0 ? rows[0] : null;
    }

    public static FolderRow? Last(IReadOnlyList<FolderRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count > 0 ? rows[^1] : null;
    }

    /// <summary>
    /// The row <paramref name="offset"/> places away, or <c>null</c> at either edge or when
    /// <paramref name="currentPath"/> is no longer visible. Trees clamp at the ends rather
    /// than wrapping, which matches every desktop file manager.
    /// </summary>
    public static FolderRow? Neighbour(IReadOnlyList<FolderRow> rows, string? currentPath, int offset)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int index = IndexOf(rows, currentPath);
        if (index < 0)
        {
            return null;
        }

        int target = index + offset;
        return target >= 0 && target < rows.Count ? rows[target] : null;
    }

    /// <summary>The row directly after <paramref name="currentPath"/> if it sits one level deeper.</summary>
    public static FolderRow? FirstChild(IReadOnlyList<FolderRow> rows, string? currentPath)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int index = IndexOf(rows, currentPath);
        if (index < 0 || index + 1 >= rows.Count)
        {
            return null;
        }

        FolderRow next = rows[index + 1];
        return next.Level == rows[index].Level + 1 ? next : null;
    }

    /// <summary>The nearest row above that sits one level shallower, or <c>null</c> for a root.</summary>
    public static FolderRow? Parent(IReadOnlyList<FolderRow> rows, string? currentPath)
    {
        ArgumentNullException.ThrowIfNull(rows);

        int index = IndexOf(rows, currentPath);
        if (index < 0)
        {
            return null;
        }

        int level = rows[index].Level;
        for (int i = index - 1; i >= 0; i--)
        {
            if (rows[i].Level < level)
            {
                return rows[i];
            }
        }

        return null;
    }

    private static int IndexOf(IReadOnlyList<FolderRow> rows, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return -1;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
