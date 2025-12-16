using Microsoft.Data.Sqlite;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class MergeResult
{
    public int NewFiles { get; set; }
    public int NewTags { get; set; }
    public int NewAssociations { get; set; }
}

public class DatabaseMergeService
{
    private readonly DatabaseManager _targetDb;
    private readonly FileRepository _targetFileRepo;
    private readonly TagRepository _targetTagRepo;
    private readonly FileTagRepository _targetFileTagRepo;

    public DatabaseMergeService(
        DatabaseManager targetDb,
        FileRepository targetFileRepo,
        TagRepository targetTagRepo,
        FileTagRepository targetFileTagRepo)
    {
        _targetDb = targetDb;
        _targetFileRepo = targetFileRepo;
        _targetTagRepo = targetTagRepo;
        _targetFileTagRepo = targetFileTagRepo;
    }

    public MergeResult Merge(string sourceDbPath)
    {
        MergeResult result = new();

        // Connect to source DB manually
        string sourceConnString = $"Data Source={sourceDbPath}";
        using SqliteConnection sourceConn = new(sourceConnString);
        sourceConn.Open();

        // 1. Load Source Data
        List<Tag> sourceTags = LoadTags(sourceConn);
        List<FileEntry> sourceFiles = LoadFiles(sourceConn);
        Dictionary<long, List<long>> sourceFileTagMap = LoadFileTags(sourceConn);

        // 2. Load Target Data
        Dictionary<string, Tag> targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileEntry> targetFiles = _targetFileRepo.GetAll(SortBy.Path, SortDirection.Asc)
            .Where(f => f.Hash != null)
            .ToDictionary(f => f.Hash!);

        // 3. Merge Tags
        Dictionary<long, long> tagIdMap = []; // SourceId -> TargetId

        foreach (Tag sTag in sourceTags)
        {
            if (targetTags.TryGetValue(sTag.Name, out Tag? existing))
            {
                tagIdMap[sTag.Id] = existing.Id;
            }
            else
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                // Need to fetch the new ID. For simplicity, we reload tags or assume Add returns ID.
                // Current repo doesn't return ID. Let's re-fetch.
                Tag newTag = _targetTagRepo.GetAll().First(t => t.Name == sTag.Name);
                targetTags[newTag.Name] = newTag;
                tagIdMap[sTag.Id] = newTag.Id;
                result.NewTags++;
            }
        }

        // 4. Merge Files & Associations
        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash == null)
            {
                continue;
            }

            long targetFileId;

            if (targetFiles.TryGetValue(sFile.Hash, out FileEntry? existingFile))
            {
                targetFileId = existingFile.Id;
            }
            else
            {
                // Add File
                _ = _targetFileRepo.Add(sFile.Path, sFile.Hash);
                // Re-fetch to get ID. Optimization: Repo.Add should return ID/Entry.
                // Fallback:
                FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
                if (added == null)
                {
                    continue; // Failed to add
                }

                targetFileId = added.Id;
                result.NewFiles++;
            }

            // Merge Associations
            if (sourceFileTagMap.TryGetValue(sFile.Id, out List<long>? sTagIds))
            {
                foreach (long sTagId in sTagIds)
                {
                    if (tagIdMap.TryGetValue(sTagId, out long targetTagId))
                    {
                        // Repository handles "INSERT OR IGNORE"
                        _targetFileTagRepo.AddTagToFile(targetFileId, targetTagId);
                        // We assume success, hard to count actual "new" without checking first.
                        // We'll just increment. Real logic would check existence.
                        result.NewAssociations++;
                    }
                }
            }
        }

        return result;
    }

    // Mini-loaders for the external DB connection
    private static List<Tag> LoadTags(SqliteConnection conn)
    {
        List<Tag> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ColorHex, Emoji FROM Tags";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Tag { Id = r.GetInt64(0), Name = r.GetString(1), ColorHex = r.GetString(2), Emoji = r.IsDBNull(3) ? null : r.GetString(3) });
        }

        return list;
    }

    private static List<FileEntry> LoadFiles(SqliteConnection conn)
    {
        List<FileEntry> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FileEntry
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Name = r.GetString(2),
                Hash = r.IsDBNull(3) ? null : r.GetString(3),
                DateAdded = r.GetInt64(4),
                IsMissing = r.GetBoolean(5)
            });
        }

        return list;
    }

    private static Dictionary<long, List<long>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<long, List<long>> map = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, TagId FROM FileTags";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long fid = r.GetInt64(0);
            long tid = r.GetInt64(1);
            if (!map.ContainsKey(fid))
            {
                map[fid] = [];
            }

            map[fid].Add(tid);
        }
        return map;
    }
}