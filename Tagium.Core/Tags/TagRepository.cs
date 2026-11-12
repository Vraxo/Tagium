using Dapper;
using Microsoft.Data.Sqlite;
using Tagium.Core;
using Tagium.Core.Settings;

namespace Tagium.Core.Tags;

public class TagRepository
{
    private readonly DatabaseManager _db;

    public TagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public long GetCount()
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Tags");
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

        return TagOrdering.ByName(conn.Query<Tag>(sql, new { Ids = idList }));
    }

    public void Add(string name, string colorHex, string? emoji, string? category = null)
    {
        string canonicalName = TagFormatter.CanonicalizeName(name);
        if (canonicalName.Length == 0)
        {
            return;
        }

        using SqliteConnection conn = _db.GetConnection();
        try
        {
            conn.Execute("INSERT INTO Tags (Id, Name, ColorHex, Emoji, Category) VALUES (@Id, @Name, @ColorHex, @Emoji, @Category)",
                new { Id = Guid.NewGuid(), Name = canonicalName, ColorHex = colorHex, Emoji = emoji, Category = category });
        }
        catch { /* Ignore duplicates */ }
    }

    public void Update(Tag tag)
    {
        tag.Name = TagFormatter.CanonicalizeName(tag.Name);
        if (tag.Name.Length == 0)
        {
            return;
        }

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

    public TagGraphData GetGraphData(int maxNodes = 120)
    {
        using SqliteConnection conn = _db.GetConnection();
        List<Tag> nodes = conn.Query<Tag>(@"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) AS FileCount, T.Category
            FROM Tags T
            LEFT JOIN cache.FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            ORDER BY FileCount DESC, T.Name COLLATE NOCASE ASC
            LIMIT @Limit", new { Limit = maxNodes }).ToList();

        if (nodes.Count == 0)
        {
            return new TagGraphData(nodes, []);
        }

        List<Guid> nodeIds = nodes.Select(node => node.Id).ToList();
        Dictionary<(Guid Source, Guid Target), (int SharedCount, bool IsHierarchy, Guid DirectedSource, Guid DirectedTarget)> edgeMap = [];

        IEnumerable<dynamic> overlaps = conn.Query(@"
            SELECT A.TagId AS SourceTagId, B.TagId AS TargetTagId, COUNT(DISTINCT A.FileId) AS SharedFileCount
            FROM cache.FileTags A
            INNER JOIN cache.FileTags B ON A.FileId = B.FileId AND A.TagId < B.TagId
            WHERE A.TagId IN @TagIds AND B.TagId IN @TagIds
            GROUP BY A.TagId, B.TagId", new { TagIds = nodeIds });

        foreach (dynamic row in overlaps)
        {
            Guid source = Guid.Parse(Convert.ToString(row.SourceTagId)!);
            Guid target = Guid.Parse(Convert.ToString(row.TargetTagId)!);
            edgeMap[(source, target)] = (Convert.ToInt32(row.SharedFileCount), false, source, target);
        }

        IEnumerable<dynamic> hierarchy = conn.Query(@"
            SELECT ParentTagId AS SourceTagId, ChildTagId AS TargetTagId
            FROM TagHierarchy
            WHERE ParentTagId IN @TagIds AND ChildTagId IN @TagIds", new { TagIds = nodeIds });

        foreach (dynamic row in hierarchy)
        {
            Guid source = Guid.Parse(Convert.ToString(row.SourceTagId)!);
            Guid target = Guid.Parse(Convert.ToString(row.TargetTagId)!);
            (Guid, Guid) pair = CompareIds(source, target);
            int shared = edgeMap.TryGetValue(pair, out var existing) ? existing.SharedCount : 0;
            edgeMap[pair] = (shared, true, source, target);
        }

        List<TagGraphEdge> edges = edgeMap
            .Select(edge => new TagGraphEdge(
                edge.Value.IsHierarchy ? edge.Value.DirectedSource : edge.Key.Source,
                edge.Value.IsHierarchy ? edge.Value.DirectedTarget : edge.Key.Target,
                edge.Value.SharedCount,
                edge.Value.IsHierarchy))
            .ToList();
        return new TagGraphData(nodes, edges);
    }

    private static (Guid Source, Guid Target) CompareIds(Guid first, Guid second)
    {
        return string.CompareOrdinal(first.ToString("N"), second.ToString("N")) <= 0
            ? (first, second)
            : (second, first);
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
        string canonicalInput = TagFormatter.CanonicalizeName(input);
        if (canonicalInput.Length == 0)
        {
            return null;
        }

        using SqliteConnection conn = _db.GetConnection();
        return conn.QueryFirstOrDefault<Guid?>("SELECT Id FROM Tags WHERE Name = @Name", new { Name = canonicalInput }) ??
               conn.QueryFirstOrDefault<Guid?>("SELECT TagId FROM TagAliases WHERE Alias = @Alias", new { Alias = input.Trim() });
    }
}