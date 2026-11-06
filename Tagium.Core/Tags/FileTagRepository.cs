using Dapper;
using Microsoft.Data.Sqlite;

using Tagium.Core;

namespace Tagium.Core.Tags;

public class FileTagRepository
{
    private readonly DatabaseManager _db;

    public FileTagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("INSERT OR IGNORE INTO cache.FileTags (FileId, TagId) VALUES (@FileId, @TagId)", new { FileId = fileId, TagId = tagId });
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM cache.FileTags WHERE FileId = @FileId AND TagId = @TagId", new { FileId = fileId, TagId = tagId });
    }

    public List<Guid> GetTagIdsForFile(Guid fileId)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<Guid>("SELECT TagId FROM cache.FileTags WHERE FileId = @FileId", new { FileId = fileId }).ToList();
    }

    public List<Guid> GetFileIdsForTag(Guid tagId)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<Guid>("SELECT FileId FROM cache.FileTags WHERE TagId = @TagId", new { TagId = tagId }).ToList();
    }

    public Dictionary<Guid, int> GetTagAssignmentCounts(IEnumerable<Guid> fileIds)
    {
        List<Guid> ids = fileIds.Distinct().ToList();
        Dictionary<Guid, int> counts = [];

        foreach (Guid[] idChunk in ids.Chunk(900))
        {
            using SqliteConnection conn = _db.GetConnection();
            IEnumerable<dynamic> rows = conn.Query(
                "SELECT TagId, COUNT(*) AS AssignedCount FROM cache.FileTags WHERE FileId IN @FileIds GROUP BY TagId",
                new { FileIds = idChunk });

            foreach (dynamic row in rows)
            {
                Guid tagId = Guid.Parse(Convert.ToString(row.TagId)!);
                int assignedCount = Convert.ToInt32(row.AssignedCount);
                counts[tagId] = counts.GetValueOrDefault(tagId) + assignedCount;
            }
        }

        return counts;
    }
}