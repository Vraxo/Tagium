using System.Text;
using System.Text.Json;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class DatabaseExport
{
    public List<Tag> Tags { get; set; } = [];
    public List<FileExportData> Files { get; set; } = [];
}

public class FileExportData
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public List<long> TagIds { get; set; } = [];
}

public class DataExportService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;

    public DataExportService(FileRepository fileRepo, TagRepository tagRepo, FileTagRepository fileTagRepo)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
    }

    public async Task ExportJsonAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    public async Task ExportCsvAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        Dictionary<long, string> tagLookup = data.Tags.ToDictionary(t => t.Id, t => t.Name);
        StringBuilder sb = new();

        _ = sb.AppendLine("Id,Path,Name,Hash,DateAdded,Tags");

        foreach (var file in data.Files)
        {
            string tags = string.Join(";", file.TagIds.Select(id => tagLookup.GetValueOrDefault(id, "unknown")));
            string line = $"{file.Id},\"{Escape(file.Path)}\",\"{Escape(file.Name)}\",{file.Hash},{file.DateAdded},\"{Escape(tags)}\"";
            _ = sb.AppendLine(line);
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private DatabaseExport BuildExportData()
    {
        List<FileEntry> files = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        List<Tag> tags = _tagRepo.GetAll();

        DatabaseExport export = new() { Tags = tags };

        foreach (FileEntry f in files)
        {
            List<long> tagIds = _fileTagRepo.GetTagIdsForFile(f.Id);
            export.Files.Add(new FileExportData
            {
                Id = f.Id,
                Path = f.Path,
                Name = f.Name,
                Hash = f.Hash,
                DateAdded = f.DateAdded,
                IsMissing = f.IsMissing,
                TagIds = tagIds
            });
        }
        return export;
    }

    private static string Escape(string s)
    {
        return s.Replace("\"", "\"\"");
    }
}