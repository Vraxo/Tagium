namespace Tagium.Core.Services.DataExport;

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
