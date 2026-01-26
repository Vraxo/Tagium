using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class TagRepository
{
    private readonly DatabaseManager _db;

    public TagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<Tag> GetAll(TagSortBy sortBy = TagSortBy.Name, SortDirection direction = SortDirection.Asc)
    {
        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(new Tag
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ColorHex = reader.GetString(2),
                Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileCount = reader.GetInt32(4)
            });
        }

        return tags;
    }

    private static string GetOrderByClause(TagSortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            TagSortBy.FileCount => "FileCount",
            _ => "T.Name"
        };

        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}, T.Name ASC";
    }

    public List<Tag> GetTagsByIds(IEnumerable<long> ids)
    {
        if (!ids.Any())
        {
            return [];
        }

        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            WHERE T.Id IN (" + string.Join(",", ids) + @")
            GROUP BY T.Id";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(new Tag
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ColorHex = reader.GetString(2),
                Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
                FileCount = reader.GetInt32(4)
            });
        }

        return tags;
    }

    public void Add(string name, string colorHex, string? emoji)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tags (Name, ColorHex, Emoji) VALUES (@Name, @Color, @Emoji)";
        _ = cmd.Parameters.AddWithValue("@Name", name);
        _ = cmd.Parameters.AddWithValue("@Color", colorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)emoji ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore duplicates */ }
    }

    public void Update(Tag tag)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tags 
            SET Name = @Name, ColorHex = @Color, Emoji = @Emoji 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", tag.Id);
        _ = cmd.Parameters.AddWithValue("@Name", tag.Name);
        _ = cmd.Parameters.AddWithValue("@Color", tag.ColorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)tag.Emoji ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore name collision */ }
    }

    public void Delete(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);
        _ = cmd.ExecuteNonQuery();
    }
}