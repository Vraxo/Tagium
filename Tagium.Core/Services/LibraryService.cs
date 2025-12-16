using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;

    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    // --- Files ---

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            List<FileEntry> all = _fileRepo.GetAll(sort, dir);
            return all.Where(f => f.IsMissing).ToList();
        }

        return string.IsNullOrWhiteSpace(searchText) ? _fileRepo.GetAll(sort, dir) : _searchRepo.Search(searchText, sort, dir);
    }

    public bool AddFile(string path)
    {
        string? hash = FileHasher.ComputeSha256(path);
        if (hash == null)
        {
            return false; // File read error
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            LibraryChanged?.Invoke();
            return true;
        }

        return false; // Duplicate or Error
    }

    public void RemoveFile(long fileId)
    {
        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void AddTagToFile(long fileId, long tagId)
    {
        _fileTagRepo.AddTagToFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        _fileTagRepo.RemoveTagFromFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    // --- Tags ---

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTagsForFile(long fileId)
    {
        List<long> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> allTags = _tagRepo.GetAll();
        return allTags.Where(t => tagIds.Contains(t.Id)).ToList();
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        _tagRepo.Update(tag);
        LibraryChanged?.Invoke();
    }

    public void DeleteTag(long id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }

    // --- Thumbnails ---

    [Obsolete]
    public void EnsureThumbnails(IEnumerable<FileEntry> files)
    {
        foreach (FileEntry file in files)
        {
            if (!_thumbnailService.HasThumbnail(file.Id))
            {
                _ = _thumbnailService.GenerateThumbnailAsync(file);
            }
        }
    }
}