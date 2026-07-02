using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ExifToolService : IDisposable
{
    private readonly string _commandName;
    private Process? _exifToolProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly object _lock = new();
    private bool _isDisposed;

    public bool IsEnabled { get; private set; }

    public ExifToolService()
    {
        _commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";
        InitializeProcess();
    }

    private void InitializeProcess()
    {
        try
        {
            ProcessStartInfo startInfo = new(_commandName)
            {
                Arguments = "-stay_open True -@ -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            _exifToolProcess = Process.Start(startInfo);
            if (_exifToolProcess is not null)
            {
                _stdin = _exifToolProcess.StandardInput;
                _stdout = _exifToolProcess.StandardOutput;

                _exifToolProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Console.WriteLine($"[ExifTool Warning] {e.Data}");
                    }
                };
                _exifToolProcess.BeginErrorReadLine();

                IsEnabled = !_exifToolProcess.HasExited;
            }
        }
        catch (Exception ex)
        {
            IsEnabled = false;
            Console.WriteLine($"[ExifToolService] WARNING: Failed to initialize persistent daemon: {ex.Message}. Metadata writing and reading will be disabled.");
        }
    }

    public async Task<MediaMetadata?> ReadMetadataAsync(string filePath)
    {
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return null;
        }

        try
        {
            string output;
            lock (_lock)
            {
                _stdin!.WriteLine("-json");
                _stdin!.WriteLine("-Subject");
                _stdin!.WriteLine("-Keywords");
                _stdin!.WriteLine("-Title");
                _stdin!.WriteLine("-Description");
                _stdin!.WriteLine("-Rating");
                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                StringBuilder sb = new();
                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    sb.AppendLine(line);
                }
                output = sb.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
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
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return false;
        }

        try
        {
            lock (_lock)
            {
                _stdin!.WriteLine("-overwrite_original");
                _stdin!.WriteLine("-Subject=");
                _stdin!.WriteLine("-Keywords=");

                foreach (string tag in metadata.Tags)
                {
                    _stdin!.WriteLine($"-Subject={tag}");
                    _stdin!.WriteLine($"-Keywords={tag}");
                }

                if (metadata.Title is not null)
                {
                    _stdin!.WriteLine($"-Title={metadata.Title}");
                }
                else
                {
                    _stdin!.WriteLine("-Title=");
                }

                if (metadata.Description is not null)
                {
                    _stdin!.WriteLine($"-Description={metadata.Description}");
                }
                else
                {
                    _stdin!.WriteLine("-Description=");
                }

                if (metadata.Rating.HasValue)
                {
                    _stdin!.WriteLine($"-Rating={metadata.Rating.Value}");
                }
                else
                {
                    _stdin!.WriteLine("-Rating=");
                }

                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing && _exifToolProcess is not null && !_exifToolProcess.HasExited)
        {
            try
            {
                lock (_lock)
                {
                    _stdin?.WriteLine("-stay_open");
                    _stdin?.WriteLine("False");
                    _stdin?.WriteLine("-execute");
                    _stdin?.Flush();
                }
                _exifToolProcess.WaitForExit(3000);
            }
            catch
            {
            }
            finally
            {
                _exifToolProcess.Dispose();
            }
        }

        _isDisposed = true;
    }
}