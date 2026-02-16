using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Import;

public sealed record ImportResult(AddFileResult Status, FileEntry? Entry);