using NativeFileDialogs.Net;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Tagium.Photino.Blazor.Services;

public class FileDialogService
{
    public static string[]? OpenFiles()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OpenFilesWindowsPowerShell();
        }

        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok
            ? paths
            : null;
    }

    public static string? PickFolder()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return PickFolderWindowsPowerShell();
        }

        NfdStatus result = Nfd.PickFolder(out string path, null);

        return result == NfdStatus.Ok
            ? path
            : null;
    }

    private static string? PickFolderWindowsPowerShell()
    {
        try
        {
            string script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.FolderBrowserDialog; $f.Description = 'Select Library Folder'; $f.ShowNewFolderButton = $true; if ($f.ShowDialog() -eq 'OK') { Write-Output $f.SelectedPath }";

            ProcessStartInfo startInfo = new("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileDialog] PowerShell folder picker fallback failed: {ex.Message}");
            NfdStatus result = Nfd.PickFolder(out string path, null);
            return result == NfdStatus.Ok ? path : null;
        }
    }

    private static string[]? OpenFilesWindowsPowerShell()
    {
        try
        {
            string script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.OpenFileDialog; $f.Multiselect = $true; if ($f.ShowDialog() -eq 'OK') { foreach ($file in $f.FileNames) { Write-Output $file } }";

            ProcessStartInfo startInfo = new("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -Command \"{script}\"",
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            List<string> files = [];
            while (!process.StandardOutput.EndOfStream)
            {
                string? line = process.StandardOutput.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    files.Add(line.Trim());
                }
            }
            process.WaitForExit();

            return files.Count > 0 ? [.. files] : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileDialog] PowerShell file picker fallback failed: {ex.Message}");
            NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);
            return result == NfdStatus.Ok ? paths : null;
        }
    }
}