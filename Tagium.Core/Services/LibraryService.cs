using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ThumbnailService _thumbnailService;

    // Events for UI reactivity
    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ThumbnailService thumbnailService)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _thumbnailService = thumbnailService;
    }

    public List<FileEntry> GetFiles(SortBy sortBy, SortDirection direction)
    {
        var files = _fileRepo.GetAll(sortBy, direction);
        // Optimization: Don't load tags for grid view unless necessary?
        // For now, simple approach:
        return files;
    }

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public void AddFile(string path)
    {
        // Simple hash placeholder for now
        string hash = Guid.NewGuid().ToString();
        _fileRepo.Add(path, hash);
        LibraryChanged?.Invoke();
    }

    public void EnsureThumbnails(IEnumerable<FileEntry> files)
    {
        foreach (var file in files)
        {
            if (_thumbnailService.HasThumbnail(file.Id))
            {
                continue;
            }
            _ = _thumbnailService.GenerateThumbnailAsync(file).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    // Optional: granular update event
                }
            });
        }
    }
}