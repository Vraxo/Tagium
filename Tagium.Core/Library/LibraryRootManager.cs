namespace Tagium.Core.Library;

/// <summary>Validates and consolidates library roots before registering them.</summary>
internal sealed class LibraryRootManager
{
    private readonly Func<List<LibraryRoot>> _getRoots;
    private readonly Action<string> _addRoot;
    private readonly Action<Guid, string> _deleteRoot;
    private readonly Action _notifyChanged;

    public LibraryRootManager(Func<List<LibraryRoot>> getRoots, Action<string> addRoot,
        Action<Guid, string> deleteRoot, Action notifyChanged)
    {
        _getRoots = getRoots;
        _addRoot = addRoot;
        _deleteRoot = deleteRoot;
        _notifyChanged = notifyChanged;
    }

    public AddRootResult AddWithValidation(string path)
    {
        string targetPath = path.Trim();
        if (string.IsNullOrEmpty(targetPath))
        {
            return new AddRootResult { Status = AddRootStatus.Duplicate, Message = "Invalid path." };
        }

        string targetWithSep = targetPath.EndsWith(Path.DirectorySeparatorChar)
            ? targetPath
            : targetPath + Path.DirectorySeparatorChar;

        List<LibraryRoot> existingRoots = _getRoots();

        bool exactDuplicate = existingRoots.Any(r => string.Equals(r.Path.TrimEnd(Path.DirectorySeparatorChar), targetPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));
        if (exactDuplicate)
        {
            return new AddRootResult
            {
                Status = AddRootStatus.Duplicate,
                Message = "This folder is already registered as a library root."
            };
        }

        LibraryRoot? parentRoot = existingRoots.FirstOrDefault(r =>
        {
            string existingWithSep = r.Path.EndsWith(Path.DirectorySeparatorChar) ? r.Path : r.Path + Path.DirectorySeparatorChar;
            return targetWithSep.StartsWith(existingWithSep, StringComparison.OrdinalIgnoreCase);
        });

        if (parentRoot != null)
        {
            return new AddRootResult
            {
                Status = AddRootStatus.NestedInsideExisting,
                Message = $"This folder is already covered by your existing library folder: {parentRoot.Path}"
            };
        }

        List<LibraryRoot> childrenRoots = existingRoots.Where(r =>
        {
            string existingWithSep = r.Path.EndsWith(Path.DirectorySeparatorChar) ? r.Path : r.Path + Path.DirectorySeparatorChar;
            return existingWithSep.StartsWith(targetWithSep, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        List<string> consolidated = [];
        if (childrenRoots.Count > 0)
        {
            foreach (LibraryRoot child in childrenRoots)
            {
                _deleteRoot(child.Id, child.Path);
                consolidated.Add(child.Path);
            }
        }

        _addRoot(targetPath);
        _notifyChanged();

        if (consolidated.Count > 0)
        {
            string consolidatedNames = string.Join(", ", consolidated.Select(p => Path.GetFileName(p) is string name && !string.IsNullOrEmpty(name) ? name : p));
            return new AddRootResult
            {
                Status = AddRootStatus.ConsolidatedChildren,
                Message = $"Added {targetPath}. Consolidated nested folder(s): {consolidatedNames}",
                ConsolidatedPaths = consolidated
            };
        }

        return new AddRootResult
        {
            Status = AddRootStatus.Success,
            Message = "Library folder added successfully."
        };
    }
}
