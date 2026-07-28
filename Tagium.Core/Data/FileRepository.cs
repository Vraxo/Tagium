using Dapper;
using Microsoft.Data.Sqlite;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileRepository
{
    private readonly DatabaseManager _db;

    public FileRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<FileEntry> GetAll(SortBy sortBy, SortDirection direction)
    {
        using SqliteConnection conn = _db.GetConnection();
        string orderBy = GetOrderByClause(sortBy, direction);
        string sql = $@"
            SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files F
            WHERE EXISTS (
                SELECT 1 FROM LibraryRoots LR 
                WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}')
            )
            {orderBy}";

        return conn.Query<FileEntry>(sql).ToList();
    }

    public List<FileEntry> GetFilesWithTags(SortBy sortBy, SortDirection direction, bool includeMissing = false)
    {
        using SqliteConnection conn = _db.GetConnection();
        string orderBy = GetOrderByClause(sortBy, direction);
        string rootFilter = $"EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}'))";
        string missingFilter = includeMissing ? $"WHERE {rootFilter}" : $"WHERE {rootFilter} AND F.IsMissing = 0";

        string sql = $@"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime,
                F.Rating, F.Title, F.Description,
                GROUP_CONCAT(T.Id || ':' || T.Name || ':' || T.ColorHex || ':' || IFNULL(T.Emoji, ''), ';;') AS TagData
            FROM cache.Files F
            LEFT JOIN cache.FileTags FT ON F.Id = FT.FileId
            LEFT JOIN Tags T ON FT.TagId = T.Id
            {missingFilter}
            GROUP BY F.Id
            {orderBy}";

        IEnumerable<FileEntryDto> dtos = conn.Query<FileEntryDto>(sql);
        foreach (FileEntryDto dto in dtos)
        {
            if (!string.IsNullOrEmpty(dto.TagData))
            {
                dto.Tags = ParseTagData(dto.TagData);
            }
        }
        return dtos.Cast<FileEntry>().ToList();
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime,
                F.Rating, F.Title, F.Description,
                GROUP_CONCAT(T.Id || ':' || T.Name || ':' || T.ColorHex || ':' || IFNULL(T.Emoji, ''), ';;') AS TagData
            FROM cache.Files F
            LEFT JOIN cache.FileTags FT ON F.Id = FT.FileId
            LEFT JOIN Tags T ON FT.TagId = T.Id
            WHERE F.Id = @Id
            GROUP BY F.Id";

        FileEntryDto? dto = conn.QueryFirstOrDefault<FileEntryDto>(sql, new { Id = id });
        if (dto != null && !string.IsNullOrEmpty(dto.TagData))
        {
            dto.Tags = ParseTagData(dto.TagData);
        }
        return dto;
    }

    public FileEntry? GetById(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.QueryFirstOrDefault<FileEntry>(
            "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Id = @Id",
            new { Id = id });
    }

    public FileEntry? GetByHash(string hash)
    {
        using SqliteConnection conn = _db.GetConnection();
        return conn.QueryFirstOrDefault<FileEntry>(
            "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Hash = @Hash",
            new { Hash = hash });
    }

    public List<FileEntry> GetFilesUnderPath(string rootPath)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
        return conn.Query<FileEntry>(sql, new { Path = rootPath }).ToList();
    }

    public bool Add(string path, string hash, long size, long modifiedTime)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            INSERT INTO cache.Files (Id, Path, Name, Hash, DateAdded, Size, ModifiedTime, Rating, Title, Description) 
            VALUES (@Id, @Path, @Name, @Hash, strftime('%s','now'), @Size, @ModifiedTime, NULL, NULL, NULL)";

        try
        {
            conn.Execute(sql, new
            {
                Id = Guid.NewGuid(),
                Path = path,
                Name = Path.GetFileName(path),
                Hash = hash,
                Size = size,
                ModifiedTime = modifiedTime
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Restore(FileEntry entry)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            INSERT INTO cache.Files (Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description) 
            VALUES (@Id, @Path, @Name, @Hash, @DateAdded, @IsMissing, @Size, @ModifiedTime, @Rating, @Title, @Description)";
        conn.Execute(sql, entry);
    }

    public void Update(FileEntry file)
    {
        using SqliteConnection conn = _db.GetConnection();
        string sql = @"
            UPDATE cache.Files 
            SET Path = @Path, Name = @Name, Hash = @Hash, IsMissing = @IsMissing, Size = @Size, ModifiedTime = @ModifiedTime, 
                Rating = @Rating, Title = @Title, Description = @Description 
            WHERE Id = @Id";
        conn.Execute(sql, file);
    }

    public void Remove(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM cache.Files WHERE Id = @Id", new { Id = id });
    }

    public void SetMissingStatus(IEnumerable<Guid> fileIds, bool isMissing)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteTransaction trans = conn.BeginTransaction();
        var parameters = fileIds.Select(id => new { Id = id, IsMissing = isMissing });
        conn.Execute("UPDATE cache.Files SET IsMissing = @IsMissing WHERE Id = @Id", parameters, trans);
        trans.Commit();
    }

    public void RemoveMissingFiles()
    {
        using SqliteConnection conn = _db.GetConnection();
        conn.Execute("DELETE FROM cache.Files WHERE IsMissing = 1");
    }

    private List<Tag> ParseTagData(string tagData)
    {
        return string.IsNullOrEmpty(tagData)
            ? []
            : tagData.Split(";;", StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTagFromData)
            .Where(t => t != null)
            .Cast<Tag>()
            .ToList();
    }

    private Tag? ParseTagFromData(string tagData)
    {
        string[] parts = tagData.Split(':', 4);
        return parts.Length < 3 || !Guid.TryParse(parts[0], out Guid tagId)
            ? null
            : new Tag
            {
                Id = tagId,
                Name = parts[1],
                ColorHex = parts[2],
                Emoji = parts.Length > 3 && parts[3] != "" ? parts[3] : null
            };
    }

    private static string GetOrderByClause(SortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            SortBy.Name => "F.Name",
            SortBy.DateAdded => "F.DateAdded",
            _ => "F.Path"
        };

        string dir = direction == SortDirection.Desc ? "DESC" : "ASC";
        return $"ORDER BY {col} {dir}";
    }

    private class FileEntryDto : FileEntry
    {
        public string? TagData { get; set; }
    }
}