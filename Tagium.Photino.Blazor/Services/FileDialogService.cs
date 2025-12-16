using NativeFileDialogs.Net;

namespace Tagium.Photino.Blazor.Services;

public class FileDialogService
{
    public string[]? OpenFiles()
    {
        // Nfd.OpenDialogMultiple returns a path set, or throws/returns error status.
        // We wrap it to return a safe string array or null.

        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok ? paths : null;
    }
}