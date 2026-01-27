namespace Tagium.Core.Services.DataExport;

public class FileExportData
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public List<Guid> TagIds { get; set; } = [];
}