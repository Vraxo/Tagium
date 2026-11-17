using Microsoft.Extensions.Logging;
using Tagium.Core.Files;
using Tagium.Core.Metadata;
using Tagium.Core.Tags;

namespace Tagium.Core.Library;

/// <summary>
/// Copies embedded metadata into the catalog.
///
/// Two operations, deliberately separate because a scan treats them differently:
///
///  * <see cref="ApplyFieldsAsync"/> reads the file's embedded rating, title and description onto
///    the entry in memory and leaves persistence to the caller. A re-scanned file needs the new
///    fields folded into the same update that records its new size and hash, so it is one write.
///  * <see cref="ImportAsync"/> is the first-import path: it also maps embedded tags into the tag
///    tables. Tags are mapped only on import, never on a rescan, so re-reading a file does not
///    resurrect tags the user deleted.
/// </summary>
public sealed class MetadataImporter
{
    private const string ImportedTagColor = "#757575";

    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly ILogger<MetadataImporter> _logger;

    public MetadataImporter(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        ILogger<MetadataImporter> logger)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _logger = logger;
    }

    /// <summary>
    /// Reads the file's embedded rating, title and description onto <paramref name="entry"/>.
    /// Returns <c>false</c> when the file carries no readable metadata and the entry was left
    /// untouched. Does not write to the catalog.
    /// </summary>
    public async Task<bool> ApplyFieldsAsync(string path, FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return ApplyFields(await _exifTool.ReadMetadataAsync(path), entry);
    }

    private static bool ApplyFields(MediaMetadata? metadata, FileEntry entry)
    {
        if (metadata is null)
        {
            return false;
        }

        entry.Rating = metadata.Rating;
        entry.Title = metadata.Title;
        entry.Description = metadata.Description;
        return true;
    }

    /// <summary>
    /// Applies embedded metadata to a newly imported file, persists it, and maps its embedded
    /// tags. A file with no readable metadata is left exactly as the catalog already has it.
    /// </summary>
    public async Task ImportAsync(string path, FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using LogTiming timer = LogTiming.StartDebug(_logger, "Metadata and tag mapping extraction for {Path}", path);

        // One ExifTool read serves both the fields and the tags.
        MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
        if (!ApplyFields(metadata, entry))
        {
            return;
        }

        _logger.LogInformation("Extracting and writing embedded ExifTool metadata for imported file: {Path}", path);
        _fileRepo.Update(entry);

        foreach (string normalizedTag in NormalizeTags(metadata!.Tags))
        {
            Guid? tagId = ResolveOrCreateTag(normalizedTag);
            if (tagId.HasValue)
            {
                _fileTagRepo.AddTagToFile(entry.Id, tagId.Value);
            }
        }
    }

    /// <summary>
    /// The file's embedded tags, normalised and de-duplicated. Read separately from the fields so
    /// the rescan path can skip it entirely instead of paying for a parse it will not use.
    /// </summary>
    private static List<string> NormalizeTags(IReadOnlyCollection<string> rawTags)
    {
        List<string> normalized = [];

        foreach (string rawTag in rawTags)
        {
            string tag = TagFormatter.Normalize(rawTag);
            if (!string.IsNullOrWhiteSpace(tag) && !normalized.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(tag);
            }
        }

        return normalized;
    }

    /// <summary>
    /// Returns the existing tag's id, or creates the tag and returns the new id. Resolve-then-create
    /// because a failed insert returns nothing and the tag may exist already.
    /// </summary>
    private Guid? ResolveOrCreateTag(string normalizedName) =>
        _tagRepo.ResolveTagId(normalizedName) ?? _tagRepo.Add(normalizedName, ImportedTagColor, null);
}
