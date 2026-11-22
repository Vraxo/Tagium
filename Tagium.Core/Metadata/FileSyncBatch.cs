namespace Tagium.Core.Metadata;

/// <summary>Why a batch of files is being written back, so the UI can describe the work.</summary>
public enum FileSyncBatch
{
    /// <summary>Tags, ratings, titles or descriptions changed on these files.</summary>
    EditedFiles,

    /// <summary>A tag was renamed or deleted, so every file carrying it has to be rewritten.</summary>
    TagUpdated
}
