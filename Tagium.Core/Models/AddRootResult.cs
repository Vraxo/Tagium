namespace Tagium.Core.Models;

public enum AddRootStatus
{
    Success,
    Duplicate,
    NestedInsideExisting,
    ConsolidatedChildren
}

public class AddRootResult
{
    public AddRootStatus Status { get; set; }
    public required string Message { get; set; }
    public List<string> ConsolidatedPaths { get; set; } = [];
}