using Microsoft.Data.Sqlite;

namespace Tagium.Core.Data;

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

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO cache.FileTags (FileId, TagId) VALUES (@FileId, @TagId)";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));
        cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cache.FileTags WHERE FileId = @FileId AND TagId = @TagId";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));
        cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public List<Guid> GetTagIdsForFile(Guid fileId)
    {
        List<Guid> ids = [];

        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TagId FROM cache.FileTags WHERE FileId = @FileId";
        cmd.Parameters.AddWithValue("@FileId", fileId.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }

    public List<Guid> GetFileIdsForTag(Guid tagId)
    {
        List<Guid> ids = [];

        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId FROM cache.FileTags WHERE TagId = @TagId";
        cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids;
    }
}