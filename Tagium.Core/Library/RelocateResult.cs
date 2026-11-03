using Tagium.Core.Files;

namespace Tagium.Core.Library;

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);
