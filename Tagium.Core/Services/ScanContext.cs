using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ScanContext
{
    public List<string> CandidateFiles { get; } = [];
    public Dictionary<string, FileEntry> DbFilesMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> UntrackedFiles { get; } = [];
    public HashSet<string> VerifiedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasChanges { get; set; }
}