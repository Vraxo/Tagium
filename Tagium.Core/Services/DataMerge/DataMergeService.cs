using Microsoft.Data.Sqlite;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.DataMerge;

public class DatabaseMergeService
{
    private readonly FileRepository _targetFileRepo;
    private readonly TagRepository _targetTagRepo;
    private readonly FileTagRepository _targetFileTagRepo;

    public DatabaseMergeService(
        DatabaseManager targetDb,
        FileRepository targetFileRepo,
        TagRepository targetTagRepo,
        FileTagRepository targetFileTagRepo)
    {
        _targetFileRepo = targetFileRepo;
        _targetTagRepo = targetTagRepo;
        _targetFileTagRepo = targetFileTagRepo;
    }

    public MergeResult Merge(string sourceDbPath)
    {
        MergeResult result = new();

        string sourceConnString = $"Data Source={sourceDbPath}";
        using SqliteConnection sourceConn = new(sourceConnString);
        sourceConn.Open();

        List<Tag> sourceTags = LoadTags(sourceConn);
        List<FileEntry> sourceFiles = LoadFiles(sourceConn);
        Dictionary<Guid, List<Guid>> sourceFileTagMap = LoadFileTags(sourceConn);

        Dictionary<string, Tag> targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileEntry> targetFiles = _targetFileRepo.GetAll(SortBy.Path, SortDirection.Asc)
            .Where(f => f.Hash is not null)
            .ToDictionary(f => f.Hash!);

        foreach (Tag sTag in sourceTags)
        {
            if (!targetTags.ContainsKey(sTag.Name))
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                result.NewTags++;
            }
        }

        targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash is null || targetFiles.ContainsKey(sFile.Hash))
            {
                continue;
            }

            _ = _targetFileRepo.Add(sFile.Path, sFile.Hash, sFile.Size, sFile.ModifiedTime);
            result.NewFiles++;

            FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
            if (added is null)
            {
                continue;
            }

            if (!sourceFileTagMap.TryGetValue(sFile.Id, out List<Guid>? sTagIds))
            {
                continue;
            }

            foreach (Guid sTagId in sTagIds)
            {
                Tag? sourceTag = sourceTags.FirstOrDefault(t => t.Id == sTagId);
                if (sourceTag is null)
                {
                    continue;
                }

                Tag? targetTag = targetTags.GetValueOrDefault(sourceTag.Name);
                if (targetTag is null)
                {
                    continue;
                }

                _targetFileTagRepo.AddTagToFile(added.Id, targetTag.Id);
                result.NewAssociations++;
            }
        }

        return result;
    }

    private static List<Tag> LoadTags(SqliteConnection conn)
    {
        List<Tag> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ColorHex, Emoji FROM Tags";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            list.Add(new Tag
            {
                Id = Guid.Parse(r.GetString(0)),
                Name = r.GetString(1),
                ColorHex = r.GetString(2),
                Emoji = r.IsDBNull(3) ? null : r.GetString(3)
            });
        }

        return list;
    }

    private static List<FileEntry> LoadFiles(SqliteConnection conn)
    {
        List<FileEntry> list = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime FROM Files";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            list.Add(new()
            {
                Id = Guid.Parse(r.GetString(0)),
                Path = r.GetString(1),
                Name = r.GetString(2),
                Hash = r.IsDBNull(3) ? null : r.GetString(3),
                DateAdded = r.GetInt64(4),
                IsMissing = r.GetBoolean(5),
                Size = r.GetInt64(6),
                ModifiedTime = r.GetInt64(7)
            });
        }

        return list;
    }

    private static Dictionary<Guid, List<Guid>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<Guid, List<Guid>> map = [];

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, TagId FROM FileTags";

        using SqliteDataReader r = cmd.ExecuteReader();

        while (r.Read())
        {
            Guid fid = Guid.Parse(r.GetString(0));
            Guid tid = Guid.Parse(r.GetString(1));

            if (!map.TryGetValue(fid, out List<Guid>? value))
            {
                value = [];
                map[fid] = value;
            }

            value.Add(tid);
        }

        return map;
    }
}