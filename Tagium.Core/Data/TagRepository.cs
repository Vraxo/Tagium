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
        using SqliteConnection conn = _db.GetConnection();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount, T.Category
            FROM Tags T
            LEFT JOIN cache.FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(ReadTag(reader));
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

    public List<Tag> GetTagsByIds(IEnumerable<Guid> ids)
    {
        List<Guid> idList = ids.ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        List<Tag> tags = [];
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount, T.Category
            FROM Tags T
            LEFT JOIN cache.FileTags FT ON T.Id = FT.TagId
            WHERE T.Id IN (" + string.Join(",", idList.Select(id => $"'{id:N}'")) + @")
            GROUP BY T.Id";

        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            tags.Add(ReadTag(reader));
        }

        return tags;
    }

    public void Add(string name, string colorHex, string? emoji, string? category = null)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tags (Id, Name, ColorHex, Emoji, Category) VALUES (@Id, @Name, @Color, @Emoji, @Category)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Name", name);
        _ = cmd.Parameters.AddWithValue("@Color", colorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)emoji ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Category", (object?)category ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore duplicates */ }
    }

    public void Update(Tag tag)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Tags 
            SET Name = @Name, ColorHex = @Color, Emoji = @Emoji, Category = @Category 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", tag.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Name", tag.Name);
        _ = cmd.Parameters.AddWithValue("@Color", tag.ColorHex);
        _ = cmd.Parameters.AddWithValue("@Emoji", (object?)tag.Emoji ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Category", (object?)tag.Category ?? DBNull.Value);

        try { _ = cmd.ExecuteNonQuery(); } catch { /* Ignore name collision */ }
    }

    public void Delete(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Tags WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void AddHierarchy(Guid parentId, Guid childId)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO TagHierarchy (ParentTagId, ChildTagId) VALUES (@Parent, @Child)";
        _ = cmd.Parameters.AddWithValue("@Parent", parentId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Child", childId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveHierarchy(Guid parentId, Guid childId)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TagHierarchy WHERE ParentTagId = @Parent AND ChildTagId = @Child";
        _ = cmd.Parameters.AddWithValue("@Parent", parentId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Child", childId.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public List<Guid> GetChildren(Guid parentId)
    {
        List<Guid> list = [];
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChildTagId FROM TagHierarchy WHERE ParentTagId = @Parent";
        _ = cmd.Parameters.AddWithValue("@Parent", parentId.ToString("N"));
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(Guid.Parse(r.GetString(0)));
        }
        return list;
    }

    public List<Guid> GetParents(Guid childId)
    {
        List<Guid> list = [];
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ParentTagId FROM TagHierarchy WHERE ChildTagId = @Child";
        _ = cmd.Parameters.AddWithValue("@Child", childId.ToString("N"));
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(Guid.Parse(r.GetString(0)));
        }
        return list;
    }

    public void AddAlias(Guid tagId, string alias)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO TagAliases (Id, TagId, Alias) VALUES (@Id, @TagId, @Alias)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Alias", alias);
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveAlias(Guid tagId, string alias)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM TagAliases WHERE TagId = @TagId AND Alias = @Alias";
        _ = cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Alias", alias);
        _ = cmd.ExecuteNonQuery();
    }

    public List<string> GetAliases(Guid tagId)
    {
        List<string> list = [];
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Alias FROM TagAliases WHERE TagId = @TagId ORDER BY Alias ASC";
        _ = cmd.Parameters.AddWithValue("@TagId", tagId.ToString("N"));
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(r.GetString(0));
        }
        return list;
    }

    public Guid? ResolveTagId(string input)
    {
        using SqliteConnection conn = _db.GetConnection();
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM Tags WHERE Name = @Name";
            _ = cmd.Parameters.AddWithValue("@Name", input);
            string? id = (string?)cmd.ExecuteScalar();
            if (id != null)
            {
                return Guid.Parse(id);
            }
        }
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT TagId FROM TagAliases WHERE Alias = @Alias";
            _ = cmd.Parameters.AddWithValue("@Alias", input);
            string? id = (string?)cmd.ExecuteScalar();
            if (id != null)
            {
                return Guid.Parse(id);
            }
        }
        return null;
    }

    private static Tag ReadTag(SqliteDataReader reader)
    {
        return new Tag
        {
            Id = Guid.Parse(reader.GetString(0)),
            Name = reader.GetString(1),
            ColorHex = reader.GetString(2),
            Emoji = reader.IsDBNull(3) ? null : reader.GetString(3),
            FileCount = reader.GetInt32(4),
            Category = reader.IsDBNull(5) ? null : reader.GetString(5)
        };
    }
}