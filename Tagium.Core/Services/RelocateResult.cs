using Tagium.Core.Models;

namespace Tagium.Core.Services;

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);
