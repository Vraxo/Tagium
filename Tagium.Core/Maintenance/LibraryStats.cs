using Tagium.Core.Files;

namespace Tagium.Core.Maintenance;

/// <summary>
/// Whole-library overview shown in the statistics modal:
/// file counters from the cache plus tag/root counts from the library database.
/// </summary>
public record LibraryStats(
    FileStats Files,
    IReadOnlyList<ExtensionStat> Extensions,
    long TagCount,
    long RootCount);
