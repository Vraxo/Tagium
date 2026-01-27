using NativeFileDialogs.Net;

namespace Tagium.Photino.Blazor.Services;

public class FileDialogService
{
    public static string[]? OpenFiles()
    {
        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok
            ? paths
            : null;
    }
}