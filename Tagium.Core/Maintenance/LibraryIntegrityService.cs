using Tagium.Core.Files;
using Tagium.Core.Settings;

namespace Tagium.Core.Maintenance;

public class LibraryIntegrityService
{
    private readonly FileRepository _fileRepo;

    public LibraryIntegrityService(FileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public Task<IntegrityScanResult> ScanAsync()
    {
        // This includes the full catalog read as well as filesystem probes and database updates.
        // Keep all of it off the UI thread for large libraries and slow network roots.
        return Task.Run(() =>
        {
            List<FileEntry> allFiles = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
            IntegrityScanResult result = new() { FilesScanned = allFiles.Count };

            List<Guid> toMarkMissing = [];
            List<Guid> toMarkFound = [];

            foreach (FileEntry file in allFiles)
            {
                bool exists = File.Exists(file.Path);

                if (!exists && !file.IsMissing)
                {
                    toMarkMissing.Add(file.Id);
                }
                else if (exists && file.IsMissing)
                {
                    toMarkFound.Add(file.Id);
                }
            }

            if (toMarkMissing.Count != 0)
            {
                _fileRepo.SetMissingStatus(toMarkMissing, true);
                result.NewlyMissing = toMarkMissing.Count;
            }

            if (toMarkFound.Count != 0)
            {
                _fileRepo.SetMissingStatus(toMarkFound, false);
                result.NewlyFound = toMarkFound.Count;
            }

            return result;
        });
    }

    public Task PurgeMissingAsync()
    {
        return Task.Run(_fileRepo.RemoveMissingFiles);
    }
}
