using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tagium.Photino.Blazor.Components.Modals;

namespace Tagium.Core.Services;

public class ExifToolService
{
    private readonly string _commandName;

    public bool IsEnabled { get; }

    public ExifToolService()
    {
        _commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(_commandName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-ver" }
            });
            IsEnabled = process is not null && process.WaitForExit(1000) && process.ExitCode == 0;
        }
        catch
        {
            IsEnabled = false;
        }

        if (!IsEnabled)
        {
            Console.WriteLine($"[ExifToolService] WARNING: '{_commandName}' command not found. Metadata writing and reading will be disabled.");
        }
    }

    public async Task<MediaMetadata?> ReadMetadataAsync(string filePath)
    {
        if (!IsEnabled || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            ProcessStartInfo startInfo = new(_commandName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-json", "-Subject", "-Keywords", "-Title", "-Description", "-Rating", filePath }
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[ExifTool] Error reading {filePath}: {error}");
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = doc.RootElement[0];
            MediaMetadata metadata = new();

            if (first.TryGetProperty("Title", out JsonElement titleProp))
            {
                metadata.Title = titleProp.GetString();
            }

            if (first.TryGetProperty("Description", out JsonElement descProp))
            {
                metadata.Description = descProp.GetString();
            }

            if (first.TryGetProperty("Rating", out JsonElement ratingProp))
            {
                if (ratingProp.ValueKind == JsonValueKind.Number)
                {
                    metadata.Rating = ratingProp.GetInt32();
                }
                else if (ratingProp.ValueKind == JsonValueKind.String && int.TryParse(ratingProp.GetString(), out int r))
                {
                    metadata.Rating = r;
                }
            }

            HashSet<string> uniqueTags = new(StringComparer.OrdinalIgnoreCase);

            if (first.TryGetProperty("Subject", out JsonElement subjProp))
            {
                PopulateTags(subjProp, uniqueTags);
            }

            if (first.TryGetProperty("Keywords", out JsonElement kwProp))
            {
                PopulateTags(kwProp, uniqueTags);
            }

            metadata.Tags = [.. uniqueTags];
            return metadata;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExifTool] Exception reading {filePath}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> WriteMetadataAsync(string filePath, MediaMetadata metadata)
    {
        if (!IsEnabled || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new(_commandName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-overwrite_original", "-Subject=", "-Keywords=" }
            };

            foreach (string tag in metadata.Tags)
            {
                startInfo.ArgumentList.Add($"-Subject={tag}");
                startInfo.ArgumentList.Add($"-Keywords={tag}");
            }

            if (metadata.Title is not null)
            {
                startInfo.ArgumentList.Add($"-Title={metadata.Title}");
            }
            else
            {
                startInfo.ArgumentList.Add("-Title=");
            }

            if (metadata.Description is not null)
            {
                startInfo.ArgumentList.Add($"-Description={metadata.Description}");
            }
            else
            {
                startInfo.ArgumentList.Add("-Description=");
            }

            if (metadata.Rating.HasValue)
            {
                startInfo.ArgumentList.Add($"-Rating={metadata.Rating.Value}");
            }
            else
            {
                startInfo.ArgumentList.Add("-Rating=");
            }

            startInfo.ArgumentList.Add(filePath);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                Console.WriteLine($"[ExifTool] Error writing {filePath}: {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ExifTool] Exception writing {filePath}: {ex.Message}");
            return false;
        }
    }

    private static void PopulateTags(JsonElement element, HashSet<string> tags)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    _ = tags.Add(s.Trim());
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? s = element.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                _ = tags.Add(s.Trim());
            }
        }
    }
}