using Tagium.Core.Tags;

namespace Tagium.Core.Maintenance;

public class DatabaseExport
{
    public List<Tag> Tags { get; set; } = [];
    public List<FileExportData> Files { get; set; } = [];
}
