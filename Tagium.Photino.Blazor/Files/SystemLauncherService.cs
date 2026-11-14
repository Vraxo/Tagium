using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Tagium.Photino.Blazor.Files;

public class SystemLauncherService
{
    private readonly ILogger<SystemLauncherService> _logger;

    public SystemLauncherService(ILogger<SystemLauncherService> logger)
    {
        _logger = logger;
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot open file. File does not exist at path: {Path}", path);
            return;
        }

        try
        {
            _logger.LogInformation("Opening file: {Path}", path);
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file: {Path}", path);
        }
    }

    public void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot show in explorer. File does not exist at path: {Path}", path);
            return;
        }

        try
        {
            _logger.LogInformation("Showing file in explorer: {Path}", path);
            // Select the file in explorer
            string argument = $"/select,\"{path}\"";

            ProcessStartInfo processStartInfo = new("explorer.exe", argument)
            {
                UseShellExecute = true
            };

            _ = Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show file in explorer: {Path}", path);
        }
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("Cannot open folder. Folder does not exist at path: {Path}", path);
            return;
        }

        try
        {
            _logger.LogInformation("Opening folder in Explorer: {Path}", path);
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open folder: {Path}", path);
        }
    }

}