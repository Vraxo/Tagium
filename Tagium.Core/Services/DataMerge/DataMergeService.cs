using Dapper;
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

            added.Rating = sFile.Rating;
            added.Title = sFile.Title;
            added.Description = sFile.Description;
            _targetFileRepo.Update(added);

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
        return conn.Query<Tag>("SELECT Id, Name, ColorHex, Emoji FROM Tags").ToList();
    }

    private static List<FileEntry> LoadFiles(SqliteConnection conn)
    {
        return conn.Query<FileEntry>("SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM Files").ToList();
    }

    private static Dictionary<Guid, List<Guid>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<Guid, List<Guid>> map = [];
        IEnumerable<dynamic> rows = conn.Query("SELECT FileId, TagId FROM FileTags");

        foreach (dynamic row in rows)
        {
            Guid fid = Guid.Parse((string)row.FileId);
            Guid tid = Guid.Parse((string)row.TagId);

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