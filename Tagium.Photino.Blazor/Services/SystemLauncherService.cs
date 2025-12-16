using System.Diagnostics;

namespace Tagium.Photino.Blazor.Services;

public class SystemLauncherService
{
    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to open file: {ex.Message}");
        }
    }

    public void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            // Select the file in explorer
            string argument = $"/select,\"{path}\"";
            _ = Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to show in explorer: {ex.Message}");
        }
    }
}