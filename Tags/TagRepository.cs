using Dapper;
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
        using SqliteConnection conn = _db.GetConnection();
        string orderBy = GetOrderByClause(sortBy, direction);
        string sql = $@"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount, T.Category
            FROM Tags T
            LEFT JOIN cache.FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            {orderBy}";

        return conn.Query<Tag>(sql).ToList();
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

        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount, T.Category
            FROM Tags T
            LEFT JOIN cache.FileTags FT ON T.Id = FT.TagId
            WHERE T.Id IN @Ids
            GROUP BY T.Id";

        return conn.Query<Tag>(sql, new { Ids = idList }).ToList();
    }

    public void Add(string name, string colorHex, string? emoji, string? category = null)
    {
        using SqliteConnection conn = _db.GetConnection();
        try
        {
            conn.Execute("INSERT INTO Tags (Id, Name, ColorHex, Emoji, Category) VALUES (@Id, @Name, @ColorHex, @Emoji, @Category)",
                new { Id = Guid.NewGuid(), Name = name, ColorHex = colorHex, Emoji = emoji, Category = category });
        }
        catch { /* Ignore duplicates */ }
    }

    public void Update(Tag tag)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            UPDATE Tags 
            SET Name = @Name, ColorHex = @ColorHex, Emoji = @Emoji, Category = @Category 
            WHERE Id = @Id";

        try { conn.Execute(sql, tag); } catch { /* Ignore name collision */ }
    }

    public void Delete(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM Tags WHERE Id = @Id", new { Id = id });
    }

    public void AddHierarchy(Guid parentId, Guid childId)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("INSERT OR IGNORE INTO TagHierarchy (ParentTagId, ChildTagId) VALUES (@Parent, @Child)", new { Parent = parentId, Child = childId });
    }

    public void RemoveHierarchy(Guid parentId, Guid childId)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM TagHierarchy WHERE ParentTagId = @Parent AND ChildTagId = @Child", new { Parent = parentId, Child = childId });
    }

    public List<Guid> GetChildren(Guid parentId)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<Guid>("SELECT ChildTagId FROM TagHierarchy WHERE ParentTagId = @Parent", new { Parent = parentId }).ToList();
    }

    public List<Guid> GetParents(Guid childId)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<Guid>("SELECT ParentTagId FROM TagHierarchy WHERE ChildTagId = @Child", new { Child = childId }).ToList();
    }

    public void AddAlias(Guid tagId, string alias)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("INSERT OR IGNORE INTO TagAliases (Id, TagId, Alias) VALUES (@Id, @TagId, @Alias)", new { Id = Guid.NewGuid(), TagId = tagId, Alias = alias });
    }

    public void RemoveAlias(Guid tagId, string alias)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM TagAliases WHERE TagId = @TagId AND Alias = @Alias", new { TagId = tagId, Alias = alias });
    }

    public List<string> GetAliases(Guid tagId)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.Query<string>("SELECT Alias FROM TagAliases WHERE TagId = @TagId ORDER BY Alias ASC", new { TagId = tagId }).ToList();
    }

    public Guid? ResolveTagId(string input)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.QueryFirstOrDefault<Guid?>("SELECT Id FROM Tags WHERE Name = @Name", new { Name = input }) ??
               conn.QueryFirstOrDefault<Guid?>("SELECT TagId FROM TagAliases WHERE Alias = @Alias", new { Alias = input });
    }
}