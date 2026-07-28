### `Tagium.Core\Tagium.Core.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

	<PropertyGroup>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="FFMpegCore" Version="5.4.0" />
		<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.10" />
		<PackageReference Include="SkiaSharp" Version="4.150.1" />
		<PackageReference Include="Serilog" Version="4.4.0" />
		<PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
		<PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
		<PackageReference Include="Serilog.Extensions.Logging" Version="10.0.0" />
	</ItemGroup>

</Project>
```

---

### `Tagium.Core\Data\DatabaseManager.cs`

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Tagium.Core.Helpers;

namespace Tagium.Core.Data;

public class DatabaseManager
{
    public string LibraryDbPath { get; }
    public string CacheDbPath { get; }
    private readonly ILogger<DatabaseManager> _logger;

    public DatabaseManager(ILogger<DatabaseManager> logger)
    {
        _logger = logger;
        string baseDir = AppDataHelper.GetBaseDirectory();
        LibraryDbPath = Path.Combine(baseDir, "library.db");
        CacheDbPath = Path.Combine(baseDir, "cache.db");
        InitializeDatabases();
    }

    public SqliteConnection GetConnection()
    {
        SqliteConnection connection = new($"Data Source={LibraryDbPath}");
        connection.Open();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
            _ = cmd.ExecuteNonQuery();
        }

        bool isAttached = false;
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA database_list;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(1);
                if (string.Equals(name, "cache", StringComparison.OrdinalIgnoreCase))
                {
                    isAttached = true;
                    break;
                }
            }
        }

        if (!isAttached)
        {
            using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = $"ATTACH DATABASE '{CacheDbPath}' AS cache;";
            _ = cmd.ExecuteNonQuery();
        }

        return connection;
    }

    private void InitializeDatabases()
    {
        _logger.LogInformation("Initializing databases. Library DB: {LibraryPath}, Cache DB: {CachePath}", LibraryDbPath, CacheDbPath);

        try
        {
            using SqliteConnection conn = new($"Data Source={LibraryDbPath}");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS Tags (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    ColorHex TEXT NOT NULL DEFAULT '#757575',
                    Emoji TEXT NULL,
                    Category TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS TagHierarchy (
                    ParentTagId TEXT NOT NULL,
                    ChildTagId TEXT NOT NULL,
                    FOREIGN KEY(ParentTagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                    FOREIGN KEY(ChildTagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                    PRIMARY KEY (ParentTagId, ChildTagId)
                );

                CREATE TABLE IF NOT EXISTS TagAliases (
                    Id TEXT PRIMARY KEY,
                    TagId TEXT NOT NULL,
                    Alias TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS LibraryRoots (
                    Id TEXT PRIMARY KEY,
                    Path TEXT NOT NULL UNIQUE,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    LastScanTime INTEGER NULL
                );
            ";
            _ = cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize library database");
            throw;
        }

        try
        {
            using SqliteConnection conn = new($"Data Source={CacheDbPath}");
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS Files (
                    Id TEXT PRIMARY KEY,
                    Path TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL DEFAULT '',
                    Hash TEXT NULL,
                    DateAdded INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
                    IsMissing INTEGER NOT NULL DEFAULT 0,
                    Size INTEGER NOT NULL DEFAULT 0,
                    ModifiedTime INTEGER NOT NULL DEFAULT 0,
                    Rating INTEGER NULL,
                    Title TEXT NULL,
                    Description TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS FileTags (
                    FileId TEXT NOT NULL,
                    TagId TEXT NOT NULL,
                    FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                    PRIMARY KEY (FileId, TagId)
                );
            ";
            _ = cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize cache database");
            throw;
        }

        MigrateSchema();
    }

    private void MigrateSchema()
    {
        _logger.LogInformation("Checking schema migrations...");
        using SqliteConnection conn = GetConnection();
        string[] columns = ["Size", "ModifiedTime", "Rating", "Title", "Description"];
        foreach (string col in columns)
        {
            try
            {
                using SqliteCommand cmd = conn.CreateCommand();
                string colType = col switch
                {
                    "Rating" => "INTEGER NULL",
                    "Title" or "Description" => "TEXT NULL",
                    _ => "INTEGER NOT NULL DEFAULT 0"
                };
                cmd.CommandText = $"ALTER TABLE cache.Files ADD COLUMN {col} {colType};";
                _ = cmd.ExecuteNonQuery();
                _logger.LogInformation("Successfully migrated column {Column} in cache.Files", col);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                _logger.LogDebug("Column {Column} already exists in cache.Files, skipping.", col);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ignored error during column migration for {Column}", col);
            }
        }

        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Tags ADD COLUMN Category TEXT NULL;";
            _ = cmd.ExecuteNonQuery();
            _logger.LogInformation("Successfully migrated column Category in Tags");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            _logger.LogDebug("Column Category already exists in Tags, skipping.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignored error during Tags category column migration");
        }
    }
}
```

---

### `Tagium.Core\Data\FileRepository.cs`

```csharp
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
        List<FileEntry> files = [];
        using SqliteConnection conn = _db.GetConnection();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files F
            WHERE EXISTS (
                SELECT 1 FROM LibraryRoots LR 
                WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}')
            )
            {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public List<FileEntry> GetFilesWithTags(SortBy sortBy, SortDirection direction, bool includeMissing = false)
    {
        List<FileEntry> files = [];
        using SqliteConnection conn = _db.GetConnection();

        string orderBy = GetOrderByClause(sortBy, direction);
        string rootFilter = $"EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '{Path.DirectorySeparatorChar}'))";
        string missingFilter = includeMissing ? $"WHERE {rootFilter}" : $"WHERE {rootFilter} AND F.IsMissing = 0";

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $@"
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

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            FileEntry file = ReadFile(reader);

            if (!reader.IsDBNull(11))
            {
                string tagData = reader.GetString(11);
                file.Tags = ParseTagData(tagData);
            }

            files.Add(file);
        }
        return files;
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime,
                F.Rating, F.Title, F.Description,
                GROUP_CONCAT(T.Id || ':' || T.Name || ':' || T.ColorHex || ':' || IFNULL(T.Emoji, ''), ';;') AS TagData
            FROM cache.Files F
            LEFT JOIN cache.FileTags FT ON F.Id = FT.FileId
            LEFT JOIN Tags T ON FT.TagId = T.Id
            WHERE F.Id = @Id
            GROUP BY F.Id";

        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            FileEntry file = ReadFile(reader);
            if (!reader.IsDBNull(11))
            {
                string tagData = reader.GetString(11);
                file.Tags = ParseTagData(tagData);
            }
            return file;
        }
        return null;
    }

    public FileEntry? GetById(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public FileEntry? GetByHash(string hash)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Hash = @Hash";
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public List<FileEntry> GetFilesUnderPath(string rootPath)
    {
        List<FileEntry> files = [];
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM cache.Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
        _ = cmd.Parameters.AddWithValue("@Path", rootPath);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public bool Add(string path, string hash, long size, long modifiedTime)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO cache.Files (Id, Path, Name, Hash, DateAdded, Size, ModifiedTime, Rating, Title, Description) 
            VALUES (@Id, @Path, @Name, @Hash, strftime('%s','now'), @Size, @ModifiedTime, NULL, NULL, NULL)";

        Guid newId = Guid.NewGuid();
        _ = cmd.Parameters.AddWithValue("@Id", newId.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);
        _ = cmd.Parameters.AddWithValue("@Size", size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", modifiedTime);

        try
        {
            _ = cmd.ExecuteNonQuery();
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

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO cache.Files (Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description) 
            VALUES (@Id, @Path, @Name, @Hash, @DateAdded, @IsMissing, @Size, @ModifiedTime, @Rating, @Title, @Description)";

        _ = cmd.Parameters.AddWithValue("@Id", entry.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", entry.Path);
        _ = cmd.Parameters.AddWithValue("@Name", entry.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)entry.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@DateAdded", entry.DateAdded);
        _ = cmd.Parameters.AddWithValue("@IsMissing", entry.IsMissing ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@Size", entry.Size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", entry.ModifiedTime);
        _ = cmd.Parameters.AddWithValue("@Rating", (object?)entry.Rating ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Title", (object?)entry.Title ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Description", (object?)entry.Description ?? DBNull.Value);

        _ = cmd.ExecuteNonQuery();
    }

    public void Update(FileEntry file)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE cache.Files 
            SET Path = @Path, Name = @Name, Hash = @Hash, IsMissing = @IsMissing, Size = @Size, ModifiedTime = @ModifiedTime, 
                Rating = @Rating, Title = @Title, Description = @Description 
            WHERE Id = @Id";

        _ = cmd.Parameters.AddWithValue("@Id", file.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", file.Path);
        _ = cmd.Parameters.AddWithValue("@Name", file.Name);
        _ = cmd.Parameters.AddWithValue("@Hash", (object?)file.Hash ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@IsMissing", file.IsMissing ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@Size", file.Size);
        _ = cmd.Parameters.AddWithValue("@ModifiedTime", file.ModifiedTime);
        _ = cmd.Parameters.AddWithValue("@Rating", (object?)file.Rating ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Title", (object?)file.Title ?? DBNull.Value);
        _ = cmd.Parameters.AddWithValue("@Description", (object?)file.Description ?? DBNull.Value);

        _ = cmd.ExecuteNonQuery();
    }

    public void Remove(Guid id)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cache.Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
        _ = cmd.ExecuteNonQuery();
    }

    public void SetMissingStatus(IEnumerable<Guid> fileIds, bool isMissing)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteTransaction trans = conn.BeginTransaction();

        foreach (Guid id in fileIds)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = "UPDATE cache.Files SET IsMissing = @IsMissing WHERE Id = @Id";
            _ = cmd.Parameters.AddWithValue("@IsMissing", isMissing ? 1 : 0);
            _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
            _ = cmd.ExecuteNonQuery();
        }
        trans.Commit();
    }

    public void RemoveMissingFiles()
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cache.Files WHERE IsMissing = 1";
        _ = cmd.ExecuteNonQuery();
    }

    private static FileEntry ReadFile(SqliteDataReader reader)
    {
        return new FileEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
            DateAdded = reader.GetInt64(4),
            IsMissing = reader.GetBoolean(5),
            Size = reader.GetInt64(6),
            ModifiedTime = reader.GetInt64(7),
            Rating = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            Title = reader.IsDBNull(9) ? null : reader.GetString(9),
            Description = reader.IsDBNull(10) ? null : reader.GetString(10),
            Tags = []
        };
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
}
```

---

### `Tagium.Core\Data\FileSearchRepository.cs`

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;
    private readonly SearchQueryCompiler _compiler;
    private readonly ILogger<FileSearchRepository> _logger;

    public FileSearchRepository(DatabaseManager db, SearchQueryCompiler compiler, ILogger<FileSearchRepository> logger)
    {
        _db = db;
        _compiler = compiler;
        _logger = logger;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        using LogTiming timer = LogTiming.StartDebug(_logger, "Search database execution for compiled query: {Query}", searchText);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            return [];
        }

        CompiledQuery compiled = _compiler.Compile(searchText, sortBy, sortDirection);

        using SqliteConnection conn = _db.GetConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;

        foreach (var (name, value) in compiled.Parameters)
        {
            _ = cmd.Parameters.AddWithValue(name, value);
        }

        _logger.LogDebug("Executing SQL Query: {Sql}", cmd.CommandText);

        List<FileEntry> files = [];
        using SqliteDataReader reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            files.Add(new FileEntry
            {
                Id = Guid.Parse(reader.GetString(0)),
                Path = reader.GetString(1),
                Name = reader.GetString(2),
                Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                DateAdded = reader.GetInt64(4),
                IsMissing = reader.GetBoolean(5),
                Size = reader.GetInt64(6),
                ModifiedTime = reader.GetInt64(7),
                Rating = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Title = reader.IsDBNull(9) ? null : reader.GetString(9),
                Description = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        _logger.LogInformation("Database query execution completed, returned {Count} items", files.Count);
        return files;
    }
}
```

---

### `Tagium.Core\Data\FileTagRepository.cs`

```csharp
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
```

---

### `Tagium.Core\Data\LibraryRootRepository.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Models;

namespace Tagium.Core.Data;

public class LibraryRootRepository
{
    private readonly DatabaseManager _db;

    public LibraryRootRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<LibraryRoot> GetAll()
    {
        List<LibraryRoot> roots = [];
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, IsEnabled, LastScanTime FROM LibraryRoots ORDER BY Path ASC";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            roots.Add(ReadRoot(reader));
        }
        return roots;
    }

    public void Add(string path)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO LibraryRoots (Id, Path, IsEnabled, LastScanTime) VALUES (@Id, @Path, 1, NULL)";
        _ = cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.ExecuteNonQuery();
    }

    public void Update(LibraryRoot root)
    {
        using SqliteConnection conn = _db.GetConnection();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE LibraryRoots SET IsEnabled = @IsEnabled, LastScanTime = @LastScanTime WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", root.Id.ToString("N"));
        _ = cmd.Parameters.AddWithValue("@IsEnabled", root.IsEnabled ? 1 : 0);
        _ = cmd.Parameters.AddWithValue("@LastScanTime", (object?)root.LastScanTime ?? DBNull.Value);
        _ = cmd.ExecuteNonQuery();
    }

    public void Delete(Guid id, string path)
    {
        using SqliteConnection conn = _db.GetConnection();
        using SqliteTransaction trans = conn.BeginTransaction();

        try
        {
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = "DELETE FROM LibraryRoots WHERE Id = @Id";
                _ = cmd.Parameters.AddWithValue("@Id", id.ToString("N"));
                _ = cmd.ExecuteNonQuery();
            }

            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.Transaction = trans;
                cmd.CommandText = $"DELETE FROM cache.Files WHERE Path = @Path OR SUBSTR(Path, 1, LENGTH(@Path) + 1) = @Path || '{Path.DirectorySeparatorChar}'";
                _ = cmd.Parameters.AddWithValue("@Path", path);
                _ = cmd.ExecuteNonQuery();
            }

            trans.Commit();
        }
        catch
        {
            trans.Rollback();
            throw;
        }
    }

    private static LibraryRoot ReadRoot(SqliteDataReader reader)
    {
        return new LibraryRoot
        {
            Id = Guid.Parse(reader.GetString(0)),
            Path = reader.GetString(1),
            IsEnabled = reader.GetBoolean(2),
            LastScanTime = reader.IsDBNull(3) ? null : reader.GetInt64(3)
        };
    }
}
```

---

### `Tagium.Core\Data\SearchQueryCompiler.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Text;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public record CompiledQuery(string Sql, List<(string Name, object Value)> Parameters);

public class SearchQueryCompiler
{
    private readonly ILogger<SearchQueryCompiler> _logger;

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "artist", "character", "series", "location", "person", "franchise", "species", "year", "meta"
    };

    public SearchQueryCompiler(ILogger<SearchQueryCompiler> logger)
    {
        _logger = logger;
    }

    public CompiledQuery Compile(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        string[] tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        StringBuilder query = new();
        query.AppendLine("SELECT DISTINCT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing, F.Size, F.ModifiedTime, F.Rating, F.Title, F.Description");
        query.AppendLine("FROM cache.Files F");
        query.AppendLine("WHERE EXISTS (SELECT 1 FROM LibraryRoots LR WHERE LR.IsEnabled = 1 AND (F.Path = LR.Path OR SUBSTR(F.Path, 1, LENGTH(LR.Path) + 1) = LR.Path || '" + Path.DirectorySeparatorChar + "'))");

        List<(string Name, object Value)> parameters = [];
        int paramCounter = 0;

        foreach (string token in tokens)
        {
            bool isNegated = false;
            string rawToken = token;

            if (rawToken.StartsWith('-') && rawToken.Length > 1)
            {
                isNegated = true;
                rawToken = rawToken[1..];
            }

            int colonIdx = rawToken.IndexOf(':');
            if (colonIdx > 0 && colonIdx < rawToken.Length - 1)
            {
                string key = rawToken[..colonIdx];
                string val = rawToken[(colonIdx + 1)..];

                if (string.Equals(key, "rating", StringComparison.OrdinalIgnoreCase))
                {
                    BuildRatingClause(val, isNegated, query, parameters, ref paramCounter);
                }
                else if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase))
                {
                    BuildSizeClause(val, isNegated, query, parameters, ref paramCounter);
                }
                else if (string.Equals(key, "ext", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "extension", StringComparison.OrdinalIgnoreCase))
                {
                    string extPattern = "%." + val.TrimStart('.').ToLowerInvariant();
                    string paramName = $"@p{paramCounter++}";
                    parameters.Add((paramName, extPattern));

                    if (isNegated)
                    {
                        query.AppendLine($"AND F.Path NOT LIKE {paramName}");
                    }
                    else
                    {
                        query.AppendLine($"AND F.Path LIKE {paramName}");
                    }
                }
                else if (ValidCategories.Contains(key))
                {
                    BuildCategoryClause(key, val, isNegated, query, parameters, ref paramCounter);
                }
                else
                {
                    BuildTagSearchClause(rawToken, isNegated, query, parameters, ref paramCounter);
                }
            }
            else
            {
                BuildTagSearchClause(rawToken, isNegated, query, parameters, ref paramCounter);
            }
        }

        query.AppendLine(GetOrderByClause(sortBy, sortDirection));

        return new CompiledQuery(query.ToString(), parameters);
    }

    private void BuildRatingClause(string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        (string op, string rStr) = SplitOperator(val);
        if (int.TryParse(rStr, out int rVal))
        {
            string paramName = $"@p{paramCounter++}";
            parameters.Add((paramName, rVal));

            if (isNegated)
            {
                query.AppendLine($"AND (F.Rating IS NULL OR NOT (F.Rating {op} {paramName}))");
            }
            else
            {
                query.AppendLine($"AND (F.Rating IS NOT NULL AND F.Rating {op} {paramName})");
            }
        }
        else
        {
            _logger.LogWarning("Skipped invalid rating search filter value: {Value}", val);
        }
    }

    private void BuildSizeClause(string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        (string op, string sStr) = SplitOperator(val);
        long? bytes = ParseSizeToBytes(sStr);
        if (bytes.HasValue)
        {
            string paramName = $"@p{paramCounter++}";
            parameters.Add((paramName, bytes.Value));

            if (isNegated)
            {
                query.AppendLine($"AND NOT (F.Size {op} {paramName})");
            }
            else
            {
                query.AppendLine($"AND F.Size {op} {paramName}");
            }
        }
        else
        {
            _logger.LogWarning("Skipped invalid size search filter value: {Value}", val);
        }
    }

    private void BuildCategoryClause(string key, string val, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string searchPattern = "%" + val + "%";
        string catParam = $"@p{paramCounter++}";
        string valParam = $"@p{paramCounter++}";

        parameters.Add((catParam, key));
        parameters.Add((valParam, searchPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND T.Category = {catParam} AND (T.Name LIKE {valParam} OR TA.Alias LIKE {valParam})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT ({subQuery})");
        }
        else
        {
            query.AppendLine($"AND ({subQuery})");
        }
    }

    private void BuildTagSearchClause(string token, bool isNegated, StringBuilder query, List<(string Name, object Value)> parameters, ref int paramCounter)
    {
        string paramName = $"@p{paramCounter++}";
        string termPattern = $"%{token}%";
        parameters.Add((paramName, termPattern));

        string subQuery = $@"
            EXISTS (
                SELECT 1 FROM cache.FileTags FT
                JOIN Tags T ON FT.TagId = T.Id
                LEFT JOIN TagAliases TA ON T.Id = TA.TagId
                WHERE FT.FileId = F.Id AND (T.Name LIKE {paramName} OR TA.Alias LIKE {paramName})
            )";

        if (isNegated)
        {
            query.AppendLine($"AND NOT (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
        else
        {
            query.AppendLine($"AND (F.Name LIKE {paramName} OR F.Path LIKE {paramName} OR {subQuery})");
        }
    }

    private static (string op, string rest) SplitOperator(string val)
    {
        if (val.StartsWith(">="))
        {
            return (">=", val[2..]);
        }

        if (val.StartsWith("<="))
        {
            return ("<=", val[2..]);
        }

        if (val.StartsWith(">"))
        {
            return (">", val[1..]);
        }

        if (val.StartsWith("<"))
        {
            return ("<", val[1..]);
        }

        if (val.StartsWith("="))
        {
            return ("=", val[1..]);
        }

        return ("=", val);
    }

    private static long? ParseSizeToBytes(string val)
    {
        val = val.Trim().ToLowerInvariant();
        int idx = 0;
        while (idx < val.Length && (char.IsDigit(val[idx]) || val[idx] == '.'))
        {
            idx++;
        }

        if (idx == 0)
        {
            return null;
        }

        string numPart = val[..idx];
        string unitPart = val[idx..].Trim();

        if (!double.TryParse(numPart, out double num))
        {
            return null;
        }

        double multiplier = unitPart switch
        {
            "kb" or "k" => 1024.0,
            "mb" or "m" => 1024.0 * 1024.0,
            "gb" or "g" => 1024.0 * 1024.0 * 1024.0,
            _ => 1.0
        };

        return (long)(num * multiplier);
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
        return $"ORDER BY {col} {dir}, F.Path ASC";
    }
}
```

---

### `Tagium.Core\Data\TagRepository.cs`

```csharp
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
```

---

### `Tagium.Core\Helpers\AppDataHelpers.cs`

```csharp
namespace Tagium.Core.Helpers;

public static class AppDataHelper
{
    private const string AppName = "Tagium";

    public static string GetBaseDirectory()
    {
        // Using LocalApplicationData to be distinct from the old "Tagra" app
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string path = Path.Combine(appData, AppName);
        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }
        return path;
    }

    public static string GetDatabasePath()
    {
        return Path.Combine(GetBaseDirectory(), "tagium.db");
    }

    public static string GetThumbnailCacheDirectory()
    {
        string path = Path.Combine(GetBaseDirectory(), "Thumbnails");

        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }

        return path;
    }

    public static string GetThemesDirectory()
    {
        string path = Path.Combine(GetBaseDirectory(), "Themes");

        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }

        return path;
    }
}
```

---

### `Tagium.Core\Helpers\FileHasher.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public class FileHasher
{
    private readonly ILogger<FileHasher> _logger;

    public FileHasher(ILogger<FileHasher> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ComputeSha256Async(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File does not exist for hashing: {FilePath}", filePath);
            return null;
        }

        using LogTiming timer = LogTiming.StartDebug(_logger, "SHA256 computation for {FilePath}", filePath);

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            long length = stream.Length;

            if (length <= 256 * 1024)
            {
                using SHA256 sha256 = SHA256.Create();
                byte[] hash = await sha256.ComputeHashAsync(stream);
                return Convert.ToHexStringLower(hash);
            }

            byte[] buffer = new byte[256 * 1024];

            int bytesReadStart = await stream.ReadAsync(buffer.AsMemory(0, 128 * 1024));

            stream.Seek(-128 * 1024, SeekOrigin.End);
            int bytesReadEnd = await stream.ReadAsync(buffer.AsMemory(128 * 1024, 128 * 1024));

            using SHA256 sha256Combined = SHA256.Create();
            byte[] hashBytes = sha256Combined.ComputeHash(buffer, 0, bytesReadStart + bytesReadEnd);

            return $"{length:X}-{Convert.ToHexStringLower(hashBytes)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing SHA256 for file: {FilePath}", filePath);
            return null;
        }
    }
}
```

---

### `Tagium.Core\Helpers\LogTiming.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Tagium.Core.Helpers;

public readonly struct LogTiming : IDisposable
{
    private readonly ILogger _logger;
    private readonly LogLevel _level;
    private readonly string _messageTemplate;
    private readonly object?[]? _args;
    private readonly long _startTimestamp;

    private LogTiming(ILogger logger, LogLevel level, string messageTemplate, object?[]? args)
    {
        _logger = logger;
        _level = level;
        _messageTemplate = messageTemplate;
        _args = args;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public static LogTiming Start(ILogger logger, string messageTemplate, params object?[]? args)
    {
        return new LogTiming(logger, LogLevel.Information, messageTemplate, args);
    }

    public static LogTiming StartDebug(ILogger logger, string messageTemplate, params object?[]? args)
    {
        return new LogTiming(logger, LogLevel.Debug, messageTemplate, args);
    }

    public void Dispose()
    {
        long elapsed = Stopwatch.GetTimestamp() - _startTimestamp;
        double ms = (double)elapsed * 1000 / Stopwatch.Frequency;

        string format = $"{_messageTemplate} completed in {{ElapsedMs:F2}} ms";

        object?[] finalArgs;
        if (_args == null || _args.Length == 0)
        {
            finalArgs = [ms];
        }
        else
        {
            finalArgs = new object?[_args.Length + 1];
            Array.Copy(_args, finalArgs, _args.Length);
            finalArgs[_args.Length] = ms;
        }

        _logger.Log(_level, format, finalArgs);
    }
}
```

---

### `Tagium.Core\Helpers\TagFormatter.cs`

```csharp
using System.Text.RegularExpressions;

namespace Tagium.Core.Helpers;

public static class TagFormatter
{
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        string s = input.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"[\s\-_\+\.]+", "_");
        s = Regex.Replace(s, @"[^a-z0-9_]", "");
        s = Regex.Replace(s, @"_+", "_");
        return s.Trim('_');
    }
}
```

---

### `Tagium.Core\Models\FileEntry.cs`

```csharp
namespace Tagium.Core.Models;

public class FileEntry
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public long Size { get; set; }
    public long ModifiedTime { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public int? Rating { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
```

---

### `Tagium.Core\Models\LibraryRoot.cs`

```csharp
namespace Tagium.Core.Models;

public class LibraryRoot
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long? LastScanTime { get; set; }
}
```

---

### `Tagium.Core\Models\MediaMetadata.cs`

```csharp
namespace Tagium.Core.Models;

public class MediaMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public int? Rating { get; set; }
    public List<string> Tags { get; set; } = [];
}
```

---

### `Tagium.Core\Models\Tag.cs`

```csharp
namespace Tagium.Core.Models;

public class Tag
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }
    public int FileCount { get; set; }
    public string? Category { get; set; }

    public override string ToString()
    {
        return Name;
    }
}
```

---

### `Tagium.Core\Services\ExifToolService.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ExifToolService : IDisposable
{
    private readonly string _commandName;
    private Process? _exifToolProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly object _lock = new();
    private bool _isDisposed;
    private readonly ILogger<ExifToolService> _logger;

    public bool IsEnabled { get; private set; }

    public ExifToolService(ILogger<ExifToolService> logger)
    {
        _logger = logger;
        _commandName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exiftool.exe" : "exiftool";
        InitializeProcess();
    }

    private void InitializeProcess()
    {
        try
        {
            ProcessStartInfo startInfo = new(_commandName)
            {
                Arguments = "-stay_open True -@ -",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false)
            };

            _exifToolProcess = Process.Start(startInfo);
            if (_exifToolProcess is not null)
            {
                _stdin = _exifToolProcess.StandardInput;
                _stdout = _exifToolProcess.StandardOutput;

                _exifToolProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogDebug("ExifTool Process Output: {Warning}", e.Data);
                    }
                };
                _exifToolProcess.BeginErrorReadLine();

                IsEnabled = !_exifToolProcess.HasExited;
                if (IsEnabled)
                {
                    _logger.LogInformation("Persistent ExifTool daemon successfully started");
                }
            }
        }
        catch (Exception ex)
        {
            IsEnabled = false;
            _logger.LogWarning(ex, "Failed to initialize persistent ExifTool daemon. Metadata reading and writing will be disabled");
        }
    }

    public async Task<MediaMetadata?> ReadMetadataAsync(string filePath)
    {
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return null;
        }

        using LogTiming timer = LogTiming.StartDebug(_logger, "ExifTool read metadata for {FilePath}", filePath);

        try
        {
            string output;
            lock (_lock)
            {
                _stdin!.WriteLine("-m");
                _stdin!.WriteLine("-json");
                _stdin!.WriteLine("-Subject");
                _stdin!.WriteLine("-Keywords");
                _stdin!.WriteLine("-Title");
                _stdin!.WriteLine("-Description");
                _stdin!.WriteLine("-Rating");
                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                StringBuilder sb = new();
                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    sb.AppendLine(line);
                }
                output = sb.ToString();
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            JsonElement first = doc.RootElement[0];
            MediaMetadata metadata = new();

            if (first.TryGetProperty("Title", out JsonElement titleProp))
            {
                metadata.Title = titleProp.GetString();
            }

            if (first.TryGetProperty("Description", out JsonElement descProp))
            {
                metadata.Description = descProp.GetString();
            }

            if (first.TryGetProperty("Rating", out JsonElement ratingProp))
            {
                if (ratingProp.ValueKind == JsonValueKind.Number)
                {
                    metadata.Rating = ratingProp.GetInt32();
                }
                else if (ratingProp.ValueKind == JsonValueKind.String && int.TryParse(ratingProp.GetString(), out int r))
                {
                    metadata.Rating = r;
                }
            }

            HashSet<string> uniqueTags = new(StringComparer.OrdinalIgnoreCase);

            if (first.TryGetProperty("Subject", out JsonElement subjProp))
            {
                PopulateTags(subjProp, uniqueTags);
            }

            if (first.TryGetProperty("Keywords", out JsonElement kwProp))
            {
                PopulateTags(kwProp, uniqueTags);
            }

            metadata.Tags = [.. uniqueTags];
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception reading metadata for file: {FilePath}", filePath);
            return null;
        }
    }

    public async Task<bool> WriteMetadataAsync(string filePath, MediaMetadata metadata)
    {
        if (!IsEnabled || _exifToolProcess is null || _exifToolProcess.HasExited)
        {
            return false;
        }

        using LogTiming timer = LogTiming.Start(_logger, "ExifTool write metadata for {FilePath}", filePath);

        try
        {
            lock (_lock)
            {
                _stdin!.WriteLine("-m");
                _stdin!.WriteLine("-overwrite_original");
                _stdin!.WriteLine("-Subject=");
                _stdin!.WriteLine("-Keywords=");

                foreach (string tag in metadata.Tags)
                {
                    _stdin!.WriteLine($"-Subject={tag}");
                    _stdin!.WriteLine($"-Keywords={tag}");
                }

                if (metadata.Title is not null)
                {
                    _stdin!.WriteLine($"-Title={metadata.Title}");
                }
                else
                {
                    _stdin!.WriteLine("-Title=");
                }

                if (metadata.Description is not null)
                {
                    _stdin!.WriteLine($"-Description={metadata.Description}");
                }
                else
                {
                    _stdin!.WriteLine("-Description=");
                }

                if (metadata.Rating.HasValue)
                {
                    _stdin!.WriteLine($"-Rating={metadata.Rating.Value}");
                }
                else
                {
                    _stdin!.WriteLine("-Rating=");
                }

                _stdin!.WriteLine(filePath);
                _stdin!.WriteLine("-execute");
                _stdin!.Flush();

                while (true)
                {
                    string? line = _stdout!.ReadLine();
                    if (line is null || line.StartsWith("{ready}", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception writing metadata for file: {FilePath}", filePath);
            return false;
        }
    }

    private static void PopulateTags(JsonElement element, HashSet<string> tags)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    _ = tags.Add(s.Trim());
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            string? s = element.GetString();
            if (!string.IsNullOrWhiteSpace(s))
            {
                _ = tags.Add(s.Trim());
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
        {
            return;
        }

        if (disposing && _exifToolProcess is not null && !_exifToolProcess.HasExited)
        {
            try
            {
                lock (_lock)
                {
                    _stdin?.WriteLine("-stay_open");
                    _stdin?.WriteLine("False");
                    _stdin?.WriteLine("-execute");
                    _stdin?.Flush();
                }
                _exifToolProcess.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error shutting down ExifTool process");
            }
            finally
            {
                _exifToolProcess.Dispose();
            }
        }

        _isDisposed = true;
    }
}
```

---

### `Tagium.Core\Services\FFmpegLocator.cs`

```csharp
namespace Tagium.Core.Services;

internal static class FFmpegLocator
{
    public static string? Find()
    {
        string? ffmpegPath = FindInAppDirectory() ?? FindInPath();

        return ffmpegPath is not null && HasCompanionFfprobe(ffmpegPath)
            ? ffmpegPath : null;
    }

    private static string? FindInAppDirectory()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

        return File.Exists(ffmpeg)
            ? ffmpeg
            : null;
    }

    private static string? FindInPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        string[] paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (string dir in paths)
        {
            string candidate = Path.Combine(dir, "ffmpeg.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasCompanionFfprobe(string ffmpegPath)
    {
        string dir = Path.GetDirectoryName(ffmpegPath)!;
        string ffprobe = Path.Combine(dir, "ffprobe.exe");

        return File.Exists(ffprobe);
    }
}
```

---

### `Tagium.Core\Services\LibraryService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using System.Threading;

namespace Tagium.Core.Services;

public class LibraryService
{
    public bool CanUndo => _undoService.CanUndo;

    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;
    private readonly UndoService _undoService;
    private readonly LibraryRootRepository _rootRepo;
    private readonly LibraryScanService _scanService;
    private readonly ExifToolService _exifTool;

    private bool _tagSyncInProgress;
    private int _tagSyncProcessed;
    private int _tagSyncTotal;
    private CancellationTokenSource? _tagSyncCts;

    public bool IsTagSyncRunning => _tagSyncInProgress;
    public int TagSyncProcessed => _tagSyncProcessed;
    public int TagSyncTotal => _tagSyncTotal;

    public event Action<int, int>? TagSyncProgress;
    public event Action? TagSyncCompleted;
    public event Action? LibraryChanged;

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager,
        UndoService _undoService,
        LibraryRootRepository rootRepo,
        LibraryScanService scanService,
        ExifToolService exifTool)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
        this._undoService = _undoService;
        _rootRepo = rootRepo;
        _scanService = scanService;
        _exifTool = exifTool;

        _scanService.ScanCompleted += () => LibraryChanged?.Invoke();
        _scanService.FilesImported += () => LibraryChanged?.Invoke();
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    public void Undo()
    {
        _undoService.Undo();
    }

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            return [.. _fileRepo.GetFilesWithTags(sort, dir, true)
                .Where(f => f.IsMissing)];
        }
        else
        {
            return string.IsNullOrWhiteSpace(searchText)
                ? _fileRepo.GetFilesWithTags(sort, dir)
                : _searchRepo.Search(searchText, sort, dir);
        }
    }

    public FileEntry? GetFileById(Guid id)
    {
        return _fileRepo.GetById(id);
    }

    public FileEntry? GetFileWithTagsById(Guid id)
    {
        return _fileRepo.GetFileWithTagsById(id);
    }

    public void AddTagToFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool alreadyHadTag = existingIds.Contains(tagId);

        _fileTagRepo.AddTagToFile(fileId, tagId);

        if (!alreadyHadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: true));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
    }

    public void AddTagToFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];

        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (!existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.AddTagToFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: true));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
            }
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(Guid fileId, Guid tagId)
    {
        List<Guid> existingIds = _fileTagRepo.GetTagIdsForFile(fileId);
        bool hadTag = existingIds.Contains(tagId);

        _fileTagRepo.RemoveTagFromFile(fileId, tagId);

        if (hadTag)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                [fileId],
                wasAdding: false));

            _ = SyncMetadataToFileAsync(fileId);
        }

        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFiles(IEnumerable<Guid> fileIds, Guid tagId)
    {
        List<Guid> ids = [.. fileIds];

        if (ids.Count == 0)
        {
            return;
        }

        List<Guid> modifiedFiles = [];
        foreach (Guid id in ids)
        {
            List<Guid> existing = _fileTagRepo.GetTagIdsForFile(id);
            if (existing.Contains(tagId))
            {
                modifiedFiles.Add(id);
            }

            _fileTagRepo.RemoveTagFromFile(id, tagId);
        }

        if (modifiedFiles.Count != 0)
        {
            _undoService.Push(new TagAssignmentUndoCommand(
                _fileTagRepo,
                _exifTool,
                _fileRepo,
                _tagRepo,
                () => LibraryChanged?.Invoke(),
                tagId,
                modifiedFiles,
                wasAdding: false));

            foreach (Guid fileId in modifiedFiles)
            {
                _ = SyncMetadataToFileAsync(fileId);
            }
        }

        LibraryChanged?.Invoke();
    }

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTags(TagSortBy sortBy, SortDirection direction)
    {
        return _tagRepo.GetAll(sortBy, direction);
    }

    public List<Tag> GetTagsForFile(Guid fileId)
    {
        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        return _tagRepo.GetTagsByIds(tagIds);
    }

    public List<Tag> GetCommonTags(IEnumerable<Guid> fileIds)
    {
        List<Guid> ids = fileIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        HashSet<Guid> commonIds = [.. _fileTagRepo.GetTagIdsForFile(ids[0])];

        for (int i = 1; i < ids.Count; i++)
        {
            List<Guid> tags = _fileTagRepo.GetTagIdsForFile(ids[i]);
            commonIds.IntersectWith(tags);

            if (commonIds.Count == 0)
            {
                break;
            }
        }

        return _tagRepo.GetTagsByIds(commonIds);
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        Tag? original = _tagRepo.GetTagsByIds([tag.Id]).FirstOrDefault();
        bool nameChanged = original is not null && !string.Equals(original.Name, tag.Name, StringComparison.OrdinalIgnoreCase);

        _tagRepo.Update(tag);

        if (nameChanged)
        {
            List<Guid> fileIds = _fileTagRepo.GetFileIdsForTag(tag.Id);
            QueueTagMetadataSync(fileIds);
        }

        LibraryChanged?.Invoke();
    }

    private void QueueTagMetadataSync(List<Guid> fileIds)
    {
        _tagSyncCts?.Cancel();
        _tagSyncCts = new CancellationTokenSource();
        CancellationToken token = _tagSyncCts.Token;

        if (fileIds.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _tagSyncInProgress = true;
            _tagSyncProcessed = 0;
            _tagSyncTotal = fileIds.Count;

            TagSyncProgress?.Invoke(_tagSyncProcessed, _tagSyncTotal);

            try
            {
                foreach (Guid fileId in fileIds)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    await SyncMetadataToFileAsync(fileId);

                    _tagSyncProcessed++;
                    TagSyncProgress?.Invoke(_tagSyncProcessed, _tagSyncTotal);
                }
            }
            catch (Exception)
            {
                // Gracefully log/ignore
            }
            finally
            {
                _tagSyncInProgress = false;
                TagSyncCompleted?.Invoke();
            }
        }, token);
    }

    public void DeleteTag(Guid id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }

    public List<LibraryRoot> GetRoots()
    {
        return _rootRepo.GetAll();
    }

    public void AddRoot(string path)
    {
        _rootRepo.Add(path);
        LibraryChanged?.Invoke();
    }

    public void UpdateRoot(LibraryRoot root)
    {
        _rootRepo.Update(root);
        LibraryChanged?.Invoke();
    }

    public void DeleteRoot(Guid id, string path)
    {
        _rootRepo.Delete(id, path);
        LibraryChanged?.Invoke();
    }

    public async Task ScanAsync()
    {
        await _scanService.ScanAllAsync();
    }

    private async Task SyncMetadataToFileAsync(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = tags.Select(t => t.Name).ToList(),
            Rating = file.Rating,
            Title = file.Title,
            Description = file.Description
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }
}
```

---

### `Tagium.Core\Services\RelocateResult.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public record RelocateResult(RelocateResultStatus Status, FileEntry? ConflictingFile);
```

---

### `Tagium.Core\Services\RelocateResultStatus.cs`

```csharp
namespace Tagium.Core.Services;

public enum RelocateResultStatus { Success, Duplicate, Error, NotFound }
```

---

### `Tagium.Core\Services\ScanContext.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ScanContext
{
    public List<string> CandidateFiles { get; } = [];
    public Dictionary<string, FileEntry> DbFilesMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> UntrackedFiles { get; } = [];
    public HashSet<string> VerifiedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasChanges { get; set; }
}
```

---

### `Tagium.Core\Services\Scanning.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class LibraryScanService
{
    private readonly LibraryRootRepository _rootRepo;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly FileHasher _fileHasher;
    private readonly ILogger<LibraryScanService> _logger;

    public bool IsScanning { get; private set; }
    public int ProgressCurrent { get; private set; }
    public int ProgressTotal { get; private set; }

    public event Action<int, int>? ScanProgress;
    public event Action? ScanCompleted;
    public event Action? FilesImported;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public LibraryScanService(
        LibraryRootRepository rootRepo,
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        FileHasher fileHasher,
        ILogger<LibraryScanService> logger)
    {
        _rootRepo = rootRepo;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _fileHasher = fileHasher;
        _logger = logger;
    }

    public async Task ScanAllAsync(CancellationToken cancellationToken = default)
    {
        if (IsScanning)
        {
            _logger.LogWarning("ScanAllAsync requested while scanning is already in progress");
            return;
        }

        IsScanning = true;
        ProgressCurrent = 0;
        ProgressTotal = 0;

        _logger.LogInformation("Starting library scan...");
        await Task.Run(async () => await ExecuteScanPipelineAsync(cancellationToken), cancellationToken);
    }

    private async Task ExecuteScanPipelineAsync(CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Library scan execution pipeline");

        Stopwatch uiTimer = Stopwatch.StartNew();
        ScanContext context = new();

        try
        {
            List<LibraryRoot> roots = _rootRepo.GetAll().Where(r => r.IsEnabled).ToList();
            if (roots.Count == 0)
            {
                _logger.LogInformation("No active library roots configured. Scan aborted");
                return;
            }

            DiscoverCandidateFiles(roots, context);
            LoadDatabaseCache(roots, context);

            ProgressTotal = context.CandidateFiles.Count;
            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);

            _logger.LogInformation("Processing {Count} candidate files...", ProgressTotal);
            await ProcessCandidateFilesAsync(context, uiTimer, cancellationToken);
            await ResolveMissingAndImportNewAsync(context, uiTimer, cancellationToken);

            FinalizeMissingFiles(context);
            UpdateRootsLastScanTime(roots);

            if (context.HasChanges)
            {
                _logger.LogInformation("Library content changes detected. Triggering files imported event");
                FilesImported?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in execute scan pipeline");
        }
        finally
        {
            IsScanning = false;
            ScanProgress?.Invoke(ProgressTotal, ProgressTotal);
            _logger.LogInformation("Library scan completed. Resetting state");
            ScanCompleted?.Invoke();
        }
    }

    private void DiscoverCandidateFiles(List<LibraryRoot> roots, ScanContext context)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Candidate file discovery in active roots");

        foreach (LibraryRoot root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                _logger.LogWarning("Library root directory does not exist: {Path}", root.Path);
                continue;
            }

            try
            {
                using CancellationTokenSource searchCts = new(TimeSpan.FromSeconds(30));
                _logger.LogInformation("Enumerate files in library root: {Path}", root.Path);
                IEnumerable<string> files = Directory.EnumerateFiles(root.Path, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file)));

                context.CandidateFiles.AddRange(files);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerating directory: {Path}", root.Path);
            }
        }
    }

    private void LoadDatabaseCache(List<LibraryRoot> roots, ScanContext context)
    {
        _logger.LogInformation("Loading current file definitions from database");
        foreach (LibraryRoot root in roots)
        {
            List<FileEntry> dbFiles = _fileRepo.GetFilesUnderPath(root.Path);
            foreach (FileEntry dbFile in dbFiles)
            {
                context.DbFilesMap[dbFile.Path] = dbFile;
            }
        }
    }

    private async Task ProcessCandidateFilesAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Verification processing of {Count} candidates", context.CandidateFiles.Count);

        foreach (string filePath in context.CandidateFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("File processing canceled");
                break;
            }

            await EvaluateSingleFileAsync(filePath, context, cancellationToken);

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task EvaluateSingleFileAsync(string filePath, ScanContext context, CancellationToken cancellationToken)
    {
        try
        {
            FileInfo fileInfo = new(filePath);
            long size = fileInfo.Length;
            long modifiedTime = new DateTimeOffset(fileInfo.LastWriteTimeUtc).ToUnixTimeSeconds();

            if (context.DbFilesMap.TryGetValue(filePath, out FileEntry? existing))
            {
                if (existing.Size == size && existing.ModifiedTime == modifiedTime)
                {
                    if (existing.IsMissing)
                    {
                        _logger.LogInformation("File returned back to active library state: {Path}", filePath);
                        existing.IsMissing = false;
                        _fileRepo.Update(existing);
                        context.HasChanges = true;
                    }
                    _ = context.VerifiedPaths.Add(filePath);
                    ProgressCurrent++;
                }
                else
                {
                    _logger.LogInformation("File change detected: {Path}", filePath);
                    await UpdateChangedFileAsync(existing, filePath, size, modifiedTime, context, cancellationToken);
                }
            }
            else
            {
                context.UntrackedFiles.Add(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error examining path: {Path}", filePath);
        }
    }

    private async Task UpdateChangedFileAsync(FileEntry existing, string filePath, long size, long modifiedTime, ScanContext context, CancellationToken cancellationToken)
    {
        string? hash = await _fileHasher.ComputeSha256Async(filePath);
        if (hash is not null)
        {
            existing.Hash = hash;
            existing.Size = size;
            existing.ModifiedTime = modifiedTime;
            existing.IsMissing = false;

            MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(filePath);
            if (metadata is not null)
            {
                existing.Rating = metadata.Rating;
                existing.Title = metadata.Title;
                existing.Description = metadata.Description;
            }

            _fileRepo.Update(existing);
            context.HasChanges = true;
            _ = context.VerifiedPaths.Add(filePath);
        }
        ProgressCurrent++;
    }

    private async Task ResolveMissingAndImportNewAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        List<FileEntry> missingDbFiles = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path))
            .ToList();

        HashSet<string> processedUntracked = [];

        await DetectRenamesAndMovesAsync(missingDbFiles, context, processedUntracked, uiTimer, cancellationToken);
        await ImportRemainingUntrackedAsync(context, processedUntracked, uiTimer, cancellationToken);
    }

    private async Task DetectRenamesAndMovesAsync(List<FileEntry> missingDbFiles, ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Rename and move detection of {Count} missing items", missingDbFiles.Count);

        Dictionary<long, List<string>> untrackedBySize = GroupUntrackedBySize(context.UntrackedFiles);

        foreach (FileEntry missing in missingDbFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (string.IsNullOrEmpty(missing.Hash) || !untrackedBySize.TryGetValue(missing.Size, out List<string>? candidates))
            {
                continue;
            }

            foreach (string candidatePath in candidates)
            {
                if (processedUntracked.Contains(candidatePath))
                {
                    continue;
                }

                try
                {
                    FileInfo info = new(candidatePath);
                    long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                    if (missing.ModifiedTime == modifiedTime)
                    {
                        string? candidateHash = await _fileHasher.ComputeSha256Async(candidatePath);
                        if (candidateHash == missing.Hash)
                        {
                            _logger.LogInformation("Detected file rename/relocation. Original: {Old}, New: {New}", missing.Path, candidatePath);
                            missing.Path = candidatePath;
                            missing.Name = Path.GetFileName(candidatePath);
                            missing.IsMissing = false;

                            _fileRepo.Update(missing);
                            context.HasChanges = true;

                            _ = processedUntracked.Add(candidatePath);
                            _ = context.VerifiedPaths.Add(candidatePath);
                            ProgressCurrent++;
                            break;
                        }
                    }
                }
                catch { }
            }

            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private Dictionary<long, List<string>> GroupUntrackedBySize(List<string> untrackedFiles)
    {
        Dictionary<long, List<string>> map = [];
        foreach (string path in untrackedFiles)
        {
            try
            {
                long size = new FileInfo(path).Length;
                if (!map.TryGetValue(size, out List<string>? list))
                {
                    list = [];
                    map[size] = list;
                }
                list.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect file size for grouping: {Path}", path);
            }
        }
        return map;
    }

    private async Task ImportRemainingUntrackedAsync(ScanContext context, HashSet<string> processedUntracked, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        using LogTiming timer = LogTiming.Start(_logger, "Importing {Count} remaining untracked items", context.UntrackedFiles.Count - processedUntracked.Count);

        foreach (string path in context.UntrackedFiles)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (processedUntracked.Contains(path))
            {
                continue;
            }

            try
            {
                FileInfo info = new(path);
                long size = info.Length;
                long modifiedTime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

                string? hash = await _fileHasher.ComputeSha256Async(path);
                if (hash is not null)
                {
                    _logger.LogInformation("Importing untracked file: {Path}", path);
                    _ = _fileRepo.Add(path, hash, size, modifiedTime);
                    context.HasChanges = true;

                    FileEntry? newEntry = _fileRepo.GetByHash(hash);
                    if (newEntry is not null)
                    {
                        await ImportFileMetadataAsync(path, newEntry);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing untracked file {Path}", path);
            }

            ProgressCurrent++;
            await YieldAndNotifyAsync(context, uiTimer, cancellationToken);
        }
    }

    private async Task ImportFileMetadataAsync(string path, FileEntry newEntry)
    {
        using LogTiming timer = LogTiming.StartDebug(_logger, "Metadata and tag mapping extraction for {Path}", path);

        MediaMetadata? metadata = await _exifTool.ReadMetadataAsync(path);
        if (metadata is null)
        {
            return;
        }

        _logger.LogInformation("Extracting and writing embedded ExifTool metadata for imported file: {Path}", path);
        newEntry.Rating = metadata.Rating;
        newEntry.Title = metadata.Title;
        newEntry.Description = metadata.Description;
        _fileRepo.Update(newEntry);

        if (metadata.Tags.Count == 0)
        {
            return;
        }

        foreach (string rawTag in metadata.Tags)
        {
            string norm = TagFormatter.Normalize(rawTag);
            if (string.IsNullOrWhiteSpace(norm))
            {
                continue;
            }

            Guid? resolvedId = _tagRepo.ResolveTagId(norm);
            if (resolvedId.HasValue)
            {
                _fileTagRepo.AddTagToFile(newEntry.Id, resolvedId.Value);
            }
            else
            {
                _tagRepo.Add(norm, "#757575", null);
                Guid? refreshedId = _tagRepo.ResolveTagId(norm);
                if (refreshedId.HasValue)
                {
                    _fileTagRepo.AddTagToFile(newEntry.Id, refreshedId.Value);
                }
            }
        }
    }

    private void FinalizeMissingFiles(ScanContext context)
    {
        List<Guid> finalMissingIds = context.DbFilesMap.Values
            .Where(e => !context.VerifiedPaths.Contains(e.Path) && !e.IsMissing)
            .Select(e => e.Id)
            .ToList();

        if (finalMissingIds.Count > 0)
        {
            _logger.LogInformation("Setting missing status for {Count} cataloged files", finalMissingIds.Count);
            _fileRepo.SetMissingStatus(finalMissingIds, true);
            context.HasChanges = true;
        }
    }

    private void UpdateRootsLastScanTime(List<LibraryRoot> roots)
    {
        _logger.LogInformation("Updating scan timing markers on active roots");
        foreach (LibraryRoot root in roots)
        {
            root.LastScanTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _rootRepo.Update(root);
        }
    }

    private async Task YieldAndNotifyAsync(ScanContext context, Stopwatch uiTimer, CancellationToken cancellationToken)
    {
        if (uiTimer.ElapsedMilliseconds > 250)
        {
            if (context.HasChanges)
            {
                FilesImported?.Invoke();
                context.HasChanges = false;
            }

            ScanProgress?.Invoke(ProgressCurrent, ProgressTotal);
            uiTimer.Restart();

            await Task.Delay(50, cancellationToken);
        }
    }
}
```

---

### `Tagium.Core\Services\ThumbnailCache.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Tagium.Core.Helpers;

namespace Tagium.Core.Services;

public class ThumbnailCache
{
    private readonly ConcurrentDictionary<string, bool> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ThumbnailCache> _logger;

    public string CacheDir { get; }

    public ThumbnailCache(ILogger<ThumbnailCache> logger)
    {
        _logger = logger;
        CacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Automatically purge old, bloated PNG thumbnails to migrate to high-performance JPEGs
        if (Directory.Exists(CacheDir))
        {
            try
            {
                foreach (string pngFile in Directory.EnumerateFiles(CacheDir, "*.png"))
                {
                    File.Delete(pngFile);
                }
                _logger.LogInformation("Purged legacy PNG thumbnails from cache directory");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error purging old PNG thumbnails from cache");
            }
        }

        // Populate the cache with high-performance JPEG thumbnail files from disk
        if (Directory.Exists(CacheDir))
        {
            try
            {
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(CacheDir, "*.jpg"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    _thumbnailCache.TryAdd(name, true);
                    count++;
                }
                _logger.LogInformation("Successfully indexed {Count} JPEG thumbnails in cache", count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning and populating thumbnail cache from disk");
            }
        }
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return Path.Combine(CacheDir, $"{key}.jpg");
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        string key = !string.IsNullOrEmpty(hash) ? hash : fileId.ToString("N");
        return _thumbnailCache.ContainsKey(key);
    }

    public void AddToCache(string key)
    {
        _thumbnailCache.TryAdd(key, true);
    }
}
```

---

### `Tagium.Core\Services\ThumbnailGenerator.cs`

```csharp
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Tagium.Core.Services;

public class ThumbnailGenerator
{
    private const int JpegQuality = 75;
    private readonly ILogger<ThumbnailGenerator> _logger;

    private static readonly SKColor PlaceholderBackgroundColor = new(30, 30, 30);
    public static readonly SKColor ErrorColor = SKColors.DarkRed;
    public static readonly SKColor VideoColor = SKColors.DarkSlateBlue;
    public static readonly SKColor GenericColor = SKColors.Gray;

    public ThumbnailGenerator(ILogger<ThumbnailGenerator> logger)
    {
        _logger = logger;
    }

    public async Task<bool> GenerateImageThumbnailAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                using SKCodec? codec = SKCodec.Create(sourcePath);
                if (codec is null)
                {
                    using SKBitmap? fallbackBitmap = SKBitmap.Decode(sourcePath);
                    if (fallbackBitmap is null)
                    {
                        _logger.LogWarning("Failed to decode image using fallback decoder for path: {Path}", sourcePath);
                        return false;
                    }
                    SaveResized(fallbackBitmap, destPath, size);
                    return true;
                }

                float scale = (float)size / Math.Max(codec.Info.Width, codec.Info.Height);
                if (scale < 1.0f)
                {
                    SKSizeI scaledSize = codec.GetScaledDimensions(scale);
                    SKImageInfo nearestInfo = new(
                        scaledSize.Width,
                        scaledSize.Height,
                        codec.Info.ColorType,
                        codec.Info.AlphaType);

                    using SKBitmap? original = SKBitmap.Decode(codec, nearestInfo);
                    if (original is null)
                    {
                        _logger.LogWarning("Failed to decode image with sub-sampling for path: {Path}", sourcePath);
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
                else
                {
                    using SKBitmap? original = SKBitmap.Decode(sourcePath);
                    if (original is null)
                    {
                        _logger.LogWarning("Failed to decode full image for path: {Path}", sourcePath);
                        return false;
                    }

                    SaveResized(original, destPath, size);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sub-sampled decode failed for: {Path}", sourcePath);
                return false;
            }
        }, cancellationToken);
    }

    public void GeneratePlaceholder(string destPath, int size, string text, SKColor backgroundColor)
    {
        try
        {
            using SKBitmap bitmap = new(size, size);
            using SKCanvas canvas = new(bitmap);

            canvas.Clear(backgroundColor);

            using SKPaint textPaint = new()
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            float fontSize = size / 4.0f;
            using SKFont font = new()
            {
                Size = fontSize
            };

            float x = size / 2.0f;
            float y = (size / 2.0f) + (fontSize / 2.0f) - 5;

            canvas.DrawText(text, x, y, SKTextAlign.Center, font, textPaint);
            EncodeAndSave(bitmap, destPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating placeholder thumbnail for path: {Path}", destPath);
        }
    }

    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        using SKBitmap square = new(size, size);
        using SKCanvas canvas = new(square);

        canvas.Clear(PlaceholderBackgroundColor);

        double ratio = Math.Min((double)size / original.Width, (double)size / original.Height);
        int drawWidth = (int)(original.Width * ratio);
        int drawHeight = (int)(original.Height * ratio);
        int x = (size - drawWidth) / 2;
        int y = (size - drawHeight) / 2;

        SKImageInfo info = new(drawWidth, drawHeight);
        using SKBitmap? resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Nearest));

        if (resized is null)
        {
            return;
        }

        canvas.DrawBitmap(resized, x, y);
        EncodeAndSave(square, destPath);
    }

    private static void EncodeAndSave(SKBitmap bitmap, string destPath)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }
}
```

---

### `Tagium.Core\Services\ThumbnailService.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public sealed class ThumbnailService
{
    private readonly ThumbnailCache _cache;
    private readonly ThumbnailGenerator _generator;
    private readonly VideoFrameExtractor _extractor;
    private readonly ILogger<ThumbnailService> _logger;

    private readonly ConcurrentDictionary<string, bool> _activeGenerations = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _generationSemaphore;

    private int _queueTotal = 0;
    private int _queueProcessed = 0;
    private readonly object _queueLock = new();

    private CancellationTokenSource? _bgQueueCts;
    private bool _isBgQueueRunning = false;

    private const int DefaultThumbnailSize = 300;

    public event Action<Guid>? ThumbnailGenerated;
    public event Action<int, int>? QueueProgressChanged;
    public event Action? QueueCompleted;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm"
    };

    public ThumbnailService(
        ThumbnailCache cache,
        ThumbnailGenerator generator,
        VideoFrameExtractor extractor,
        ILogger<ThumbnailService> logger)
    {
        _cache = cache;
        _generator = generator;
        _extractor = extractor;
        _logger = logger;

        int concurrencyLimit = Math.Max(2, Environment.ProcessorCount / 2);
        _generationSemaphore = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
        _logger.LogInformation("Thumbnail service initialized with concurrency limit: {Limit}", concurrencyLimit);
    }

    public string GetThumbnailPath(string? hash, Guid fileId)
    {
        return _cache.GetThumbnailPath(hash, fileId);
    }

    public bool HasThumbnail(Guid fileId, string? hash)
    {
        return _cache.HasThumbnail(fileId, hash);
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = DefaultThumbnailSize, CancellationToken cancellationToken = default)
    {
        string key = !string.IsNullOrEmpty(file.Hash) ? file.Hash : file.Id.ToString("N");
        string destPath = GetThumbnailPath(file.Hash, file.Id);

        if (_cache.HasThumbnail(file.Id, file.Hash) || !File.Exists(file.Path))
        {
            return;
        }

        if (!_activeGenerations.TryAdd(key, true))
        {
            return;
        }

        await _generationSemaphore.WaitAsync(cancellationToken);

        try
        {
            using LogTiming timer = LogTiming.StartDebug(_logger, "Thumbnail generation for {Path}", file.Path);

            if (!File.Exists(destPath))
            {
                await TryGenerateThumbnailInternalAsync(file.Path, destPath, size, cancellationToken);
            }
            _cache.AddToCache(key);
            ThumbnailGenerated?.Invoke(file.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate thumbnail for path: {Path}", file.Path);
            _generator.GeneratePlaceholder(destPath, size, "ERR", ThumbnailGenerator.ErrorColor);
            _cache.AddToCache(key);
        }
        finally
        {
            _generationSemaphore.Release();
            _activeGenerations.TryRemove(key, out _);
        }
    }

    private async Task TryGenerateThumbnailInternalAsync(string sourcePath, string destPath, int size, CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        if (ImageExtensions.Contains(extension))
        {
            _ = await _generator.GenerateImageThumbnailAsync(sourcePath, destPath, size, cancellationToken);
        }
        else if (VideoExtensions.Contains(extension))
        {
            if (!_extractor.HasFFmpeg)
            {
                _generator.GeneratePlaceholder(destPath, size, "VIDEO", ThumbnailGenerator.VideoColor);
                return;
            }

            string? tempFramePath = await _extractor.ExtractFrameAsync(sourcePath);
            if (tempFramePath is not null)
            {
                try
                {
                    _ = await _generator.GenerateImageThumbnailAsync(tempFramePath, destPath, size, cancellationToken);
                }
                finally
                {
                    _extractor.CleanupTempFile(tempFramePath);
                }
            }
            else
            {
                _generator.GeneratePlaceholder(destPath, size, "VIDEO", ThumbnailGenerator.VideoColor);
            }
        }
        else
        {
            string label = extension.TrimStart('.').ToUpperInvariant();
            _generator.GeneratePlaceholder(destPath, size, label, ThumbnailGenerator.GenericColor);
        }
    }

    public void QueueBackgroundGeneration(IEnumerable<FileEntry> files)
    {
        _bgQueueCts?.Cancel();
        _bgQueueCts = new CancellationTokenSource();
        CancellationToken token = _bgQueueCts.Token;

        List<FileEntry> fileList = files.ToList();
        if (fileList.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            _isBgQueueRunning = true;
            _logger.LogInformation("Starting background thumbnail pre-generation queue");
            try
            {
                List<FileEntry> missingFiles = fileList.Where(f => !HasThumbnail(f.Id, f.Hash)).ToList();
                int totalMissing = missingFiles.Count;

                if (totalMissing == 0)
                {
                    lock (_queueLock)
                    {
                        _queueProcessed = 0;
                        _queueTotal = 0;
                    }
                    _logger.LogInformation("All files already have cached thumbnails. Skipping queue background job");
                    QueueCompleted?.Invoke();
                    return;
                }

                lock (_queueLock)
                {
                    _queueProcessed = 0;
                    _queueTotal = totalMissing;
                    QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);
                }

                using LogTiming queueTimer = LogTiming.Start(_logger, "Background thumbnail pre-generation queue run of {Count} items", totalMissing);

                foreach (FileEntry file in missingFiles)
                {
                    if (token.IsCancellationRequested)
                    {
                        _logger.LogInformation("Thumbnail background queue generation was canceled");
                        break;
                    }

                    await GenerateThumbnailAsync(file, DefaultThumbnailSize, token);

                    lock (_queueLock)
                    {
                        _queueProcessed++;
                        QueueProgressChanged?.Invoke(_queueProcessed, _queueTotal);
                    }
                }

                lock (_queueLock)
                {
                    _queueProcessed = 0;
                    _queueTotal = 0;
                }
                _logger.LogInformation("Thumbnail background queue processing completed");
                QueueCompleted?.Invoke();
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Thumbnail queue pre-generation task cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in background thumbnail generation queue");
            }
            finally
            {
                _isBgQueueRunning = false;
            }
        }, token);
    }

    public void CancelBackgroundGeneration()
    {
        _logger.LogInformation("Cancelling background thumbnail pre-generation task");
        _bgQueueCts?.Cancel();
    }
}
```

---

### `Tagium.Core\Services\VideoFrameExtractor.cs`

```csharp
using FFMpegCore;
using Microsoft.Extensions.Logging;

namespace Tagium.Core.Services;

public class VideoFrameExtractor
{
    private const int VideoEarlyCaptureSeconds = 5;
    private const int VideoDurationThresholdSeconds = 10;
    private const double VideoLateCaptureRatio = 0.1;
    private readonly ILogger<VideoFrameExtractor> _logger;

    public bool HasFFmpeg { get; }

    public VideoFrameExtractor(ILogger<VideoFrameExtractor> logger)
    {
        _logger = logger;
        string? ffmpegPath = FFmpegLocator.Find();
        HasFFmpeg = ffmpegPath is not null;

        if (HasFFmpeg)
        {
            string binaryFolder = Path.GetDirectoryName(ffmpegPath!)!;
            _logger.LogInformation("FFmpeg companion binary folder discovered at: {Path}", binaryFolder);
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = binaryFolder });
        }
        else
        {
            _logger.LogWarning("ffmpeg.exe or ffprobe.exe not found in app paths. Video snapshot extraction is disabled");
        }
    }

    public async Task<string?> ExtractFrameAsync(string sourcePath)
    {
        try
        {
            _logger.LogDebug("Analyzing duration for video frame extraction: {Path}", sourcePath);
            TimeSpan? duration = await AnalyzeDurationAsync(sourcePath);
            if (duration is null)
            {
                _logger.LogWarning("Unable to read video length duration for: {Path}", sourcePath);
                return null;
            }

            TimeSpan captureTime = CalculateCaptureTime(duration.Value);
            _logger.LogInformation("Extracting snapshot frame for video {Path} at position {Position}", sourcePath, captureTime);
            return await ExtractSnapshotAsync(sourcePath, captureTime);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Video processing error during frame extraction for file: {Path}", sourcePath);
            return null;
        }
    }

    private async Task<TimeSpan?> AnalyzeDurationAsync(string sourcePath)
    {
        try
        {
            IMediaAnalysis analysis = await FFProbe.AnalyseAsync(sourcePath);
            return analysis.Duration;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFProbe duration analysis failed for file: {Path}", sourcePath);
            return null;
        }
    }

    private static TimeSpan CalculateCaptureTime(TimeSpan duration)
    {
        bool isLongVideo = duration.TotalSeconds > VideoDurationThresholdSeconds;

        double seconds = isLongVideo
            ? VideoEarlyCaptureSeconds
            : duration.TotalSeconds * VideoLateCaptureRatio;

        return TimeSpan.FromSeconds(seconds);
    }

    private async Task<string?> ExtractSnapshotAsync(string sourcePath, TimeSpan captureTime)
    {
        string tempPath = Path.GetTempFileName() + ".jpg";

        try
        {
            await FFMpeg.SnapshotAsync(sourcePath, tempPath, null, captureTime);
            return File.Exists(tempPath) ? tempPath : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFMpeg snapshot operation failed for: {Path}", sourcePath);
            CleanupTempFile(tempPath);
            return null;
        }
    }

    public void CleanupTempFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        const int maxRetries = 3;
        const int delayMs = 100;

        for (int i = 1; i <= maxRetries; i++)
        {
            try
            {
                File.Delete(path);
                _logger.LogDebug("Successfully cleaned up temporary video thumbnail file: {Path}", path);
                return;
            }
            catch (IOException ex) when (i < maxRetries)
            {
                _logger.LogDebug("Antivirus/OS file lock detected on {Path}. Retrying cleanup (Attempt {Current}/{Max})... Reason: {Reason}", path, i, maxRetries, ex.Message);
                Thread.Sleep(delayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup temporary video thumbnail file {Path}", path);
                break;
            }
        }
    }
}
```

---

### `Tagium.Core\Settings\EmojiSet.cs`

```csharp
namespace Tagium.Core.Settings;

public enum EmojiSet
{
    SystemDefault,
    Apple
}
```

---

### `Tagium.Core\Settings\FileViewMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum FileViewMode
{
    Grid,
    List
}
```

---

### `Tagium.Core\Settings\Settings.cs`

```csharp
namespace Tagium.Core.Settings;

public class Settings
{
    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public int ThumbnailSize { get; set; } = 120;
    public SortBy FileSortBy { get; set; } = SortBy.DateAdded;
    public SortDirection FileSortDirection { get; set; } = SortDirection.Desc;
    public TagSidebarDisplayMode TagSidebarStyle { get; set; } = TagSidebarDisplayMode.Pill;
    public TagSortBy TagSortBy { get; set; } = TagSortBy.Name;
    public SortDirection TagSortDirection { get; set; } = SortDirection.Asc;
    public FileViewMode FileViewMode { get; set; } = FileViewMode.Grid;
    public string SelectedTheme { get; set; } = "Default";
    public int LeftSidebarWidth { get; set; } = 250;
    public int RightSidebarWidth { get; set; } = 320;
    public int Zoom { get; set; } = 100;
}
```

---

### `Tagium.Core\Settings\SettingsManager.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Tagium.Core.Helpers;

namespace Tagium.Core.Settings;

public class SettingsManager
{
    public Settings Current { get; }

    private readonly string _settingsPath;

    public event Action? SettingsChanged;

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public SettingsManager()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        _settingsPath = Path.Combine(baseDir, "settings.json");
        Current = Load();
    }


    public void Update(Action<Settings> updateAction, bool save = true)
    {
        updateAction(Current);

        if (save)
        {
            Save();
        }

        SettingsChanged?.Invoke();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, _serializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error saving: {ex.Message}");
        }
    }

    private Settings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new Settings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<Settings>(json, _serializerOptions)
                ?? new Settings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error loading: {ex.Message}. Using defaults.");
            return new Settings();
        }
    }
}
```

---

### `Tagium.Core\Settings\SortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortBy
{
    Path,
    Name,
    DateAdded
}
```

---

### `Tagium.Core\Settings\SortDirection.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortDirection
{
    Asc,
    Desc
}
```

---

### `Tagium.Core\Settings\TagDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagDisplayMode
{
    ColorCircle,
    Emoji
}
```

---

### `Tagium.Core\Settings\TagSidebarDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagSidebarDisplayMode
{
    Pill,
    MinimalCircle
}
```

---

### `Tagium.Core\Settings\TagSortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagSortBy
{
    Name,
    FileCount
}
```

---

### `Tagium.Core\Services\DataExport\DatabaseExport.cs`

```csharp
using Tagium.Core.Models;

namespace Tagium.Core.Services.DataExport;

public class DatabaseExport
{
    public List<Tag> Tags { get; set; } = [];
    public List<FileExportData> Files { get; set; } = [];
}
```

---

### `Tagium.Core\Services\DataExport\DataExportService.cs`

```csharp
using System.Text;
using System.Text.Json;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.DataExport;

public class DataExportService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;

    public DataExportService(FileRepository fileRepo, TagRepository tagRepo, FileTagRepository fileTagRepo)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
    }

    public async Task ExportJsonAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
    }

    public async Task ExportCsvAsync(string path)
    {
        DatabaseExport data = BuildExportData();
        Dictionary<Guid, string> tagLookup = data.Tags.ToDictionary(t => t.Id, t => t.Name);
        StringBuilder sb = new();

        _ = sb.AppendLine("Id,Path,Name,Hash,DateAdded,IsMissing,Size,ModifiedTime,Rating,Title,Description,Tags");

        foreach (FileExportData file in data.Files)
        {
            string tags = string.Join(";", file.TagIds.Select(id => tagLookup.GetValueOrDefault(id, "unknown")));
            string line = $"{file.Id},\"{Escape(file.Path)}\",\"{Escape(file.Name)}\",{file.Hash},{file.DateAdded},{file.IsMissing},{file.Size},{file.ModifiedTime},{file.Rating},\"{Escape(file.Title ?? string.Empty)}\",\"{Escape(file.Description ?? string.Empty)}\",\"{Escape(tags)}\"";
            _ = sb.AppendLine(line);
        }

        await File.WriteAllTextAsync(path, sb.ToString());
    }

    private DatabaseExport BuildExportData()
    {
        List<FileEntry> files = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        List<Tag> tags = _tagRepo.GetAll();

        DatabaseExport export = new() { Tags = tags };

        foreach (FileEntry f in files)
        {
            List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(f.Id);

            export.Files.Add(new FileExportData
            {
                Id = f.Id,
                Path = f.Path,
                Name = f.Name,
                Hash = f.Hash,
                DateAdded = f.DateAdded,
                IsMissing = f.IsMissing,
                Size = f.Size,
                ModifiedTime = f.ModifiedTime,
                Rating = f.Rating,
                Title = f.Title,
                Description = f.Description,
                TagIds = tagIds
            });
        }
        return export;
    }

    private static string Escape(string s)
    {
        return s.Replace("\"", "\"\"");
    }
}
```

---

### `Tagium.Core\Services\DataExport\FileExportData.cs`

```csharp
namespace Tagium.Core.Services.DataExport;

public class FileExportData
{
    public Guid Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public long Size { get; set; }
    public long ModifiedTime { get; set; }
    public List<Guid> TagIds { get; set; } = [];
    public int? Rating { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
}
```

---

### `Tagium.Core\Services\DataMerge\DataMergeService.cs`

```csharp
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
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing, Size, ModifiedTime, Rating, Title, Description FROM Files";

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
                ModifiedTime = r.GetInt64(7),
                Rating = r.IsDBNull(8) ? null : r.GetInt32(8),
                Title = r.IsDBNull(9) ? null : r.GetString(9),
                Description = r.IsDBNull(10) ? null : r.GetString(10)
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
```

---

### `Tagium.Core\Services\DataMerge\MergeResult.cs`

```csharp
namespace Tagium.Core.Services.DataMerge;

public class MergeResult()
{
    public int NewFiles { get; set; }
    public int NewTags { get; set; }
    public int NewAssociations { get; set; }
}
```

---

### `Tagium.Core\Services\Import\ImportProgress.cs`

```csharp
namespace Tagium.Core.Services.Import;

public sealed record ImportProgress(int Processed, int Total);
```

---

### `Tagium.Core\Services\Import\ImportSummary.cs`

```csharp
namespace Tagium.Core.Services.Import;

public sealed record ImportSummary(int Added, int Duplicates, int Errors);
```

---

### `Tagium.Core\Services\LibraryIntegrity\IntegrityScanResult.cs`

```csharp
namespace Tagium.Core.Services.LibraryIntegrity;

public class IntegrityScanResult
{
    public int FilesScanned { get; set; }
    public int NewlyMissing { get; set; }
    public int NewlyFound { get; set; }
}
```

---

### `Tagium.Core\Services\LibraryIntegrity\LibraryIntegrityService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services.LibraryIntegrity;

public class LibraryIntegrityService
{
    private readonly FileRepository _fileRepo;

    public LibraryIntegrityService(FileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public async Task<IntegrityScanResult> ScanAsync()
    {
        List<FileEntry> allFiles = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        IntegrityScanResult result = new() { FilesScanned = allFiles.Count };

        List<Guid> toMarkMissing = [];
        List<Guid> toMarkFound = [];

        await Task.Run(() =>
        {
            foreach (FileEntry file in allFiles)
            {
                bool exists = File.Exists(file.Path);

                if (!exists && !file.IsMissing)
                {
                    toMarkMissing.Add(file.Id);
                }
                else if (exists && file.IsMissing)
                {
                    toMarkFound.Add(file.Id);
                }
            }
        });

        if (toMarkMissing.Count != 0)
        {
            _fileRepo.SetMissingStatus(toMarkMissing, true);
            result.NewlyMissing = toMarkMissing.Count;
        }

        if (toMarkFound.Count != 0)
        {
            _fileRepo.SetMissingStatus(toMarkFound, false);
            result.NewlyFound = toMarkFound.Count;
        }

        return result;
    }

    public void PurgeMissing()
    {
        _fileRepo.RemoveMissingFiles();
    }
}
```

---

### `Tagium.Core\Services\Undo\FileDeletionUndoCommand.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Undo;

public class FileDeletionUndoCommand : IUndoCommand
{
    private readonly FileRepository _fileRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly Action _onChanged;
    private readonly List<(FileEntry file, List<Guid> tagIds)> _deletedItems;

    public FileDeletionUndoCommand(
        FileRepository fileRepo,
        FileTagRepository fileTagRepo,
        Action onChanged,
        List<(FileEntry file, List<Guid> tagIds)> deletedItems)
    {
        _fileRepo = fileRepo;
        _fileTagRepo = fileTagRepo;
        _onChanged = onChanged;
        _deletedItems = deletedItems;
    }

    public void Undo()
    {
        foreach (var (file, tagIds) in _deletedItems)
        {
            _fileRepo.Restore(file);

            foreach (var tagId in tagIds)
            {
                _fileTagRepo.AddTagToFile(file.Id, tagId);
            }
        }

        _onChanged();
    }
}
```

---

### `Tagium.Core\Services\Undo\IUndoCommand.cs`

```csharp
namespace Tagium.Core.Services.Undo;

public interface IUndoCommand
{
    void Undo();
}
```

---

### `Tagium.Core\Services\Undo\TagAssignmentUndoCommand.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Core.Services.Undo;

public class TagAssignmentUndoCommand : IUndoCommand
{
    private readonly FileTagRepository _fileTagRepo;
    private readonly ExifToolService _exifTool;
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly Action _onChanged;
    private readonly Guid _tagId;
    private readonly List<Guid> _fileIds;
    private readonly bool _wasAdding;

    public TagAssignmentUndoCommand(
        FileTagRepository fileTagRepo,
        ExifToolService exifTool,
        FileRepository fileRepo,
        TagRepository tagRepo,
        Action onChanged,
        Guid tagId,
        IEnumerable<Guid> fileIds,
        bool wasAdding)
    {
        _fileTagRepo = fileTagRepo;
        _exifTool = exifTool;
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _onChanged = onChanged;
        _tagId = tagId;
        _fileIds = fileIds.ToList();
        _wasAdding = wasAdding;
    }

    public void Undo()
    {
        foreach (Guid fileId in _fileIds)
        {
            if (_wasAdding)
            {
                _fileTagRepo.RemoveTagFromFile(fileId, _tagId);
            }
            else
            {
                _fileTagRepo.AddTagToFile(fileId, _tagId);
            }

            _ = SyncMetadataToFileAsync(fileId);
        }

        _onChanged();
    }

    private async Task SyncMetadataToFileAsync(Guid fileId)
    {
        FileEntry? file = _fileRepo.GetById(fileId);
        if (file is null)
        {
            return;
        }

        List<Guid> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> tags = _tagRepo.GetTagsByIds(tagIds);

        MediaMetadata metadata = new()
        {
            Tags = tags.Select(t => t.Name).ToList(),
            Rating = file.Rating,
            Title = file.Title,
            Description = file.Description
        };

        _ = await Task.Run(() => _exifTool.WriteMetadataAsync(file.Path, metadata));
    }
}
```

---

### `Tagium.Core\Services\Undo\UndoService.cs`

```csharp
namespace Tagium.Core.Services.Undo;

public class UndoService
{
    private readonly Stack<IUndoCommand> _history = new();
    private const int MaxHistory = 50;
    private bool _isExecutingUndo;

    public bool CanUndo => _history.Count > 0 && !_isExecutingUndo;

    public void Push(IUndoCommand command)
    {
        if (_isExecutingUndo)
        {
            return;
        }

        if (_history.Count >= MaxHistory)
        {
            // Remove oldest to prevent unbounded growth
            var temp = _history.ToArray();
            _history.Clear();
            for (int i = temp.Length - 2; i >= 0; i--)
            {
                _history.Push(temp[i]);
            }
        }

        _history.Push(command);
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        _isExecutingUndo = true;
        try
        {
            IUndoCommand command = _history.Pop();
            command.Undo();
        }
        finally
        {
            _isExecutingUndo = false;
        }
    }

    public void Clear()
    {
        _history.Clear();
    }
}
```

---

### `Tagium.Photino.Blazor\design.md`

```markdown
# Tagium UI/UX Design System Guidelines

This document serves as the absolute contract for all visual developments in Tagium. All styles must strictly adhere to the tokens defined here.

---

## 1. Absolute Guardrails & Anti-Patterns

* **NO Raw Hex Codes**: Never write raw colors (e.g., `#1a1a1a` or `#333`) in any component or stylesheet. Always map colors to semantic CSS custom properties.
* **NO Inline Styles**: Do not use `style="..."` attribute assignments for padding, margins, colors, or sizes. Keep CSS isolated inside companion `.razor.css` files.
* **Grid Alignment**: Layouts, padding, margins, and component gaps must strictly use multipliers of the 8px grid (using spacing custom properties).
* **Minimalist Feedback**: Keep hover states subtle and rounded. Ensure borders are thin (`1px`) and highly contrasting.

---

## 2. Global Semantic Tokens

All CSS variables are declared globally in `app.css`. Use them according to these functional definitions:

### Colors

| CSS Variable | Semantic Purpose / Value Mapping |
| :--- | :--- |
| `var(--bg-color)` | Core app background (dark charcoal) |
| `var(--panel-bg)` | Sidebar and secondary panel background |
| `var(--surface-bg)` | Cards, input fields, and elevated elements |
| `var(--surface-hover)` | Interactive hover state (semi-transparent white) |
| `var(--border-color)` | Structural separation lines, thin borders |
| `var(--text-color)` | Primary readable content |
| `var(--text-muted)` | Supporting descriptors, labels, and timestamps |
| `var(--primary-color)` | Focus rings, primary buttons, accents |
| `var(--primary-hover)` | Active primary button hover states |
| `var(--text-on-primary)` | Contrasting text color sitting on primary-colored backgrounds (WCAG AA) |
| `var(--danger-color)` | Destructive buttons, warnings, missing alerts |
| `var(--danger-hover)` | Active destructive hover states |
| `var(--folder-color)` | Directories, roots, and folder paths (`#ffca28`) |
| `var(--warning-color)` | High-visibility alert orange for missing file flags (`#ff9800`) |
| `var(--appearance-color)` | Theme preferences and creative palettes (`#ba68c8`) |

### Spacing & Grid System

| CSS Variable | Value | Purpose |
| :--- | :--- | :--- |
| `var(--space-2xs)` | 4px | Micro-paddings, element borders |
| `var(--space-xs)` | 8px | Grid gap, label-to-input gap, list item vertical padding |
| `var(--space-sm)` | 12px | Standard component padding, sub-panels |
| `var(--space-md)` | 16px | Workspace borders, grid container margins |
| `var(--space-lg)` | 24px | Layout spacing, empty state paddings |

### Border Radii & Transitions

| CSS Variable | Value | Purpose / Standard Application |
| :--- | :--- | :--- |
| `var(--radius-sm)` | 4px | Small buttons, badges, tag pills |
| `var(--radius-md)` | 6px | Input fields, file cards, small containers |
| `var(--radius-lg)` | 8px | Modals, preview windows, dialog frames |
| `var(--transition-fast)` | 0.1s ease-in-out | Hovers, background transitions, selection changes |
| `var(--transition-normal)` | 0.15s ease-in-out | Drawers, modal overlays, progress bars |

---

## 3. Interactive Component State Rules

Configure UI elements and icons dynamically using these targeted states:

* **Default Icons**: Render inactive system controls using `var(--text-muted)`.
* **System Folders**: Always render folder icons with `color: var(--folder-color)`.
* **Alarms & Warnings**: Render critical states and missing files with `color: var(--warning-color)`.
* **Selection Override**: When a container gains the active selection state, all inner text and colored icons must immediately transition to `color: var(--text-on-primary) !important`.
* **Interactive Deletions**: Tag-removal/delete controls must rest at `var(--text-muted)` and transition to `var(--danger-color)` on hover.

### Settings Panel Tab Accents

Map category tab icons to these domains to optimize visual navigation:
* **Library Folders Tab**: Icon color `var(--folder-color)`
* **Appearance Tab**: Icon color `var(--appearance-color)`
* **Maintenance Tab**: Icon color `var(--primary-color)`
* **Active State**: Selected tab icon transition to `color: var(--primary-color)`.

---

## 4. Component Sizing Specs (Compact Desktop)

Ensure all inputs, selectors, and controls conform to this compact desktop horizontal and vertical layout formula:

### Standard Utility Buttons, Dropdowns & Inputs
* **Height**: Lock strictly between **24px and 26px**.
* **Padding**: Vertical: `var(--space-2xs)` (4px); Horizontal: `var(--space-xs)` (8px) or `var(--space-sm)` (12px).
* **Line Height**: Lock to `line-height: 1.2` or `1.3` to prevent vertical skewing.
* **Dropdowns**: Select fields must match adjacent button paddings and line heights exactly to ensure perfect horizontal alignment.

### Hardware-Style Toggle Switches
* **Outer Profile**: Width `30px`, Height `16px`.
* **Visual Thumb**: Perfect circle, diameter `10px`.
* **Internal Clearance**: Centered with exactly `2px` clearance on all sides (`left: 2px; bottom: 2px`).
* **Active Position**: Transform offset transition is locked to `translateX(14px)`.
* **Thumb Color**: Always use `background: var(--text-on-primary)` for high contrast when activated.
```

---

### `Tagium.Photino.Blazor\Program.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;
using Photino.NET;
using Serilog;
using Serilog.Events;
using Tagium.Core.Helpers;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Setup;

namespace Tagium.Photino.Blazor;

public class Program
{
    public static PhotinoWindow? MainWindow { get; set; }

    [STAThread]
    [Obsolete]
    private static void Main(string[] args)
    {
        InitializeLogger();

        try
        {
            Log.Information("Starting Tagium Application...");

            PhotinoBlazorAppBuilder appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

            // Register Services
            ServiceRegistrar.ConfigureServices(appBuilder.Services);

            // Register Root Component
            appBuilder.RootComponents.Add<App>("#app");

            PhotinoBlazorApp app = appBuilder.Build();

            // Pass ServiceProvider to static SchemeHandler
            SchemeHandlers.ServiceProvider = app.Services;

            // Pass MainWindow to static property for runtime accessibility
            MainWindow = app.MainWindow;

            // Custom Scheme for Thumbnails and Previews
            _ = app.MainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

            // Load saved zoom factor on startup
            SettingsManager settings = app.Services.GetRequiredService<SettingsManager>();
            int savedZoom = settings.Current.Zoom;

            _ = app.MainWindow
                .SetTitle("Tagium")
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .Center()
                .SetZoom(savedZoom)
                .SetLogVerbosity(0);

            app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Tagium application terminated unexpectedly.");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void InitializeLogger()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        string logPath = Path.Combine(baseDir, "app.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
    }
}
```

---

### `Tagium.Photino.Blazor\Tagium.Photino.Blazor.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

	<PropertyGroup>
		<OutputType>Exe</OutputType>
		<TargetFramework>net10.0</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<AssemblyName>Tagium</AssemblyName>
		<UseWindowsForms>False</UseWindowsForms>
	</PropertyGroup>

	<ItemGroup>
		<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.10" />
		<PackageReference Include="NativeFileDialogs.Net" Version="1.3.0.1" />
		<!-- Downgraded to 3.2.3 to restore RegisterFileDropHandler support -->
		<PackageReference Include="Photino.Blazor" Version="4.0.13" />
		<PackageReference Include="Serilog" Version="4.4.0" />
		<PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
		<PackageReference Include="Serilog.Sinks.Console" Version="6.1.1" />
		<PackageReference Include="Serilog.Extensions.Logging" Version="10.0.0" />
	</ItemGroup>

	<ItemGroup>
		<ProjectReference Include="..\Tagium.Core\Tagium.Core.csproj" />
	</ItemGroup>

	<ItemGroup>
		<Content Update="wwwroot\**">
			<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
		</Content>
	</ItemGroup>

</Project>
```

---

### `Tagium.Photino.Blazor\Components\App.razor`

```
@namespace Tagium.Photino.Blazor
@using Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor

<Router AppAssembly="@typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
    </Found>
    <NotFound>
        <LayoutView Layout="@typeof(MainLayout)">
            <p>Page not found.</p>
        </LayoutView>
    </NotFound>
</Router>
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@inject SettingsManager Settings

<div class="app-toolbar">
    <!-- Left Section: Scope selector -->
    <div class="toolbar-left">
        <div class="segmented-control">
            <button class="segment-btn @(AppState.CurrentView == AppView.Files ? "active" : "")"
                    @onclick="() => AppState.SwitchView(AppView.Files)">
                <i class="fa-solid fa-images"></i> Files
            </button>
            <button class="segment-btn @(AppState.CurrentView == AppView.Tags ? "active" : "")"
                    @onclick="() => AppState.SwitchView(AppView.Tags)">
                <i class="fa-solid fa-tags"></i> Tags
            </button>
        </div>
    </div>

    <!-- Center Section: Dynamic query input -->
    <div class="toolbar-center">
        @if (AppState.CurrentView == AppView.Files)
        {
            <div class="search-wrapper">
                <SearchInput @ref="SearchInput" />
            </div>
        }
    </div>

    <!-- Right Section: Media grid controls & Settings triggers -->
    <div class="toolbar-right">
        @if (AppState.CurrentView == AppView.Files)
        {
            <button class="btn-icon"
                    @onclick="() => SetViewMode(Settings.Current.FileViewMode == FileViewMode.Grid ? FileViewMode.List : FileViewMode.Grid)"
                    title="@(Settings.Current.FileViewMode == FileViewMode.Grid ? "Switch to List View" : "Switch to Grid View")">
                @if (Settings.Current.FileViewMode == FileViewMode.Grid)
                {
                    <i class="fa-solid fa-list"></i>
                }
                else
                {
                    <i class="fa-solid fa-table-cells"></i>
                }
            </button>

            <div class="view-options-wrapper">
                <button class="btn-icon @(IsMenuOpen ? "active" : "")"
                        @onclick="ToggleMenu"
                        title="View Options">
                    <i class="fa-solid fa-eye"></i>
                </button>

                @if (IsMenuOpen)
                {
                    <div class="view-menu-overlay" @onclick="CloseMenu"></div>
                    <div class="view-menu-dropdown">
                        <div class="menu-section">
                            <label>Sort By</label>
                            <SortControls />
                        </div>
                        <hr />
                        <div class="menu-section">
                            <label>Thumbnail Size</label>
                            <ThumbnailSizeSlider />
                        </div>
                    </div>
                }
            </div>

            <div class="vertical-divider"></div>
        }

        <button class="btn-icon" @onclick="OpenSettings" title="Settings & Preferences">
            <i class="fa-solid fa-gear"></i>
        </button>
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Modals;
using Tagium.Photino.Blazor.Components.Shared;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class AppToolbar
{
    [Inject] private ModalService ModalService { get; set; } = default!;

    private SearchInput? SearchInput { get; set; }
    private bool IsMenuOpen = false;

    public async Task FocusSearchAsync()
    {
        if (SearchInput != null)
        {
            await SearchInput.FocusAsync();
        }
    }

    private void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
    }

    private void CloseMenu()
    {
        IsMenuOpen = false;
    }

    private void SetViewMode(FileViewMode mode)
    {
        Settings.Update(s => s.FileViewMode = mode);
    }

    private void OpenSettings()
    {
        ModalService.Show("Settings", builder =>
        {
            builder.OpenComponent<SettingsModal>(0);
            builder.CloseComponent();
        });
    }
}
```

---

### `Tagium.Photino.Blazor\Components\AppToolbar.razor.css`

```css
.app-toolbar {
    height: var(--toolbar-height);
    border-bottom: 1px solid var(--border-color);
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0 var(--space-sm);
    background: var(--panel-bg);
    user-select: none;
    flex-shrink: 0;
    box-sizing: border-box;
}

/* Sections */
.toolbar-left, .toolbar-right {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
}

.toolbar-center {
    flex: 1;
    max-width: 420px;
    margin: 0 var(--space-md);
}

.search-wrapper {
    width: 100%;
}

/* Vertical Divider */
.vertical-divider {
    width: 1px;
    height: 18px;
    background-color: var(--border-color);
    margin: 0 var(--space-2xs);
}

/* Segmented Control Switcher */
.segmented-control {
    display: inline-flex;
    background-color: var(--bg-color);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-md);
    padding: 2px;
}

.segment-btn {
    background: transparent;
    border: none;
    padding: 3px var(--space-sm);
    border-radius: calc(var(--radius-md) - 2px);
    color: var(--text-muted);
    cursor: pointer;
    font-size: var(--font-size-xs);
    display: flex;
    align-items: center;
    gap: var(--space-2xs);
    font-weight: 500;
    transition: background-color var(--transition-fast), color var(--transition-fast);
}

    .segment-btn:hover {
        color: var(--text-color);
    }

    .segment-btn.active {
        background-color: var(--surface-bg);
        color: var(--text-color);
        box-shadow: 0 1px 3px rgba(0,0,0,0.2);
    }

/* Icon Buttons */
.btn-icon {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-muted);
    width: 28px;
    height: 28px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: var(--font-size-sm);
    transition: background-color var(--transition-fast), color var(--transition-fast);
}

    .btn-icon:hover {
        background-color: var(--surface-hover);
        color: var(--text-color);
    }

    .btn-icon.active {
        background-color: var(--surface-hover);
        color: var(--primary-color);
        border-color: var(--border-color);
    }

/* Dropdown Menu styling */
.view-options-wrapper {
    position: relative;
    display: flex;
    align-items: center;
}

.view-menu-dropdown {
    position: absolute;
    top: 100%;
    right: 0;
    margin-top: var(--space-xs);
    background: var(--panel-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-md);
    padding: var(--space-sm);
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
    z-index: 1001;
    min-width: 220px;
    animation: fadeIn var(--transition-fast) ease-out;
}

.view-menu-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    z-index: 1000;
}

.menu-section {
    display: flex;
    flex-direction: column;
    gap: var(--space-2xs);
}

    .menu-section label {
        font-size: var(--font-size-xs);
        color: var(--text-muted);
        font-weight: 600;
        text-transform: uppercase;
    }

hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: var(--space-sm) 0;
}

@keyframes fadeIn {
    from {
        opacity: 0;
        transform: translateY(-5px);
    }

    to {
        opacity: 1;
        transform: translateY(0);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@implements IDisposable
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings
@inject IJSRuntime JS

<style>
    #file-grid-container {
        display: block !important;
        width: 100% !important;
        height: 100% !important;
        overflow-y: auto !important;
        box-sizing: border-box !important;
        padding: 16px !important;
        opacity: 1 !important;
        transition: opacity 0.15s ease-in-out !important;
    }

        #file-grid-container.initializing {
            opacity: 0 !important;
            pointer-events: none !important;
        }

    .file-grid-row {
        display: flex !important;
        flex-direction: row !important;
        flex-wrap: nowrap !important;
        gap: 16px !important;
        /* Calculate exact width of active columns block plus gaps */
        width: calc((var(--cols-count) * var(--thumb-size)) + ((var(--cols-count) - 1) * 16px)) !important;
        height: calc(var(--thumb-size) + 30px) !important; /* Explicitly lock row height to prevent virtualizer math skewing */
        box-sizing: border-box !important;
        margin: 0 auto 16px auto !important; /* Center the row block horizontally */
        contain: layout style paint !important; /* CSS containment for massive reflow savings */
    }

    .file-card {
        width: var(--thumb-size) !important;
        height: 100% !important; /* Let cards stretch to fill locked row height exactly */
        flex-shrink: 0 !important;
        flex-grow: 0 !important;
        box-sizing: border-box !important;
        contain: strict !important; /* Card has completely locked height and width! */
    }
</style>

<div id="file-grid-container"
     class="grid-container @(_isInitialized ? "" : "initializing")"
     style="--thumb-size: @(Settings.Current.ThumbnailSize)px; --cols-count: @_columnsCount">

    <Virtualize Items="FileRows" ItemSize="@(Settings.Current.ThumbnailSize + 46)" Context="row">
        <div @key="row" class="file-grid-row">
            @foreach (var file in row)
            {
                bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
                bool hasThumb = ThumbnailService.HasThumbnail(file.Id, file.Hash);
                TriggerThumbnailIfMissing(file, hasThumb);

                <div @key="file.Id"
                     class="file-card @(isSelected ? "selected" : "")"
                     data-id="@file.Id"
                     @onclick="e => OnCardClick(e, file.Id)"
                     @ondblclick="e => OnCardDoubleClick(e, file)"
                     @onclick:stopPropagation="true">

                    <div class="thumbnail @(hasThumb ? "" : "broken")">
                        @if (hasThumb)
                        {
                            <img src="tagium://thumbnail/@file.Id/@(file.Hash ?? "none")" alt="@file.Name" />
                        }
                        else
                        {
                            <div class="placeholder-icon">
                                <i class="fa-solid fa-image"></i>
                            </div>
                        }
                    </div>
                    <div class="filename" title="@file.Name">
                        @file.Name
                    </div>
                </div>
            }
        </div>
    </Virtualize>
</div>
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileGrid : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    private List<FileEntry> Files = [];
    private List<List<FileEntry>> FileRows = [];
    protected int _columnsCount = 5;
    private bool _isInitialized = false;
    private DotNetObjectReference<FileGrid>? _objRef;
    private bool _isRenderPending = false;
    private readonly object _renderLock = new();

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gridInteractions.init", _objRef, "file-grid-container");

        await JS.InvokeVoidAsync("eval", @"
            window.setupGridResizeObserver = (helper, containerId) => {
                const el = document.getElementById(containerId);
                if (!el) return;
                let lastCols = -1;
                let timeoutId = null;
                const observer = new ResizeObserver(entries => {
                    for (let entry of entries) {
                        const width = entry.contentRect.width;
                        const thumbSize = parseInt(el.style.getPropertyValue('--thumb-size')) || 120;
                        const gap = 16;
                        const cols = Math.floor((width + gap) / (thumbSize + gap)) || 1;
                        if (cols !== lastCols) {
                            clearTimeout(timeoutId);
                            timeoutId = setTimeout(() => {
                                lastCols = cols;
                                helper.invokeMethodAsync('UpdateColumnsCount', cols);
                            }, 150);
                        }
                    }
                });
                observer.observe(el);
                window.gridObserverInstance = observer;
            };
        ");

        await JS.InvokeVoidAsync("setupGridResizeObserver", _objRef, "file-grid-container");
    }

    private void ReloadData()
    {
        _ = InvokeAsync(() =>
        {
            LoadFiles();
            StateHasChanged();
        });
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnThumbnailGenerated(Guid fileId)
    {
        lock (_renderLock)
        {
            if (_isRenderPending)
            {
                return;
            }
            _isRenderPending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lock (_renderLock)
            {
                _isRenderPending = false;
            }
            await InvokeAsync(StateHasChanged);
        });
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
        RebuildRows();
    }

    private void RebuildRows()
    {
        FileRows = [];
        if (Files == null || Files.Count == 0)
        {
            return;
        }

        int cols = _columnsCount > 0 ? _columnsCount : 1;
        for (int i = 0; i < Files.Count; i += cols)
        {
            FileRows.Add(Files.GetRange(i, Math.Min(cols, Files.Count - i)));
        }
    }

    [JSInvokable]
    public void UpdateColumnsCount(int count)
    {
        _isInitialized = true;
        if (_columnsCount != count)
        {
            _columnsCount = count;
            RebuildRows();
            StateHasChanged();
        }
        else
        {
            StateHasChanged();
        }
    }

    private void TriggerThumbnailIfMissing(FileEntry file, bool hasThumb)
    {
        if (!hasThumb)
        {
            _ = Task.Run(() => ThumbnailService.GenerateThumbnailAsync(file));
        }
    }

    private void OnCardClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnCardDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    [JSInvokable]
    public void SelectFile(string idStr)
    {
        if (!Guid.TryParse(idStr, out Guid id))
        {
            return;
        }

        AppState.SelectFile(id, false, false);
    }

    [JSInvokable]
    public void SelectFiles(string[] idStrs)
    {
        IEnumerable<Guid> ids = idStrs.Select(s => Guid.TryParse(s, out Guid g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value);

        AppState.SetSelection(ids);
    }

    [JSInvokable]
    public void SelectAll()
    {
        AppState.SetSelection(Files.Select(f => f.Id));
    }

    [JSInvokable]
    public void ClearSelection()
    {
        AppState.ClearSelection();
    }

    [JSInvokable]
    public void OnEnterKey()
    {
        if (AppState.SelectedFileIds.Count != 1)
        {
            return;
        }

        Guid id = AppState.SelectedFileIds.First();
        FileEntry? file = Files.FirstOrDefault(f => f.Id == id);

        if (file is null || file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    [JSInvokable]
    public void OnDeleteKey()
    {
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;

        _objRef?.Dispose();
        _ = JS.InvokeVoidAsync("eval", "if (window.gridObserverInstance) { window.gridObserverInstance.disconnect(); delete window.gridObserverInstance; }");
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor.css`

```css
.grid-container {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
    align-content: flex-start;
    height: 100%;
    overflow-y: auto;
    padding: 10px;
    position: relative;
    background-color: var(--bg-color);
}

.file-card {
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    display: flex;
    flex-direction: column;
    cursor: pointer;
    user-select: none;
    transition: border-color var(--transition-fast), background-color var(--transition-fast);
    width: var(--thumb-size);
    height: calc(var(--thumb-size) + 30px);
}

    .file-card:hover {
        border-color: var(--text-muted);
        background: var(--panel-bg);
    }

    .file-card.selected {
        border-color: var(--primary-color);
        box-shadow: 0 0 0 2px rgba(66, 165, 245, 0.3);
    }

.thumbnail {
    flex: 1;
    background: #000;
    overflow: hidden;
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
}

    .thumbnail img {
        width: 100%;
        height: 100%;
        object-fit: contain;
        position: relative;
        z-index: 2;
    }

.placeholder-icon {
    position: absolute;
    font-size: 2.2em;
    color: var(--border-color);
    z-index: 1;
    transition: color var(--transition-fast);
}

.file-card:hover .placeholder-icon {
    color: var(--text-muted);
}

.thumbnail.broken .placeholder-icon {
    color: var(--warning-color);
}

.filename {
    height: 30px;
    line-height: 30px;
    padding: 0 5px;
    font-size: 12px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    background: var(--surface-bg);
    color: var(--text-color);
    border-top: 1px solid var(--border-color);
}
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@implements IDisposable
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings
@inject IJSRuntime JS

<div id="file-list-container" class="list-container">
    <table class="file-list">
        <thead>
            <tr>
                <th></th> <!-- Thumbnail -->
                <th>Name</th>
                <th>Path</th>
                <th>Tags</th>
                <th>Date Added</th>
            </tr>
        </thead>
        <tbody>
            <Virtualize Items="Files" ItemSize="53" Context="file" SpacerElement="tr">
                @{
                    bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
                    bool hasThumb = ThumbnailService.HasThumbnail(file.Id, file.Hash);
                    TriggerThumbnailIfMissing(file, hasThumb);
                }
                <tr @key="file.Id"
                    class="file-row @(isSelected ? "selected" : "")"
                    data-id="@file.Id"
                    @onclick="e => OnRowClick(e, file.Id)"
                    @ondblclick="e => OnRowDoubleClick(e, file)">

                    <td class="thumbnail-cell">
                        <div class="thumbnail @(hasThumb ? "" : "broken")">
                            @if (hasThumb)
                            {
                                <img src="tagium://thumbnail/@file.Id/@(file.Hash ?? "none")" alt="@file.Name" />
                            }
                            else
                            {
                                <div class="placeholder-icon">
                                    <i class="fa-solid fa-image"></i>
                                </div>
                            }
                        </div>
                    </td>
                    <td class="name-cell" title="@file.Name">@file.Name</td>
                    <td class="path-cell" title="@file.Path">@file.Path</td>
                    <td class="tags-cell">
                        @foreach (var tag in file.Tags)
                        {
                            <span class="tag-pill" style="border-color: @tag.ColorHex">@tag.Name</span>
                        }
                    </td>
                    <td class="date-cell">@FormatDate(file.DateAdded)</td>
                </tr>
            </Virtualize>
        </tbody>
    </table>
</div>
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class FileListView : IDisposable
{
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    private List<FileEntry> Files = [];
    private bool _isRenderPending = false;
    private readonly object _renderLock = new();

    protected override void OnInitialized()
    {
        LoadFiles();

        AppState.OnChange += ReloadData;
        AppState.OnSelectionChange += RefreshSelection;
        Library.LibraryChanged += ReloadData;
        Settings.SettingsChanged += ReloadData;
        ThumbnailService.ThumbnailGenerated += OnThumbnailGenerated;
    }

    private void ReloadData()
    {
        _ = InvokeAsync(() =>
        {
            LoadFiles();
            StateHasChanged();
        });
    }

    private void RefreshSelection()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnThumbnailGenerated(Guid fileId)
    {
        lock (_renderLock)
        {
            if (_isRenderPending)
            {
                return;
            }
            _isRenderPending = true;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            lock (_renderLock)
            {
                _isRenderPending = false;
            }
            await InvokeAsync(StateHasChanged);
        });
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
    }

    private void TriggerThumbnailIfMissing(FileEntry file, bool hasThumb)
    {
        if (!hasThumb)
        {
            _ = Task.Run(() => ThumbnailService.GenerateThumbnailAsync(file));
        }
    }

    private void OnRowClick(MouseEventArgs e, Guid id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void OnRowDoubleClick(MouseEventArgs e, FileEntry file)
    {
        if (file.IsMissing)
        {
            return;
        }

        Launcher.OpenFile(file.Path);
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToShortDateString();
    }

    public void Dispose()
    {
        AppState.OnChange -= ReloadData;
        AppState.OnSelectionChange -= RefreshSelection;
        Library.LibraryChanged -= ReloadData;
        Settings.SettingsChanged -= ReloadData;
        ThumbnailService.ThumbnailGenerated -= OnThumbnailGenerated;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileListView.razor.css`

```css
.list-container {
    height: 100%;
    overflow-y: auto;
    background-color: var(--bg-color);
    padding: 0;
    position: relative;
}

.file-list {
    width: 100%;
    border-collapse: collapse;
    font-size: var(--font-size-sm);
    color: var(--text-color);
    table-layout: fixed;
}

    .file-list thead {
        position: sticky;
        top: 0;
        background: var(--panel-bg);
        z-index: 10;
    }

    .file-list th {
        text-align: left;
        padding: 8px 10px;
        font-weight: 600;
        color: var(--text-muted);
        border-bottom: 1px solid var(--border-color);
        font-size: var(--font-size-xs);
        text-transform: uppercase;
        letter-spacing: 0.5px;
    }

    .file-list tbody tr {
        cursor: pointer;
        border-bottom: 1px solid var(--border-color);
        transition: background-color var(--transition-fast);
    }

        .file-list tbody tr:hover {
            background: var(--surface-hover);
        }

        .file-list tbody tr.selected {
            background: rgba(66, 165, 245, 0.15);
            border-left: 3px solid var(--primary-color);
        }

    .file-list td {
        padding: 6px 10px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
    }

.thumbnail-cell {
    width: 40px;
    padding: 2px 0 2px 10px !important;
}

.thumbnail {
    width: 40px;
    height: 40px;
    background: #000;
    border-radius: var(--radius-sm);
    overflow: hidden;
    display: flex;
    align-items: center;
    justify-content: center;
    position: relative;
}

    .thumbnail img {
        width: 100%;
        height: 100%;
        object-fit: contain;
        position: relative;
        z-index: 2;
    }

    .thumbnail .placeholder-icon {
        position: absolute;
        font-size: 1.5em;
        color: var(--border-color);
        z-index: 1;
        transition: color var(--transition-fast);
    }

.file-list tbody tr:hover .placeholder-icon {
    color: var(--text-muted);
}

.thumbnail.broken .placeholder-icon {
    color: var(--warning-color);
}

.name-cell {
    width: 25%;
    font-weight: 500;
}

.path-cell {
    width: 35%;
    color: var(--text-muted);
    font-family: monospace;
    font-size: 0.9em;
}

.tags-cell {
    width: 25%;
    white-space: normal !important;
    line-height: 1.4;
}

.tag-pill {
    display: inline-block;
    background: var(--surface-bg);
    padding: 2px 6px;
    border-radius: 12px;
    font-size: 0.85em;
    margin-right: 4px;
    margin-bottom: 2px;
    border: 1px solid transparent;
    white-space: nowrap;
    max-width: 100px;
    overflow: hidden;
    text-overflow: ellipsis;
    vertical-align: middle;
}

.date-cell {
    width: 10%;
    color: var(--text-muted);
    font-size: 0.9em;
}

/* Column widths */
.file-list th:nth-child(1) {
    width: 60px;
}
/* thumbnail */
.file-list th:nth-child(2) {
    width: 25%;
}
/* name */
.file-list th:nth-child(3) {
    width: 35%;
}
/* path */
.file-list th:nth-child(4) {
    width: 25%;
}
/* tags */
.file-list th:nth-child(5) {
    width: 10%;
}
/* date */
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor`

```
@namespace Tagium.Photino.Blazor.Components
@inherits LayoutComponentBase
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Services.Toasting
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Components.Views
@using Tagium.Photino.Blazor.Components.Shared

<style>
    /* 1. Force the main container to be a rigid, full-screen horizontal row that never wraps */
    .main-layout {
        display: flex !important;
        flex-direction: row !important;
        flex-wrap: nowrap !important;
        width: 100vw !important;
        height: 100vh !important;
        overflow: hidden !important;
        box-sizing: border-box !important;
    }

    /* 2. Constrain the sidebar panel horizontally and vertically */
    .sidebar-panel {
        height: 100% !important;
        overflow: hidden !important;
        display: flex !important;
        flex-direction: column !important;
    }

    /* 3. Lock the middle content panel to occupy exactly the remaining width and height */
    .content-panel {
        flex: 1 !important;
        min-width: 0 !important;
        height: 100% !important;
        display: flex !important;
        flex-direction: column !important;
        overflow: hidden !important;
    }

    /* 4. Force the view container to take up remaining height inside content panel */
    .view-container {
        flex: 1 !important;
        min-height: 0 !important;
        display: flex !important;
        flex-direction: column !important;
        overflow: hidden !important;
    }

    /* 5. Force the file grid and list scrolling containers to scroll natively */
    .list-container,
    .file-grid-container,
    .grid-container {
        flex: 1 !important;
        min-height: 0 !important;
        overflow-y: auto !important;
        overflow-x: auto !important;
    }

    /* 6. Lock the inspector panel width.
                      Note: We specify a default width of 320px WITHOUT !important. This acts as the baseline,
                      and allows the splitter's JavaScript inline style modification to override it perfectly. */
    .inspector-panel {
        width: 320px;
        height: 100% !important;
        overflow: hidden !important;
        display: flex !important;
        flex-direction: column !important;
        min-width: 200px !important;
        flex-shrink: 0 !important;
        flex-grow: 0 !important;
    }

        /* 7. Force all elements inside the inspector panel to respect the outer width bounds */
        .inspector-panel * {
            max-width: 100% !important;
            box-sizing: border-box !important;
        }

    /* 8. Force all container layers in the inspector subtree to wrap and clip overflow horizontally */
    .inspector-container,
    .inspector-content,
    .single-file-inspector,
    .multi-file-inspector,
    .inspector-preview {
        width: 100% !important;
        min-width: 0 !important;
        max-width: 100% !important;
        overflow-x: hidden !important;
    }

    /* 9. Guard splitters from collapsing or wrapping */
    .splitter {
        width: 4px !important;
        cursor: col-resize !important;
        background-color: #1a1a1a !important;
        align-self: stretch !important;
        flex-shrink: 0 !important;
    }

    .zoom-indicator-toast {
        position: fixed !important;
        top: 60px !important;
        left: 50% !important;
        background: rgba(30, 30, 30, 0.95) !important;
        color: var(--text-color) !important;
        padding: 6px 16px !important;
        border-radius: 20px !important;
        font-size: var(--font-size-sm) !important;
        font-weight: 600;
        border: 1px solid var(--border-color) !important;
        box-shadow: 0 4px 12px rgba(0,0,0,0.5) !important;
        z-index: 10000 !important;
        pointer-events: none !important;
        animation: zoomToastFade var(--transition-normal) ease-out !important;
    }

    @@keyframes zoomToastFade {
        from {
            opacity: 0;
            transform: translate(-50%, -10px);
        }

        to {
            opacity: 1;
            transform: translate(-50%, 0);
        }
    }
</style>

<div class="main-layout">
    <div class="sidebar-panel" id="sidebar-panel" style="width: @(SettingsManager.Current.LeftSidebarWidth)px;">
        <TagSidebar />
    </div>

    <div class="splitter" id="splitter-left"></div>

    <div class="content-panel">
        <AppToolbar @ref="AppToolbarRef" />

        <div class="view-container">
            @if (AppState.CurrentView == AppView.Files)
            {
                <div style="display: flex; flex-direction: column; height: 100%; min-width: 0; overflow: hidden;">
                    @if (SettingsManager.Current.FileViewMode == FileViewMode.Grid)
                    {
                        <FileGrid />
                    }
                    else
                    {
                        <FileListView />
                    }
                </div>
            }
            else if (AppState.CurrentView == AppView.Tags)
            {
                <TagManagerView />
            }
        </div>

        <StatusBar />
    </div>

    <div class="splitter" id="splitter-right"></div>

    <div class="inspector-panel" id="inspector-panel" style="width: @(SettingsManager.Current.RightSidebarWidth)px;">
        <Tagium.Photino.Blazor.Components.Inspectors.FileInspector />
    </div>

    @if (_showZoomToast)
    {
        <div class="zoom-indicator-toast" style="transform: translateX(-50%) scale(@((100.0 / SettingsManager.Current.Zoom).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))) !important;">
            @(SettingsManager.Current.Zoom)%
        </div>
    }

    <ModalHost />
</div>
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class MainLayout : IDisposable
{
    [Inject] public AppState AppState { get; set; } = default!;
    [Inject] public LibraryService Library { get; set; } = default!;
    [Inject] public SettingsManager SettingsManager { get; set; } = default!;
    [Inject] public ToastService Toast { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;

    private bool _showZoomToast = false;
    private int _lastActiveZoom = 100;
    private CancellationTokenSource? _zoomToastCts;

    private DotNetObjectReference<MainLayout>? _objRef;

    private AppToolbar? AppToolbarRef { get; set; }

    private static readonly int[] ZoomSteps = [50, 67, 75, 90, 100, 110, 120, 133, 150, 175, 200];

    protected override void OnInitialized()
    {
        _lastActiveZoom = SettingsManager.Current.Zoom;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        ScanService.ScanCompleted += ShowScanStatus;
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += OnLibraryChanged;

        // Queue automatic background pre-generation of all missing thumbnails on startup
        List<FileEntry> allFiles = Library.GetFiles(null, false);
        ThumbnailService.QueueBackgroundGeneration(allFiles);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _objRef = DotNetObjectReference.Create(this);

        await JS.InvokeVoidAsync(
            "layoutResizer.init",
            _objRef,
            "sidebar-panel",
            "splitter-left",
            "inspector-panel",
            "splitter-right");

        await JS.InvokeVoidAsync("globalShortcuts.init", _objRef);
        await JS.InvokeVoidAsync("zoomTracker.init", _objRef, SettingsManager.Current.Zoom);

        // Safely boot theme configurations after the DOM is fully loaded and interactive
        await ThemeService.InitializeAsync(JS);
        await ThemeService.ApplyThemeAsync(JS, SettingsManager.Current.SelectedTheme);
    }

    [JSInvokable]
    public async Task HandleGlobalKeyDown(string key, bool ctrlOrMeta, bool shiftKey)
    {
        if (!ctrlOrMeta)
        {
            return;
        }

        switch (key.ToLower())
        {
            case "z":
                AppState.Undo();
                break;
            case "f":
                if (AppToolbarRef != null)
                {
                    await AppToolbarRef.FocusSearchAsync();
                }
                break;
            case "t":
                AppState.SwitchView(AppState.CurrentView == AppView.Files
                    ? AppView.Files
                    : AppView.Tags);
                break;
            case "r":
                await ScanService.ScanAllAsync();
                break;
            case "+":
            case "=":
                AdjustZoom(1);
                break;
            case "-":
                AdjustZoom(-1);
                break;
            case "0":
                ResetZoom();
                break;
        }
    }

    [JSInvokable]
    public void HandleWheelZoom(int direction)
    {
        AdjustZoom(direction);
    }

    [JSInvokable]
    public void SaveLeftSidebarWidth(int width)
    {
        SettingsManager.Update(s => s.LeftSidebarWidth = width);
    }

    [JSInvokable]
    public void SaveRightSidebarWidth(int width)
    {
        SettingsManager.Update(s => s.RightSidebarWidth = width);
    }

    [JSInvokable]
    public void OnNativeZoomChanged(int rawZoom)
    {
        int closestZoom = 100;
        int minDiff = int.MaxValue;
        foreach (int step in ZoomSteps)
        {
            int diff = Math.Abs(step - rawZoom);
            if (diff < minDiff)
            {
                minDiff = diff;
                closestZoom = step;
            }
        }

        if (SettingsManager.Current.Zoom != closestZoom)
        {
            SettingsManager.Update(s => s.Zoom = closestZoom);
        }
    }

    private void AdjustZoom(int direction)
    {
        int currentZoom = SettingsManager.Current.Zoom;
        int currentIndex = Array.IndexOf(ZoomSteps, currentZoom);

        if (currentIndex == -1)
        {
            int closestIndex = 0;
            int minDiff = int.MaxValue;
            for (int i = 0; i < ZoomSteps.Length; i++)
            {
                int diff = Math.Abs(ZoomSteps[i] - currentZoom);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closestIndex = i;
                }
            }
            currentIndex = closestIndex;
        }

        int newIndex = currentIndex + direction;
        if (newIndex >= 0 && newIndex < ZoomSteps.Length)
        {
            int targetZoom = ZoomSteps[newIndex];
            SettingsManager.Update(s => s.Zoom = targetZoom);
            Program.MainWindow?.SetZoom(targetZoom);
        }
    }

    private void ResetZoom()
    {
        SettingsManager.Update(s => s.Zoom = 100);
        Program.MainWindow?.SetZoom(100);
    }

    private void OnSettingsChanged()
    {
        if (SettingsManager.Current.Zoom != _lastActiveZoom)
        {
            _lastActiveZoom = SettingsManager.Current.Zoom;
            TriggerZoomToast();
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private void TriggerZoomToast()
    {
        _showZoomToast = true;

        _zoomToastCts?.Cancel();
        _zoomToastCts = new CancellationTokenSource();
        CancellationToken token = _zoomToastCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, token);
                await InvokeAsync(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        _showZoomToast = false;
                        StateHasChanged();
                    }
                });
            }
            catch (TaskCanceledException)
            {
                // Gracefully catch cancellation
            }
        }, token);
    }

    private void ShowScanStatus()
    {
        _ = InvokeAsync(() =>
        {
            Toast.Show("Library scan complete.", ToastType.Success);
            Library.Refresh();
            StateHasChanged();

            // Automatically queue background pre-generation of newly cataloged files
            List<FileEntry> allFiles = Library.GetFiles(null, false);
            ThumbnailService.QueueBackgroundGeneration(allFiles);
        });
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void ReloadCurrentView()
    {
        AppState.SetSearch("");
        AppState.ClearSelection();
        Library.Refresh();
    }

    public void Dispose()
    {
        SettingsManager.SettingsChanged -= OnSettingsChanged;
        ScanService.ScanCompleted -= ShowScanStatus;
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= OnLibraryChanged;

        _zoomToastCts?.Cancel();
        _zoomToastCts?.Dispose();

        // Cancel background task on dispose to prevent thread leaks during shutdown
        ThumbnailService.CancelBackgroundGeneration();

        _objRef?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor.css`

```css
/* Main Window Shell (Column: Toolbar | Workspace) */
.app-window {
    display: flex;
    flex-direction: column;
    height: 100vh;
    width: 100vw;
    overflow: hidden;
}

/* Workspace Row (Sidebar | Content | Inspector) */
.workspace-layout {
    display: flex;
    flex: 1; /* Fills remaining height after Toolbar */
    flex-direction: row;
    overflow: hidden;
    position: relative;
    background: var(--bg-color);
}

.left-panel {
    width: 250px;
    background: var(--panel-bg);
    display: flex;
    flex-direction: column;
    flex-shrink: 0;
    border-right: 1px solid var(--border-color);
}

.center-panel {
    flex: 1;
    display: flex;
    flex-direction: column;
    background: var(--bg-color);
    min-width: 0; /* Prevents flex items from overflowing horizontally */
}

.right-panel {
    width: 300px;
    background: var(--panel-bg);
    display: flex; /* Ensure inner content can fill height */
    flex-direction: column;
    flex-shrink: 0;
    border-left: 1px solid var(--border-color);
}

.content-area {
    flex: 1;
    overflow: hidden;
    position: relative;
    display: flex;
    flex-direction: column;
}

/* Splitter Styles */
.splitter {
    width: 4px;
    background: var(--bg-color);
    cursor: col-resize;
    flex-shrink: 0;
    position: relative;
    z-index: 30; /* Draw above toolbar during resize */
    transition: background 0.2s;
    margin-left: -2px;
    margin-right: -2px;
}

    .splitter:hover, .splitter.active {
        background: var(--primary-color);
    }

/* Error Boundary Fallback */
.error-boundary-fallback {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    padding: var(--space-md);
    color: var(--text-muted);
    text-align: center;
}

    .error-boundary-fallback h3 {
        margin: 0 0 var(--space-sm) 0;
        color: var(--danger-color);
    }

    .error-boundary-fallback p {
        margin: 0 0 var(--space-md) 0;
        font-size: var(--font-size-sm);
    }

    .error-boundary-fallback .btn {
        background: var(--primary-color);
        border: none;
        color: white;
        padding: var(--space-xs) var(--space-sm);
        border-radius: 4px;
        cursor: pointer;
        font-size: var(--font-size-sm);
    }

        .error-boundary-fallback .btn:hover {
            background: var(--primary-hover);
        }
```

---

### `Tagium.Photino.Blazor\Components\ModalHost.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Services
@inject ModalService ModalService

@if (IsVisible)
{
    <div class="modal-backdrop" @onclick="Close">
        <div class="modal-container" @onclick:stopPropagation="true">
            <div class="modal-header">
                <h3>@ModalService.Title</h3>
                <button class="close-btn" @onclick="Close">
                    <i class="fa-solid fa-xmark"></i>
                </button>
            </div>
            <div class="modal-body">
                @ModalService.Content
            </div>
        </div>
    </div>
}
```

---

### `Tagium.Photino.Blazor\Components\ModalHost.razor.cs`

```csharp
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class ModalHost
{
    private bool IsVisible => ModalService.Content is not null;

    protected override void OnInitialized()
    {
        ModalService.OnShow += StateHasChanged;
        ModalService.OnClose += StateHasChanged;
    }

    private void Close()
    {
        ModalService.Close();
    }

    public void Dispose()
    {
        ModalService.OnShow -= StateHasChanged;
        ModalService.OnClose -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\ModalHost.razor.css`

```css
.modal-backdrop {
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: rgba(0, 0, 0, 0.7);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
}

.modal-container {
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-lg);
    width: 640px;
    max-width: 95%;
    max-height: 85vh;
    display: flex;
    flex-direction: column;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
}

.modal-header {
    padding: var(--space-sm);
    border-bottom: 1px solid var(--border-color);
    display: flex;
    justify-content: space-between;
    align-items: center;
}

    .modal-header h3 {
        margin: 0;
        font-size: 1.1em;
        color: var(--text-color);
    }

.close-btn {
    background: none;
    border: none;
    color: var(--text-muted);
    font-size: 1.5em;
    cursor: pointer;
    line-height: 1;
}

    .close-btn:hover {
        color: var(--text-color);
    }

.modal-body {
    padding: var(--space-sm);
    overflow-y: auto;
}
```

---

### `Tagium.Photino.Blazor\Components\TagSidebar.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Shared
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings

<div class="sidebar-container">
    <div class="tag-list @GetDisplayModeClass()">
        <div class="tag-item system-item @(IsAllFilesActive ? "active" : "")"
             @onclick="() => SetSearch(string.Empty)">
            <i class="fa-solid fa-folder icon folder-accent fa-fw"></i> All Files
        </div>

        <div class="tag-item system-item @(IsMissingActive ? "active" : "")"
             @onclick="() => AppState.SetMissingOnly(true)">
            <i class="fa-solid fa-triangle-exclamation icon warning-accent fa-fw"></i> Missing Files
        </div>

        <hr />

        @foreach (var tag in Tags)
        {
            bool isActive = AppState.SearchText.Equals(tag.Name, StringComparison.OrdinalIgnoreCase)
            && !AppState.IsMissingOnly
            && AppState.CurrentView == AppView.Files;

            if (Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.MinimalCircle)
            {
                // Legacy mode: colored circles
                <div class="tag-item legacy @(isActive ? "active" : "")" @onclick="() => SetSearch(tag.Name)">
                    <TagIcon Tag="@tag" />
                    <span class="name">@tag.Name</span>
                    <span class="count">@tag.FileCount</span>
                </div>
            }
            else
            {
                // Modern mode: colored pills
                string tagStyle = isActive
                ? $"background-color: {tag.ColorHex}; border-color: {tag.ColorHex}; border-style: solid;"
                : $"border-color: {tag.ColorHex};";

                <div class="tag-item pill @(isActive ? "active" : "")"
                     @onclick="() => SetSearch(tag.Name)"
                     style="@tagStyle">
                    @if (!string.IsNullOrWhiteSpace(tag.Emoji))
                    {
                        <span class="item-emoji">@tag.Emoji</span>
                    }
                    <span class="name">@tag.Name</span>
                    <span class="count">@tag.FileCount</span>
                </div>
            }
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\TagSidebar.razor.cs`

```csharp
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components;

public partial class TagSidebar
{
    private List<Tag> Tags = [];

    private bool IsAllFilesActive => string.IsNullOrEmpty(AppState.SearchText)
        && !AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private bool IsMissingActive => AppState.IsMissingOnly
        && AppState.CurrentView == AppView.Files;

    private string GetDisplayModeClass()
    {
        return Settings.Current.TagSidebarStyle.ToString().ToLowerInvariant();
    }

    protected override void OnInitialized()
    {
        Tags = Library.GetTags();
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshData()
    {
        _ = InvokeAsync(() =>
        {
            Tags = Library.GetTags();
            StateHasChanged();
        });
    }

    private void SetSearch(string text)
    {
        AppState.SetSearch(text);
    }

    public void Dispose()
    {
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= RefreshData;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\TagSidebar.razor.css`

```css
/* ::deep combinator pierces CSS isolation */
::deep .sidebar-container {
    display: flex;
    flex-direction: column;
    height: 100%;
    color: var(--text-color);
}

::deep .tag-list {
    flex: 1;
    overflow-y: auto;
    padding: var(--space-xs);
}

/* ====== Pill Mode (Modern) ====== */
::deep .tag-item.pill {
    padding: 3px 8px;
    cursor: pointer;
    display: flex;
    align-items: center;
    border-radius: 12px;
    margin-bottom: 3px;
    color: var(--text-color);
    transition: background-color var(--transition-fast), border-color var(--transition-fast), color var(--transition-fast);
    user-select: none;
    font-size: var(--font-size-sm);
    line-height: 1;
    border: 1px dashed;
}

    ::deep .tag-item.pill:hover {
        background: var(--surface-bg);
    }

    ::deep .tag-item.pill.active {
        color: white;
    }

    ::deep .tag-item.pill .count {
        font-size: 0.8em;
        margin-left: auto;
    }

    ::deep .tag-item.pill .item-emoji {
        margin-right: 6px;
    }

/* ====== Legacy Mode (Minimal Circles) ====== */
::deep .tag-item.legacy {
    padding: 3px 8px;
    cursor: pointer;
    display: flex;
    align-items: center;
    border-radius: 4px;
    margin-bottom: 2px;
    color: var(--text-color);
    transition: background-color var(--transition-fast);
    user-select: none;
    font-size: var(--font-size-sm);
    line-height: 1;
    border: none;
}

    ::deep .tag-item.legacy:hover {
        background: var(--surface-bg);
    }

    ::deep .tag-item.legacy.active {
        background: var(--primary-color);
        color: var(--text-on-primary);
    }

    ::deep .tag-item.legacy .count {
        font-size: 0.8em;
        color: var(--text-muted);
        margin-left: auto;
    }

    ::deep .tag-item.legacy .tag-emoji {
        margin-right: 8px;
        width: 1.2em;
        text-align: center;
        font-size: var(--font-size-sm);
    }

/* ====== Shared Styles ====== */
::deep .system-item {
    border: none !important;
    font-size: var(--font-size-sm);
    line-height: 1;
    padding: 3px 8px;
    margin-bottom: 2px;
    display: flex;
    align-items: center;
    border-radius: 4px;
    cursor: pointer;
    user-select: none;
    transition: background-color var(--transition-fast);
}

    ::deep .system-item.active {
        background: var(--primary-color) !important;
        color: var(--text-on-primary) !important;
    }

::deep .icon {
    margin-right: 6px;
    display: inline-block;
    width: 1.2em;
    text-align: center;
    font-size: 1em;
    transition: color var(--transition-fast);
}

/* Color definitions */
::deep .system-item .folder-accent {
    color: var(--folder-color);
}

::deep .system-item .warning-accent {
    color: var(--warning-color);
}

/* High contrast active selection override */
::deep .system-item.active .folder-accent,
::deep .system-item.active .warning-accent {
    color: var(--text-on-primary) !important;
}

::deep hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: var(--space-xs) 0;
}
```

---

### `Tagium.Photino.Blazor\Components\ToastContainer.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Services.Toasting
@inject ToastService ToastService

<div class="toast-container">
    @foreach (var toast in ToastService.Toasts)
    {
        <div class="toast @GetClass(toast)" @onclick="() => Remove(toast)">
            @toast.Message
        </div>
    }
</div>

<style>
    .toast-container {
        position: fixed;
        bottom: 20px;
        right: 20px;
        display: flex;
        flex-direction: column;
        gap: 10px;
        z-index: 9999;
        pointer-events: none;
    }

    .toast {
        background: #333;
        color: white;
        padding: 10px 20px;
        border-radius: 4px;
        box-shadow: 0 4px 12px rgba(0,0,0,0.5);
        font-size: 0.9em;
        min-width: 200px;
        pointer-events: auto;
        cursor: pointer;
        animation: slideIn 0.3s ease-out;
        border-left: 4px solid transparent;
        opacity: 0.95;
    }

        .toast:hover {
            opacity: 1;
        }

        .toast.info {
            border-left-color: #42a5f5;
        }

        .toast.success {
            border-left-color: #66bb6a;
        }

        .toast.error {
            border-left-color: #ef5350;
        }

    @@keyframes slideIn {
        from {
            transform: translateX(100%);
            opacity: 0;
        }

        to {
            transform: translateX(0);
            opacity: 0.95;
        }
    }
</style>
```

---

### `Tagium.Photino.Blazor\Components\ToastContainer.razor.cs`

```csharp
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components;

public partial class ToastContainer
{
    protected override void OnInitialized()
    {
        ToastService.OnChange += HandleToastChange;
    }

    private async void HandleToastChange()
    {
        // Marshals the update to the UI thread to prevent cross-thread exceptions/silence
        await InvokeAsync(StateHasChanged);
    }

    private static string GetClass(Toast toast)
    {
        return toast.Type switch
        {
            ToastType.Success => "success",
            ToastType.Error => "error",
            _ => "info"
        };
    }

    private void Remove(Toast toast)
    {
        ToastService.Remove(toast);
    }

    public void Dispose()
    {
        ToastService.OnChange -= HandleToastChange;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\_Imports.razor`

```
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.JSInterop
@using Tagium.Photino.Blazor
@using Tagium.Photino.Blazor.Components
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Components.Views
@using Tagium.Core.Models
@using Tagium.Core.Services
```

---

### `Tagium.Photino.Blazor\Models\Theme.cs`

```csharp
namespace Tagium.Photino.Blazor.Models;

public class Theme
{
    public required string Name { get; set; }
    public required Dictionary<string, string> Colors { get; set; }
}
```

---

### `Tagium.Photino.Blazor\Services\AppState.cs`

```csharp
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Services;

public enum AppView { Files, Tags }

public class AppState
{
    private readonly LibraryService _library;

    public AppView CurrentView { get; private set; } = AppView.Files;
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<Guid> SelectedFileIds { get; private set; } = [];

    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public AppState(LibraryService library)
    {
        _library = library;
    }

    public void SwitchView(AppView view)
    {
        if (CurrentView == view)
        {
            return;
        }

        CurrentView = view;
        NotifyStateChanged();
    }

    public void Undo()
    {
        _library.Undo();
    }

    public void SetSearch(string text)
    {
        SearchText = text;
        IsMissingOnly = false;
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SetMissingOnly(bool isMissing)
    {
        IsMissingOnly = isMissing;
        SearchText = "";
        SelectedFileIds.Clear();

        if (CurrentView != AppView.Files)
        {
            CurrentView = AppView.Files;
        }

        NotifyStateChanged();
    }

    public void SelectFile(Guid id, bool multiSelect, bool rangeSelect)
    {
        if (!multiSelect)
        {
            SelectedFileIds.Clear();
            _ = SelectedFileIds.Add(id);
        }
        else
        {
            _ = SelectedFileIds.Contains(id)
                ? SelectedFileIds.Remove(id)
                : SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void SetSelection(IEnumerable<Guid> ids)
    {
        SelectedFileIds.Clear();

        foreach (Guid id in ids)
        {
            _ = SelectedFileIds.Add(id);
        }

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
        if (SelectedFileIds.Count <= 0)
        {
            return;
        }

        SelectedFileIds.Clear();
        NotifySelectionChanged();
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }

    private void NotifySelectionChanged()
    {
        OnSelectionChange?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\FileDialogService.cs`

```csharp
using NativeFileDialogs.Net;

namespace Tagium.Photino.Blazor.Services;

public class FileDialogService
{
    public static string[]? OpenFiles()
    {
        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok
            ? paths
            : null;
    }

    public static string? PickFolder()
    {
        NfdStatus result = Nfd.PickFolder(out string path, null);

        return result == NfdStatus.Ok
            ? path
            : null;
    }
}
```

---

### `Tagium.Photino.Blazor\Services\FileImportService.cs`

```csharp
using Microsoft.AspNetCore.Components.Forms;
using Tagium.Core.Helpers;

namespace Tagium.Photino.Blazor.Services;

public class FileImportService
{
    private const long MaxFileSize = 500 * 1024 * 1024; // 500 MB

    public async Task<string[]> SaveDroppedFilesAsync(IReadOnlyList<IBrowserFile> files)
    {
        string importDir = Path.Combine(AppDataHelper.GetBaseDirectory(), "Imports");
        if (!Directory.Exists(importDir))
        {
            _ = Directory.CreateDirectory(importDir);
        }

        List<string> savedPaths = [];

        foreach (var file in files)
        {
            try
            {
                // Create unique filename to prevent collisions in staging
                string safeName = Path.GetFileNameWithoutExtension(file.Name)
                                  + "_" + Guid.NewGuid().ToString()[..8]
                                  + Path.GetExtension(file.Name);

                string localPath = Path.Combine(importDir, safeName);

                using var stream = file.OpenReadStream(MaxFileSize);
                using var fs = File.Create(localPath);
                await stream.CopyToAsync(fs);

                savedPaths.Add(localPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileImportService] Failed to save dropped file '{file.Name}': {ex.Message}");
            }
        }

        return savedPaths.ToArray();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\KeyboardShortcutService.cs`

```csharp
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Services;
using Tagium.Photino.Blazor.Components.Shared;

namespace Tagium.Photino.Blazor.Services;

public class KeyboardShortcutService
{
    private readonly AppState _appState;
    private readonly LibraryService _library;
    private readonly SearchInput _searchInput;

    public KeyboardShortcutService(AppState appState, LibraryService library, SearchInput searchInput)
    {
        _appState = appState;
        _library = library;
        _searchInput = searchInput;
    }

    public async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (!e.CtrlKey && !e.MetaKey)
        {
            return;
        }

        switch (e.Key.ToLower())
        {
            case "f":
                await _searchInput.FocusAsync();
                break;
            case "t":
                ToggleTagView();
                break;
            case "r":
                _library.Refresh();
                break;
        }
    }

    private void ToggleTagView()
    {
        _appState.SwitchView(
            _appState.CurrentView == AppView.Tags ? AppView.Files : AppView.Tags
        );
    }
}
```

---

### `Tagium.Photino.Blazor\Services\ModalService.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Services;

public class ModalService
{
    public event Action? OnShow;
    public event Action? OnClose;

    public RenderFragment? Content { get; private set; }
    public string Title { get; private set; } = "";

    public void Show(string title, RenderFragment content)
    {
        Title = title;
        Content = content;
        OnShow?.Invoke();
    }

    public void Close()
    {
        Content = null;
        OnClose?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\SystemLauncherService.cs`

```csharp
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Tagium.Photino.Blazor.Services;

public class SystemLauncherService
{
    private readonly ILogger<SystemLauncherService> _logger;

    public SystemLauncherService(ILogger<SystemLauncherService> logger)
    {
        _logger = logger;
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot open file. File does not exist at path: {Path}", path);
            return;
        }

        try
        {
            _logger.LogInformation("Opening file: {Path}", path);
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open file: {Path}", path);
        }
    }

    public void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("Cannot show in explorer. File does not exist at path: {Path}", path);
            return;
        }

        try
        {
            _logger.LogInformation("Showing file in explorer: {Path}", path);
            // Select the file in explorer
            string argument = $"/select,\"{path}\"";

            ProcessStartInfo processStartInfo = new("explorer.exe", argument)
            {
                UseShellExecute = true
            };

            _ = Process.Start(processStartInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show file in explorer: {Path}", path);
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Services\ThemeService.cs`

```csharp
using Microsoft.JSInterop;
using System.Text.Json;
using Tagium.Core.Helpers;
using Tagium.Photino.Blazor.Models;

namespace Tagium.Photino.Blazor.Services;

public class ThemeService
{
    public List<Theme> LoadedThemes { get; } = [];

    private readonly string _themesDir;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public ThemeService()
    {
        _themesDir = AppDataHelper.GetThemesDirectory();
    }

    public async Task InitializeAsync(IJSRuntime js)
    {
        SyncDefaultThemesFromShippingDir();
        ReloadThemes();
        await Task.CompletedTask;
    }

    public async Task ApplyThemeAsync(IJSRuntime js, string themeName)
    {
        if (string.Equals(themeName, "Default", StringComparison.OrdinalIgnoreCase))
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", (object?)null);
            return;
        }

        Theme? active;
        lock (_lock)
        {
            active = LoadedThemes.FirstOrDefault(t => string.Equals(t.Name, themeName, StringComparison.OrdinalIgnoreCase));
        }

        if (active != null)
        {
            await js.InvokeVoidAsync("themeManager.applyTheme", active.Colors);
        }
    }

    private void ReloadThemes()
    {
        lock (_lock)
        {
            LoadedThemes.Clear();

            if (!Directory.Exists(_themesDir))
            {
                return;
            }

            string[] files = Directory.GetFiles(_themesDir, "*.json");
            foreach (string file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    Theme? theme = JsonSerializer.Deserialize<Theme>(json, _jsonOptions);

                    if (theme != null && !string.IsNullOrWhiteSpace(theme.Name))
                    {
                        // Safely de-duplicate in memory to handle orphaned files on the user's disk
                        bool alreadyLoaded = LoadedThemes.Any(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
                        if (!alreadyLoaded)
                        {
                            LoadedThemes.Add(theme);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ThemeService] Error loading theme file '{file}': {ex.Message}");
                }
            }
        }
    }

    private void SyncDefaultThemesFromShippingDir()
    {
        string shippingThemesDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "themes");
        if (!Directory.Exists(shippingThemesDir) || !Directory.Exists(_themesDir))
        {
            return;
        }

        try
        {
            string[] shippingFiles = Directory.GetFiles(shippingThemesDir, "*.json");
            foreach (string file in shippingFiles)
            {
                string fileName = Path.GetFileName(file);
                string destPath = Path.Combine(_themesDir, fileName);

                // Copy default templates only if missing to prevent overriding user customizations
                if (!File.Exists(destPath))
                {
                    File.Copy(file, destPath, true);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThemeService] Error syncing default themes from shipping folder: {ex.Message}");
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\SchemeHandlers.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static IServiceProvider? ServiceProvider { get; set; }

    [Obsolete]
    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        if (uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return HandleThumbnail(uri, out contentType);
        }

        return uri.Host.Equals("preview", StringComparison.OrdinalIgnoreCase) ? HandlePreview(uri, out contentType) : Stream.Null;
    }

    [Obsolete]
    private static Stream HandleThumbnail(Uri uri, out string contentType)
    {
        contentType = "image/jpeg";

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        try
        {
            ThumbnailService thumbnailService = ServiceProvider.GetRequiredService<ThumbnailService>();

            // Extract the hash parameter directly from the second URL segment
            string hash = segments.Length > 1 && segments[1] != "none" ? segments[1] : "";

            string path = thumbnailService.GetThumbnailPath(hash, fileId);

            if (!File.Exists(path))
            {
                return Stream.Null;
            }

            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SchemeHandler] Failed to resolve thumbnail for {fileId}: {ex.Message}");
        }

        return Stream.Null;
    }

    private static Stream HandlePreview(Uri uri, out string contentType)
    {
        contentType = "application/octet-stream";

        if (ServiceProvider is null)
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length == 0 || !Guid.TryParse(segments[0], out Guid fileId))
        {
            return Stream.Null;
        }

        FileRepository fileRepo = ServiceProvider.GetRequiredService<FileRepository>();
        FileEntry? entry = fileRepo.GetById(fileId);

        if (entry is null || !File.Exists(entry.Path))
        {
            return Stream.Null;
        }

        contentType = GetMimeType(Path.GetExtension(entry.Path));
        return new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static string GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "application/octet-stream"
        };
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\ServiceRegister.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Core.Services.Undo;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Setup;

public static class ServiceRegistrar
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        _ = services.AddLogging(loggingBuilder => loggingBuilder.AddSerilog(dispose: true));

        // Core - Settings
        _ = services.AddSingleton<SettingsManager>();

        // Core - Data
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();
        _ = services.AddSingleton<SearchQueryCompiler>();
        _ = services.AddSingleton<FileSearchRepository>();
        _ = services.AddSingleton<LibraryRootRepository>();

        // Core - Undo
        _ = services.AddSingleton<UndoService>();

        // Core - Helpers
        _ = services.AddSingleton<FileHasher>();

        // Core - Services
        _ = services.AddSingleton<ExifToolService>();
        _ = services.AddSingleton<ThumbnailCache>();
        _ = services.AddSingleton<ThumbnailGenerator>();
        _ = services.AddSingleton<VideoFrameExtractor>();
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryScanService>();
        _ = services.AddSingleton<LibraryService>();
        _ = services.AddSingleton<LibraryIntegrityService>();
        _ = services.AddSingleton<DataExportService>();
        _ = services.AddSingleton<DatabaseMergeService>();

        // UI - Components
        _ = services.AddTransient<SearchInput>();

        // UI - Services
        _ = services.AddSingleton<AppState>();
        _ = services.AddSingleton<ModalService>();
        _ = services.AddSingleton<FileDialogService>();
        _ = services.AddSingleton<SystemLauncherService>();
        _ = services.AddSingleton<ToastService>();
        _ = services.AddSingleton<KeyboardShortcutService>();
        _ = services.AddSingleton<ThemeService>();
    }
}
```

---

### `Tagium.Photino.Blazor\wwwroot\index.html`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Tagium</title>
    <base href="/" />
    <link href="vendor/fontawesome/css/all.min.css" rel="stylesheet" />
    <link href="css/app.css" rel="stylesheet" />
    <!-- Required for Blazor CSS Isolation (MainLayout.razor.css, etc.) -->
    <link href="Tagium.styles.css" rel="stylesheet" />
    <style>
        html, body {
            height: 100%;
            margin: 0;
            overflow: hidden;
        }

        #app {
            height: 100%;
        }

        .drag-over::after {
            content: 'Drop files to add';
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            background: rgba(66, 165, 245, 0.3);
            border: 4px dashed #42a5f5;
            display: flex;
            justify-content: center;
            align-items: center;
            font-size: 2em;
            color: white;
            text-shadow: 0 2px 4px rgba(0,0,0,0.5);
            z-index: 9999;
            pointer-events: none;
            box-sizing: border-box;
        }

        /* Utility class used by layout.js during drag */
        .no-select {
            user-select: none;
        }
    </style>
</head>
<body>
    <div id="app">Loading...</div>
    <script>
        window.themeManager = {
            applyTheme: function (colors) {
                if (!colors) {
                    document.documentElement.removeAttribute('style');
                    return;
                }
                document.documentElement.removeAttribute('style');
                for (const [key, value] of Object.entries(colors)) {
                    document.documentElement.style.setProperty(key, value);
                }
            }
        };
    </script>
    <script src="js/dragdrop.js"></script>
    <script src="js/grid.js"></script>
    <script src="js/layout.js"></script>
    <script src="js/shortcuts.js"></script>
    <script src="js/zoom.js"></script>
    <script src="_framework/blazor.webview.js"></script>
</body>
</html>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\Component.razor`

```
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject SystemLauncherService Launcher
@inject FileDialogService FileDialog
@inject AppState AppState
@inject ModalService ModalService

<div class="inspector-container">
    <div class="inspector-content">
        @if (HasRenderError)
        {
            <div class="state-message error">
                <p>⚠️ Display Error</p>
                <button class="btn" @onclick="LoadInspectorDataAsync">Retry</button>
            </div>
        }
        else if (SelectionMode == InspectorMode.None)
        {
            <div class="state-message">
                <i class="fa-solid fa-hand-pointer icon"></i>
                <p>Select files to inspect</p>
            </div>
        }
        else if (SelectionMode == InspectorMode.Single && SelectedFile is not null)
        {
            <SingleFileInspector @key="SelectionKey"
                                 File="SelectedFile" />
        }
        else if (SelectionMode == InspectorMode.Multi)
        {
            <MultiFileInspector @key="SelectionKey" />
        }
        else
        {
            <div class="state-message">
                <p>Loading...</p>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor.cs`

```csharp
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class FileInspector
{
    private InspectorMode SelectionMode = InspectorMode.None;
    private FileEntry? SelectedFile;
    private bool HasRenderError = false;
    private CancellationTokenSource? _cts;
    private string SelectionKey = Guid.NewGuid().ToString();

    protected override void OnInitialized()
    {
        AppState.OnSelectionChange += OnSelectionChanged;
        _ = LoadInspectorDataAsync();
    }

    private void OnSelectionChanged()
    {
        SelectionKey = Guid.NewGuid().ToString();
        _ = LoadInspectorDataAsync();
    }

    private async Task LoadInspectorDataAsync()
    {
        // Cancel any previous ongoing operation
        _cts?.Cancel();
        using CancellationTokenSource cts = new();
        _cts = cts;
        var token = cts.Token;

        try
        {
            // Capture selected IDs on UI thread
            List<Guid> selectedIds = AppState.SelectedFileIds.ToList();
            int count = selectedIds.Count;

            if (count == 0)
            {
                await InvokeAsync(() =>
                {
                    SelectionMode = InspectorMode.None;
                    SelectedFile = null;
                    StateHasChanged();
                });
                return;
            }

            // Perform database operations in background
            await Task.Run(async () =>
            {
                if (count == 1)
                {
                    var id = selectedIds[0];
                    var file = Library.GetFileWithTagsById(id);
                    if (file is null)
                    {
                        return;
                    }

                    // Update UI on UI thread
                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Single;
                        SelectedFile = file;
                        StateHasChanged();
                    });
                }
                else
                {
                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Multi;
                        SelectedFile = null;
                        StateHasChanged();
                    });
                }
            }, token);
        }
        catch (OperationCanceledException)
        {
            // Ignore, expected when new selection occurs
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Inspector] Error: {ex}");
            await InvokeAsync(() =>
            {
                HasRenderError = true;
                StateHasChanged();
            });
        }
        finally
        {
            if (_cts == cts)
            {
                _cts = null;
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        AppState.OnSelectionChange -= OnSelectionChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\FileInspector.razor.css`

```css
.inspector-container {
    display: flex;
    flex-direction: column;
    height: 100%;
    background-color: var(--panel-bg);
    color: var(--text-color);
    border-left: 1px solid var(--border-color);
    min-width: 0;
}

.inspector-content {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden;
    padding: var(--space-sm);
    position: relative;
    min-width: 0;
}

.state-message {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-muted);
    text-align: center;
    opacity: 0.7;
}

    .state-message .icon {
        font-size: 1.5em;
        margin-bottom: 8px;
    }

    .state-message.error {
        color: var(--danger-color);
        opacity: 1;
    }

.btn {
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 3px 8px;
    border-radius: 4px;
    cursor: pointer;
    margin-top: 8px;
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\InspectorMode.cs`

```csharp
namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class FileInspector
{
    private enum InspectorMode { None, Single, Multi }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors

<div class="multi-file-inspector">
    <div class="inspector-multi-header">
        <h3 class="multi-title">@AppState.SelectedFileIds.Count Files Selected</h3>
    </div>

    <TagInspectorPanel FileIds="AppState.SelectedFileIds" />
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class MultiFileInspector
{
    [Inject] private AppState AppState { get; set; } = default!;
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor.css`

```css
.multi-file-inspector {
    display: flex;
    flex-direction: column;
    gap: var(--space-sm);
    height: 100%;
    overflow-y: auto;
    padding: var(--space-sm);
    box-sizing: border-box;
    width: 100%;
    min-width: 0;
}

.inspector-multi-header {
    flex-shrink: 0;
}

.multi-title {
    margin: 0;
    font-size: 1.1em;
    line-height: 1.3;
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors

<div class="single-file-inspector">
    <div class="inspector-file-header">
        <h3 class="file-name">@File.Name</h3>
        <p class="file-path">@File.Path</p>
    </div>

    <div class="inspector-preview">
        @if (IsImage)
        {
            <img src="tagium://preview/@File.Id" alt="@File.Name" class="preview-media" />
        }
        else if (IsVideo)
        {
            <video src="tagium://preview/@File.Id" controls class="preview-media"></video>
        }
        else
        {
            <div class="generic-file-preview">
                <i class="fa-solid fa-file"></i>
            </div>
        }
    </div>

    <TagInspectorPanel FileIds="_fileIds" />

    <div class="inspector-actions">
        <button class="btn btn-secondary" @onclick="OpenFile">Open File</button>
        <button class="btn btn-secondary" @onclick="ShowInExplorer">Show in Explorer</button>
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class SingleFileInspector
{
    [Inject] private SystemLauncherService Launcher { get; set; } = default!;

    [Parameter, EditorRequired] public required FileEntry File { get; set; }

    private Guid[] _fileIds = [];

    private bool IsImage => CheckExtension([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"]);
    private bool IsVideo => CheckExtension([".mp4", ".webm", ".mov", ".mkv"]);

    protected override void OnParametersSet()
    {
        _fileIds = [File.Id];
    }

    private bool CheckExtension(string[] exts)
    {
        string ext = Path.GetExtension(File.Path);
        return exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void OpenFile()
    {
        Launcher.OpenFile(File.Path);
    }

    private void ShowInExplorer()
    {
        Launcher.ShowInExplorer(File.Path);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor.css`

```css
.single-file-inspector {
    display: flex;
    flex-direction: column;
    gap: var(--space-sm);
    height: 100%;
    overflow-y: auto;
    padding: var(--space-sm);
    box-sizing: border-box;
    width: 100%;
    min-width: 0;
}

.inspector-file-header {
    overflow-wrap: break-word;
    word-break: break-all;
    flex-shrink: 0;
}

.file-name {
    margin: 0 0 var(--space-2xs) 0;
    font-size: 1.1em;
    line-height: 1.3;
}

.file-path {
    margin: 0;
    font-size: var(--font-size-xs);
    color: var(--text-muted);
}

.inspector-preview {
    position: relative;
    display: block;
    height: 200px;
    background-color: #101010;
    border-radius: var(--radius-md);
    overflow: hidden;
    border: 1px solid var(--border-color);
    flex-shrink: 0;
    width: 100%;
    box-sizing: border-box;
}

.preview-media {
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    object-fit: contain;
    display: block;
}

.generic-file-preview {
    font-size: 48px;
    color: var(--text-muted);
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    width: 100%;
}

.inspector-actions {
    display: flex;
    flex-direction: column;
    gap: var(--space-xs);
    margin-top: auto;
    padding-top: var(--space-sm);
    border-top: 1px solid var(--border-color);
    flex-shrink: 0;
}

.btn {
    width: 100%;
    padding: var(--space-2xs) var(--space-xs);
    background-color: var(--surface-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    color: var(--text-color);
    cursor: pointer;
    text-align: center;
    font-size: var(--font-size-sm);
    line-height: 1.2;
    transition: background-color var(--transition-fast), border-color var(--transition-fast);
}

    .btn:hover {
        background-color: var(--surface-hover);
        border-color: var(--text-muted);
    }
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\TagInspectorPanel.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@using Tagium.Photino.Blazor.Components.Shared

<div class="tag-inspector-panel">
    <div class="inspector-tags">
        <h4 class="section-title">Tags</h4>
        <div class="tag-pills">
            @foreach (var tag in _activeTags)
            {
                <span class="tag-pill" style="background-color: @tag.ColorHex">
                    @if (!string.IsNullOrEmpty(tag.Emoji))
                    {
                        <span class="pill-emoji">@tag.Emoji</span>
                    }
                    <span class="pill-text">@tag.Name</span>
                    <button class="btn-remove-tag" @onclick="() => RemoveTag(tag.Id)">
                        <i class="fa-solid fa-xmark"></i>
                    </button>
                </span>
            }
            @if (_activeTags.Count == 0)
            {
                <span class="empty-tag">No tags assigned.</span>
            }
        </div>

        @if (_availableTags.Count > 0 || true)
        {
            <div class="add-tag-section">
                <TagSelector AvailableTags="_availableTags" OnTagSelected="OnTagSelected" OnTagCreated="OnTagCreated" />
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\TagInspectorPanel.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class TagInspectorPanel : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;

    [Parameter, EditorRequired] public required IEnumerable<Guid> FileIds { get; set; }

    private List<Tag> _activeTags = [];
    private List<Tag> _availableTags = [];
    private List<Guid> _fileIdsList = [];

    protected override void OnInitialized()
    {
        Library.LibraryChanged += OnLibraryChanged;
    }

    protected override void OnParametersSet()
    {
        _fileIdsList = FileIds.ToList();
        LoadTags();
    }

    private void LoadTags()
    {
        if (!_fileIdsList.Any())
        {
            _activeTags = [];
            _availableTags = [];
            return;
        }

        List<Tag> allTags = Library.GetTags();

        if (_fileIdsList.Count == 1)
        {
            FileEntry? file = Library.GetFileWithTagsById(_fileIdsList[0]);
            _activeTags = file?.Tags ?? [];
        }
        else
        {
            _activeTags = Library.GetCommonTags(_fileIdsList);
        }

        var activeIds = _activeTags.Select(t => t.Id).ToHashSet();
        _availableTags = allTags.Where(t => !activeIds.Contains(t.Id)).ToList();
    }

    private void OnLibraryChanged()
    {
        _ = InvokeAsync(() =>
        {
            LoadTags();
            StateHasChanged();
        });
    }

    private void OnTagSelected(Guid tagId)
    {
        if (_fileIdsList.Count == 1)
        {
            Library.AddTagToFile(_fileIdsList[0], tagId);
        }
        else
        {
            Library.AddTagToFiles(_fileIdsList, tagId);
        }
    }

    private void OnTagCreated(string tagName)
    {
        string normalized = tagName.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        Library.AddTag(normalized);
        Guid? id = TagRepo.ResolveTagId(normalized);
        if (id.HasValue)
        {
            if (_fileIdsList.Count == 1)
            {
                Library.AddTagToFile(_fileIdsList[0], id.Value);
            }
            else
            {
                Library.AddTagToFiles(_fileIdsList, id.Value);
            }
        }
    }

    private void RemoveTag(Guid tagId)
    {
        if (_fileIdsList.Count == 1)
        {
            Library.RemoveTagFromFile(_fileIdsList[0], tagId);
        }
        else
        {
            Library.RemoveTagFromFiles(_fileIdsList, tagId);
        }
    }

    public void Dispose()
    {
        Library.LibraryChanged -= OnLibraryChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\TagInspectorPanel.razor.css`

```css
.tag-inspector-panel {
    display: flex;
    flex-direction: column;
    width: 100%;
    min-width: 0;
}

.inspector-tags {
    display: flex;
    flex-direction: column;
    gap: var(--space-xs);
    flex-shrink: 0;
}

.section-title {
    margin: 0;
    font-size: var(--font-size-xs);
    text-transform: uppercase;
    letter-spacing: 0.5px;
    color: var(--text-muted);
}

.tag-pills {
    display: flex;
    flex-wrap: wrap;
    gap: var(--space-2xs);
}

.tag-pill {
    display: inline-flex;
    align-items: center;
    gap: var(--space-xs);
    padding: var(--space-2xs) var(--space-xs);
    border-radius: var(--radius-sm);
    font-size: var(--font-size-xs);
    color: white;
}

.pill-emoji {
    display: inline-block;
    line-height: 1;
}

.pill-text {
    line-height: 1;
}

.btn-remove-tag {
    background: none;
    border: none;
    color: rgba(255, 255, 255, 0.6);
    cursor: pointer;
    padding: 0 2px;
    font-size: var(--font-size-xs);
    margin-left: var(--space-2xs);
    line-height: 1;
    transition: color var(--transition-fast);
}

    .btn-remove-tag:hover {
        color: var(--danger-color) !important;
    }

.empty-tag {
    color: var(--text-muted);
    font-style: italic;
    font-size: var(--font-size-sm);
}

.add-tag-section {
    margin-top: var(--space-2xs);
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@inject ModalService ModalService

<div class="duplicate-modal">
    <p>A file with the same content already exists in your library:</p>

    <div class="file-path">@ExistingFile.Path</div>

    <div class="footer">
        <button class="btn btn-primary" @onclick="ShowExisting">Show Existing File</button>
        <button class="btn" @onclick="Close">Close</button>
    </div>
</div>

```

---

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class DuplicateFileModal
{
    [Parameter, EditorRequired]
    public required FileEntry ExistingFile { get; set; }

    private void ShowExisting()
    {
        AppState.SetSearch("");
        AppState.SetMissingOnly(false);
        AppState.SelectFile(ExistingFile.Id, false, false);

        ModalService.Close();
    }

    private void Close()
    {
        ModalService.Close();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\DuplicateFileModal.razor.css`

```css
.duplicate-modal p {
        color: #eee;
        margin-top: 0;
    }

    .file-path {
        background: #222;
        padding: 10px;
        border: 1px solid #444;
        border-radius: 4px;
        font-family: monospace;
        word-break: break-all;
        margin: 15px 0;
        color: #aaa;
    }

    .footer {
        display: flex;
        justify-content: flex-end;
        gap: 10px;
        margin-top: 20px;
    }

    .btn {
        padding: 6px 15px;
        border-radius: 4px;
        border: none;
        cursor: pointer;
        background: #444;
        color: white;
    }

        .btn:hover {
            background: #555;
        }

    .btn-primary {
        background: #42a5f5;
    }

        .btn-primary:hover {
            background: #1e88e5;
        }
```

---

### `Tagium.Photino.Blazor\Components\Modals\SettingsModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Settings

<div class="settings-modal-layout">
    <!-- Left Navigation Column -->
    <div class="settings-sidebar">
        <button class="tab-btn @(ActiveTab == SettingsTab.Folders ? "active" : "")" @onclick="() => SetTab(SettingsTab.Folders)">
            <i class="fa-solid fa-folder-open fa-fw"></i> Library Folders
        </button>
        <button class="tab-btn @(ActiveTab == SettingsTab.Appearance ? "active" : "")" @onclick="() => SetTab(SettingsTab.Appearance)">
            <i class="fa-solid fa-palette fa-fw"></i> Appearance
        </button>
        <button class="tab-btn @(ActiveTab == SettingsTab.Data ? "active" : "")" @onclick="() => SetTab(SettingsTab.Data)">
            <i class="fa-solid fa-database fa-fw"></i> Maintenance
        </button>
    </div>

    <!-- Right Dynamic Settings View -->
    <div class="settings-content">
        @if (ActiveTab == SettingsTab.Folders)
        {
            <div class="settings-panel">
                <div class="panel-header">
                    <h4>Library Folders</h4>
                    <p>Configure sync roots. Supported image and video formats in these folders will automatically be indexed.</p>
                </div>

                <div class="actions">
                    <button class="btn btn-primary" @onclick="AddFolder">Add Folder...</button>
                    <button class="btn" @onclick="TriggerRescan" disabled="@ScanService.IsScanning">
                        @if (ScanService.IsScanning)
                        {
                            <span>Scanning...</span>
                        }
                        else
                        {
                            <span>Rescan All</span>
                        }
                    </button>
                </div>

                @if (ScanService.IsScanning)
                {
                    <div class="scanning-status">
                        <i class="fa-solid fa-circle-notch fa-spin"></i>
                        <span>Scanning files (@ScanService.ProgressCurrent / @ScanService.ProgressTotal)...</span>
                    </div>
                }

                <div class="roots-list">
                    @if (Roots.Count == 0)
                    {
                        <div class="empty-state">No library folders configured. Add a folder to start indexing.</div>
                    }
                    else
                    {
                        @foreach (var root in Roots)
                        {
                            <div class="root-item @(root.IsEnabled ? "" : "disabled")">
                                <div class="root-info">
                                    <span class="root-path" title="@root.Path">@root.Path</span>
                                    <span class="root-meta">
                                        @if (root.LastScanTime.HasValue)
                                        {
                                            <span>Last scanned: @FormatDate(root.LastScanTime.Value)</span>
                                        }
                                        else
                                        {
                                            <span>Never scanned</span>
                                        }
                                    </span>
                                </div>
                                <div class="root-actions">
                                    <label class="switch" title="@(root.IsEnabled ? "Disable folder" : "Enable folder")">
                                        <input type="checkbox" checked="@root.IsEnabled" @onchange="() => ToggleRoot(root)" />
                                        <span class="slider"></span>
                                    </label>
                                    <button class="btn-danger-icon" @onclick="() => RemoveRoot(root)" title="Remove folder">
                                        <i class="fa-solid fa-trash-can"></i>
                                    </button>
                                </div>
                            </div>
                        }
                    }
                </div>
            </div>
        }
        else if (ActiveTab == SettingsTab.Appearance)
        {
            <div class="settings-panel">
                <div class="panel-header">
                    <h4>Appearance & Interface</h4>
                    <p>Tweak how visual elements display inside your workspaces.</p>
                </div>

                <div class="appearance-section">
                    <h5>Application Theme</h5>
                    <p class="section-hint">Select a dynamic JSON theme loaded from your local AppData folder.</p>
                    <select class="theme-select" value="@Settings.Current.SelectedTheme" @onchange="OnThemeChanged">
                        <option value="Default">Default (Dark charcoal)</option>
                        @foreach (var theme in ThemeService.LoadedThemes)
                        {
                            <option value="@theme.Name">@theme.Name</option>
                        }
                    </select>
                </div>

                <hr />

                <div class="appearance-section">
                    <h5>Interface Zoom Level</h5>
                    <p class="section-hint">Adjust the global scaling and size of interface text and elements.</p>
                    <select class="theme-select" value="@Settings.Current.Zoom" @onchange="OnZoomChanged">
                        <option value="50">50% (Smallest)</option>
                        <option value="67">67%</option>
                        <option value="75">75% (Small)</option>
                        <option value="90">90%</option>
                        <option value="100">100% (Default)</option>
                        <option value="110">110%</option>
                        <option value="120">120%</option>
                        <option value="133">133%</option>
                        <option value="150">150% (Large)</option>
                        <option value="175">175%</option>
                        <option value="200">200% (Largest)</option>
                    </select>
                </div>

                <hr />

                <div class="appearance-section">
                    <h5>Tag Sidebar Style</h5>
                    <div class="radio-group">
                        <label>
                            <input type="radio" name="tagSidebarStyle"
                                   checked="@(Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.Pill)"
                                   @onchange="@(() => UpdateTagSidebarStyle(TagSidebarDisplayMode.Pill))" />
                            Colored Pills
                        </label>
                        <label>
                            <input type="radio" name="tagSidebarStyle"
                                   checked="@(Settings.Current.TagSidebarStyle == TagSidebarDisplayMode.MinimalCircle)"
                                   @onchange="@(() => UpdateTagSidebarStyle(TagSidebarDisplayMode.MinimalCircle))" />
                            Minimal Circles
                        </label>
                    </div>
                </div>

                <hr />

                <div class="appearance-section">
                    <h5>Emoji Set Preference</h5>
                    <p class="section-hint">Requires application restart to fully apply font adjustments.</p>
                    <div class="radio-group">
                        <label>
                            <input type="radio" name="emojiSet"
                                   checked="@(Settings.Current.EmojiPreference == EmojiSet.SystemDefault)"
                                   @onchange="@(() => UpdateEmojiSet(EmojiSet.SystemDefault))" />
                            System Default (Windows/Segoe)
                        </label>
                        <label>
                            <input type="radio" name="emojiSet"
                                   checked="@(Settings.Current.EmojiPreference == EmojiSet.Apple)"
                                   @onchange="@(() => UpdateEmojiSet(EmojiSet.Apple))" />
                            Apple Color Emoji
                        </label>
                    </div>
                </div>
            </div>
        }
        else if (ActiveTab == SettingsTab.Data)
        {
            <div class="settings-panel">
                <div class="panel-header">
                    <h4>Maintenance & Database Tools</h4>
                    <p>Integrity diagnostic scanning, data migration imports, and catalog backups.</p>
                </div>

                <div class="maintenance-group">
                    <h5>Integrity Diagnostics</h5>
                    <p class="section-hint">Scan library references to clean up missing directory files.</p>
                    <div class="btn-row">
                        <button class="btn" @onclick="ScanIntegrity">Scan Missing References</button>
                        <button class="btn btn-danger" @onclick="PurgeMissing" disabled="@(Library.GetFiles(null, true).Count == 0)">Purge Missing</button>
                    </div>

                    @if (ScanResult is not null)
                    {
                        <div class="result-box">
                            Scanned: @ScanResult.FilesScanned <br />
                            Missing: @ScanResult.NewlyMissing <br />
                            Found: @ScanResult.NewlyFound
                        </div>
                    }
                </div>

                <hr />

                <div class="maintenance-group">
                    <h5>Export & Backups</h5>
                    <div class="btn-row">
                        <button class="btn" @onclick="ExportJson">Export JSON</button>
                        <button class="btn" @onclick="ExportCsv">Export CSV</button>
                        <button class="btn" @onclick="BackupDb">Backup DB</button>
                    </div>
                </div>

                <hr />

                <div class="maintenance-group">
                    <h5>Database Merge & Restore</h5>
                    <p class="section-hint">Combine indices from an alternate database file, or run a backup recovery.</p>
                    <div class="btn-row">
                        <button class="btn" @onclick="MergeDb">Merge Alternate...</button>
                        <button class="btn btn-danger" @onclick="RestoreDb">Restore Backup...</button>
                    </div>

                    @if (MergeResult is not null)
                    {
                        <div class="result-box">
                            New Files: @MergeResult.NewFiles <br />
                            New Tags: @MergeResult.NewTags <br />
                            Associations: @MergeResult.NewAssociations
                        </div>
                    }
                </div>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Modals\SettingsModal.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Services;
using Tagium.Core.Services.DataExport;
using Tagium.Core.Services.DataMerge;
using Tagium.Core.Services.LibraryIntegrity;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;
using Tagium.Photino.Blazor.Services.Toasting;

namespace Tagium.Photino.Blazor.Components.Modals;

public partial class SettingsModal : IDisposable
{
    [Inject] private LibraryService Library { get; set; } = default!;
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private SettingsManager Settings { get; set; } = default!;
    [Inject] private LibraryIntegrityService IntegrityService { get; set; } = default!;
    [Inject] private DataExportService ExportService { get; set; } = default!;
    [Inject] private DatabaseMergeService MergeService { get; set; } = default!;
    [Inject] private DatabaseManager DbManager { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;
    [Inject] private ModalService ModalService { get; set; } = default!;
    [Inject] private ThemeService ThemeService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;

    private enum SettingsTab { Folders, Appearance, Data }
    private SettingsTab ActiveTab { get; set; } = SettingsTab.Folders;

    private List<LibraryRoot> Roots = [];
    private IntegrityScanResult? ScanResult;
    private MergeResult? MergeResult;

    protected override void OnInitialized()
    {
        LoadRoots();
        ScanService.ScanProgress += OnScanProgress;
        ScanService.ScanCompleted += OnScanCompleted;
        Settings.SettingsChanged += OnSettingsChanged;
    }

    private void SetTab(SettingsTab tab)
    {
        ActiveTab = tab;
    }

    private void OnSettingsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    // --- Library Folders Methods ---
    private void LoadRoots()
    {
        Roots = Library.GetRoots();
    }

    private void AddFolder()
    {
        string? folder = FileDialogService.PickFolder();
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        Library.AddRoot(folder);
        LoadRoots();
        _ = Task.Run(() => Library.ScanAsync());
    }

    private void ToggleRoot(LibraryRoot root)
    {
        root.IsEnabled = !root.IsEnabled;
        Library.UpdateRoot(root);
        LoadRoots();
    }

    private void RemoveRoot(LibraryRoot root)
    {
        Library.DeleteRoot(root.Id, root.Path);
        LoadRoots();
    }

    private async Task TriggerRescan()
    {
        await Task.Run(() => Library.ScanAsync());
    }

    private void OnScanProgress(int current, int total)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnScanCompleted()
    {
        _ = InvokeAsync(() =>
        {
            LoadRoots();
            StateHasChanged();
        });
    }

    private static string FormatDate(long unixTimestamp)
    {
        DateTime dateTime = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).LocalDateTime;
        return dateTime.ToString("g");
    }

    // --- Appearance Methods ---
    private void UpdateTagSidebarStyle(TagSidebarDisplayMode mode)
    {
        Settings.Update(s => s.TagSidebarStyle = mode);
    }

    private void UpdateEmojiSet(EmojiSet set)
    {
        Settings.Update(s => s.EmojiPreference = set);
    }

    private async Task OnThemeChanged(ChangeEventArgs e)
    {
        string? themeName = e.Value?.ToString();
        if (string.IsNullOrEmpty(themeName))
        {
            return;
        }

        Settings.Update(s => s.SelectedTheme = themeName);
        await ThemeService.ApplyThemeAsync(JS, themeName);
    }

    private void OnZoomChanged(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int zoom))
        {
            Settings.Update(s => s.Zoom = zoom);
            Program.MainWindow?.SetZoom(zoom);
        }
    }

    // --- Maintenance (Data Management) Methods ---
    private async Task ScanIntegrity()
    {
        ScanResult = null;
        ScanResult = await IntegrityService.ScanAsync();
        Library.Refresh();
    }

    private void PurgeMissing()
    {
        IntegrityService.PurgeMissing();
        Library.Refresh();
        ScanResult = null;
        Toast.Show("Missing files purged.", ToastType.Success);
    }

    private async Task ExportJson()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.json");
        await ExportService.ExportJsonAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private async Task ExportCsv()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.csv");
        await ExportService.ExportCsvAsync(path);
        Toast.Show($"Exported to {path}", ToastType.Success);
    }

    private void BackupDb()
    {
        string source = DbManager.LibraryDbPath;
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        File.Copy(source, dest, true);
        Toast.Show($"Library configuration backup saved to {dest}", ToastType.Success);
    }

    private void MergeDb()
    {
        string[]? files = FileDialogService.OpenFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        MergeResult = MergeService.Merge(files[0]);
        Library.Refresh();
        Toast.Show("Merge complete.", ToastType.Success);
    }

    private void RestoreDb()
    {
        string[]? files = FileDialogService.OpenFiles();
        if (files is null || files.Length == 0)
        {
            return;
        }

        string source = files[0];
        string dest = DbManager.LibraryDbPath;

        try
        {
            File.Copy(source, dest, true);
            Toast.Show("Library database restored. Please restart Tagium.", ToastType.Success);
        }
        catch (Exception ex)
        {
            Toast.Show($"Restore failed: {ex.Message}", ToastType.Error);
        }
    }

    public void Dispose()
    {
        ScanService.ScanProgress -= OnScanProgress;
        ScanService.ScanCompleted -= OnScanCompleted;
        Settings.SettingsChanged -= OnSettingsChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Modals\SettingsModal.razor.css`

```css
.settings-modal-layout {
    display: flex;
    height: 380px;
    box-sizing: border-box;
}

.settings-sidebar {
    width: 170px;
    display: flex;
    flex-direction: column;
    gap: var(--space-2xs);
    padding-right: var(--space-sm);
    border-right: 1px solid var(--border-color);
    flex-shrink: 0;
}

.settings-content {
    flex: 1;
    padding-left: var(--space-sm);
    overflow-y: auto;
    min-width: 0;
}

/* Sidebar Tab buttons */
.tab-btn {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-muted);
    padding: 6px var(--space-sm);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: var(--font-size-sm);
    font-weight: 500;
    text-align: left;
    display: flex;
    align-items: center;
    gap: var(--space-xs);
    transition: background-color var(--transition-fast), color var(--transition-fast);
}

    .tab-btn:hover {
        background-color: var(--surface-hover);
        color: var(--text-color);
    }

    .tab-btn.active {
        background-color: var(--surface-bg);
        color: var(--primary-color);
        border-color: var(--border-color);
    }

    /* Tab Icon Semantic Color Rules */
    .tab-btn i {
        transition: color var(--transition-fast);
    }

        .tab-btn i.fa-folder-open {
            color: var(--folder-color);
        }

        .tab-btn i.fa-palette {
            color: var(--appearance-color);
        }

        .tab-btn i.fa-database {
            color: var(--primary-color);
        }

    /* Force standard highlighted text-color synchronization when tab is active */
    .tab-btn.active i {
        color: var(--primary-color) !important;
    }

/* Panels shared styles */
.settings-panel {
    display: flex;
    flex-direction: column;
    gap: var(--space-sm);
}

.panel-header {
    display: flex;
    flex-direction: column;
    gap: var(--space-2xs);
}

    .panel-header h4 {
        margin: 0;
        color: var(--text-color);
        font-size: 1.1em;
    }

    .panel-header p {
        margin: 0;
        font-size: var(--font-size-sm);
        color: var(--text-muted);
        line-height: 1.4;
    }

/* Secondary Headers */
h5 {
    margin: 0 0 var(--space-2xs) 0;
    font-size: var(--font-size-sm);
    font-weight: 600;
    color: var(--text-color);
}

.section-hint {
    margin: -2px 0 var(--space-xs) 0;
    font-size: var(--font-size-xs);
    color: var(--text-muted);
    line-height: 1.3;
}

/* Dropdown Menu */
.theme-select {
    width: 100%;
    margin-top: var(--space-2xs);
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: var(--space-2xs) var(--space-xs);
    border-radius: var(--radius-sm);
    outline: none;
    font-size: var(--font-size-sm);
    line-height: 1.2;
    transition: border-color var(--transition-fast);
}

    .theme-select:focus {
        border-color: var(--primary-color);
    }

/* Radio groups */
.radio-group {
    display: flex;
    flex-direction: column;
    gap: var(--space-xs);
    margin-top: var(--space-2xs);
}

    .radio-group label {
        display: flex;
        align-items: center;
        gap: var(--space-xs);
        cursor: pointer;
        font-size: var(--font-size-sm);
    }

/* Folder List elements */
.actions {
    display: flex;
    gap: var(--space-xs);
}

.scanning-status {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
    color: var(--primary-color);
    font-size: var(--font-size-sm);
    padding: var(--space-xs);
    background-color: var(--surface-hover);
    border-radius: var(--radius-sm);
}

.roots-list {
    display: flex;
    flex-direction: column;
    gap: var(--space-xs);
    max-height: 200px;
    overflow-y: auto;
}

.root-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: var(--space-xs) var(--space-sm);
    background: var(--surface-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-md);
    transition: opacity var(--transition-fast);
}

    .root-item.disabled {
        opacity: 0.5;
    }

.root-info {
    display: flex;
    flex-direction: column;
    min-width: 0;
}

.root-path {
    font-size: var(--font-size-sm);
    font-weight: 500;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.root-meta {
    font-size: var(--font-size-xs);
    color: var(--text-muted);
}

.root-actions {
    display: flex;
    align-items: center;
    gap: var(--space-sm);
}

/* Switches (Refined 30x16 desktop-grade switch selector) */
.switch {
    position: relative;
    display: inline-block;
    width: 30px;
    height: 16px;
}

    .switch input {
        opacity: 0;
        width: 0;
        height: 0;
    }

.slider {
    position: absolute;
    cursor: pointer;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background-color: var(--bg-color);
    border: 1px solid var(--border-color);
    transition: var(--transition-normal);
    border-radius: 20px;
}

    .slider:before {
        position: absolute;
        content: "";
        height: 10px;
        width: 10px;
        left: 2px;
        bottom: 2px;
        background-color: var(--text-muted);
        transition: var(--transition-normal);
        border-radius: 50%;
    }

input:checked + .slider {
    background-color: var(--primary-color);
    border-color: var(--primary-color);
}

    input:checked + .slider:before {
        transform: translateX(14px);
        background-color: var(--text-on-primary);
    }

.btn-danger-icon {
    background: transparent;
    border: none;
    color: var(--text-muted);
    cursor: pointer;
    padding: 4px;
    border-radius: var(--radius-sm);
    transition: background-color var(--transition-fast);
}

    .btn-danger-icon i {
        color: var(--text-muted);
        transition: color var(--transition-fast);
    }

    .btn-danger-icon:hover {
        background-color: var(--surface-hover);
    }

        .btn-danger-icon:hover i {
            color: var(--danger-color);
        }

/* Action button configurations */
.btn {
    padding: var(--space-2xs) var(--space-xs);
    border-radius: var(--radius-sm);
    font-size: var(--font-size-sm);
    cursor: pointer;
    font-weight: 500;
    border: 1px solid var(--border-color);
    background-color: var(--surface-bg);
    color: var(--text-color);
    line-height: 1.2;
    transition: all var(--transition-fast);
}

.btn-primary {
    background-color: var(--primary-color);
    border-color: var(--primary-color);
    color: var(--text-on-primary);
}

    .btn-primary:hover {
        background-color: var(--primary-hover);
        border-color: var(--primary-hover);
    }

.btn:hover {
    background-color: var(--surface-hover);
    border-color: var(--text-muted);
}

.btn-danger {
    background-color: var(--danger-color);
    border-color: var(--danger-hover);
    color: white;
}

    .btn-danger:hover {
        background-color: var(--danger-hover);
    }

.btn-row {
    display: flex;
    gap: var(--space-xs);
}

.result-box {
    padding: var(--space-xs);
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    font-size: var(--font-size-sm);
    color: var(--text-muted);
    margin-top: var(--space-xs);
}

hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: var(--space-sm) 0;
    width: 100%;
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared

<button class="icon-button @Class"
        @onclick="OnClick"
        title="@Title"
        aria-label="@Title"
        disabled="@Disabled">
    @ChildContent
</button>
```

---

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class IconButton
{
    [Parameter, EditorRequired] public RenderFragment? ChildContent { get; set; }
    [Parameter, EditorRequired] public string Title { get; set; } = "";
    [Parameter] public string Class { get; set; } = "";
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public EventCallback OnClick { get; set; }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\IconButton.razor.css`

```css
.icon-button {
    background: transparent;
    border: none;
    color: var(--text-muted);
    font-size: 14px;
    width: var(--toolbar-icon-size);
    height: var(--toolbar-icon-size);
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 3px;
    cursor: pointer;
    transition: color 0.1s, background-color 0.1s;
}

    .icon-button:hover {
        color: var(--text-color);
        background: var(--surface-hover);
    }

    .icon-button:disabled {
        opacity: 0.5;
        cursor: not-allowed;
    }
```

---

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@implements IDisposable

<input type="text"
       class="search-input"
       placeholder="Search files..."
       value="@_value"
       @oninput="OnInput"
       @ref="_inputElement"
       aria-label="Search files" />
```

---

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SearchInput
{
    private CancellationTokenSource? _debounceCts;
    private string _value = "";
    private ElementReference _inputElement;

    private const int DebounceMilliseconds = 300;

    protected override void OnInitialized()
    {
        _value = AppState.SearchText;
    }

    private void OnInput(ChangeEventArgs e)
    {
        _value = e.Value?.ToString() ?? "";

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();

        _ = DebounceSearchAsync(_debounceCts.Token);
    }

    private async Task DebounceSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            AppState.SetSearch(_value);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    public async Task FocusAsync()
    {
        await _inputElement.FocusAsync();
    }

    public void Dispose()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\SearchInput.razor.css`

```css
.search-input {
    width: 100%;
    /* Ensure padding doesn't overflow width */
    box-sizing: border-box;
    /* Visual Styles moved from Toolbar */
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 6px var(--space-sm);
    border-radius: 15px;
    font-size: var(--font-size-sm);
    transition: border-color 0.2s, background-color 0.2s;
}

    .search-input:focus {
        outline: none;
        border-color: var(--primary-color);
        background: #000;
    }

    .search-input::placeholder {
        color: var(--text-muted);
    }
```

---

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SettingsManager Settings

<div class="control-group">
    <select @onchange="OnSortByChange" value="@Settings.Current.FileSortBy" aria-label="Sort by">
        <option value="@SortBy.DateAdded">Date</option>
        <option value="@SortBy.Name">Name</option>
        <option value="@SortBy.Path">Path</option>
    </select>
    <select @onchange="OnSortDirChange" value="@Settings.Current.FileSortDirection" aria-label="Sort direction">
        <option value="@SortDirection.Asc">Asc</option>
        <option value="@SortDirection.Desc">Desc</option>
    </select>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class SortControls
{
    private void OnSortByChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortBy>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortBy = result);
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (!Enum.TryParse<SortDirection>(e.Value?.ToString(), out var result))
        {
            return;
        }

        Settings.Update(s => s.FileSortDirection = result);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\SortControls.razor.css`

```css
.control-group {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
}

select {
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    padding: 2px 6px;
    border-radius: 3px;
    font-size: var(--font-size-sm);
    height: 24px;
    outline: none;
}

    select:hover {
        border-color: #555;
    }
```

---

### `Tagium.Photino.Blazor\Components\Shared\StatusBar.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared

<div class="status-bar-container">
    @if (_scanInProgress)
    {
        <div class="scan-progress-bar">
            <div class="progress-fill" style="width: @(_scanProgress)%"></div>
            <div class="progress-label">Scanning library... @(_scanProcessed) / @(_scanTotal)</div>
        </div>
    }

    @if (_thumbnailQueueActive)
    {
        <div class="scan-progress-bar thumbnail-progress" style="bottom: @(GetThumbnailBottomOffset())px !important;">
            <div class="progress-fill" style="width: @(_thumbnailProgress)%"></div>
            <div class="progress-label">Generating thumbnails... @(_thumbnailProcessed) / @(_thumbnailTotal)</div>
        </div>
    }

    @if (_tagSyncInProgress)
    {
        <div class="scan-progress-bar tag-sync-progress" style="bottom: @(GetTagSyncBottomOffset())px !important;">
            <div class="progress-fill" style="width: @(_tagSyncProgress)%"></div>
            <div class="progress-label">Syncing physical files for renamed tag... @(_tagSyncProcessed) / @(_tagSyncTotal)</div>
        </div>
    }
</div>
```

---

### `Tagium.Photino.Blazor\Components\Shared\StatusBar.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Services;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class StatusBar : IDisposable
{
    [Inject] private LibraryScanService ScanService { get; set; } = default!;
    [Inject] private ThumbnailService ThumbnailService { get; set; } = default!;
    [Inject] private LibraryService Library { get; set; } = default!;

    private bool _scanInProgress;
    private int _scanProcessed;
    private int _scanTotal;
    private double _scanProgress;

    private bool _thumbnailQueueActive;
    private int _thumbnailProcessed;
    private int _thumbnailTotal;
    private double _thumbnailProgress;

    private bool _tagSyncInProgress;
    private int _tagSyncProcessed;
    private int _tagSyncTotal;
    private double _tagSyncProgress;

    protected override void OnInitialized()
    {
        ScanService.ScanProgress += UpdateScanProgress;
        ScanService.ScanCompleted += HideScanProgress;
        ThumbnailService.QueueProgressChanged += UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted += HideThumbnailProgress;
        Library.TagSyncProgress += UpdateTagSyncProgress;
        Library.TagSyncCompleted += HideTagSyncProgress;
    }

    private void UpdateScanProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = true;
            _scanProcessed = current;
            _scanTotal = total;
            _scanProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void HideScanProgress()
    {
        _ = InvokeAsync(() =>
        {
            _scanInProgress = false;
            _scanProcessed = 0;
            _scanTotal = 0;
            _scanProgress = 0;
            StateHasChanged();
        });
    }

    private void UpdateThumbnailProgress(int processed, int total)
    {
        _ = InvokeAsync(() =>
        {
            _thumbnailQueueActive = true;
            _thumbnailProcessed = processed;
            _thumbnailTotal = total;
            _thumbnailProgress = total > 0 ? (double)processed / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void HideThumbnailProgress()
    {
        _ = InvokeAsync(() =>
        {
            _thumbnailQueueActive = false;
            _thumbnailProcessed = 0;
            _thumbnailTotal = 0;
            _thumbnailProgress = 0;
            StateHasChanged();
        });
    }

    private void UpdateTagSyncProgress(int current, int total)
    {
        _ = InvokeAsync(() =>
        {
            _tagSyncInProgress = true;
            _tagSyncProcessed = current;
            _tagSyncTotal = total;
            _tagSyncProgress = total > 0 ? (double)current / total * 100 : 0;
            StateHasChanged();
        });
    }

    private void HideTagSyncProgress()
    {
        _ = InvokeAsync(() =>
        {
            _tagSyncInProgress = false;
            _tagSyncProcessed = 0;
            _tagSyncTotal = 0;
            _tagSyncProgress = 0;
            StateHasChanged();
        });
    }

    private int GetThumbnailBottomOffset()
    {
        return _scanInProgress ? 24 : 0;
    }

    private int GetTagSyncBottomOffset()
    {
        int offset = 0;
        if (_scanInProgress)
        {
            offset += 24;
        }

        if (_thumbnailQueueActive)
        {
            offset += 24;
        }

        return offset;
    }

    public void Dispose()
    {
        ScanService.ScanProgress -= UpdateScanProgress;
        ScanService.ScanCompleted -= HideScanProgress;
        ThumbnailService.QueueProgressChanged -= UpdateThumbnailProgress;
        ThumbnailService.QueueCompleted -= HideThumbnailProgress;
        Library.TagSyncProgress -= UpdateTagSyncProgress;
        Library.TagSyncCompleted -= HideTagSyncProgress;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\StatusBar.razor.css`

```css
.status-bar-container {
    position: relative;
    width: 100%;
}

.scan-progress-bar {
    position: fixed !important;
    bottom: 0 !important;
    left: 0 !important;
    width: 100% !important;
    height: 24px !important;
    background-color: #1a1a1a !important;
    z-index: 9999 !important;
    box-sizing: border-box !important;
}

.progress-fill {
    height: 100% !important;
    background-color: var(--primary-color) !important;
    transition: width 0.15s ease-in-out !important;
}

.progress-label {
    position: absolute !important;
    top: 0 !important;
    left: 16px !important;
    height: 24px !important;
    line-height: 24px !important;
    font-size: var(--font-size-xs) !important;
    color: white !important;
    font-weight: 500 !important;
}

.scan-progress-bar.thumbnail-progress {
    background-color: #222 !important;
}

    .scan-progress-bar.thumbnail-progress .progress-fill {
        background-color: #4caf50 !important;
    }

.scan-progress-bar.tag-sync-progress {
    background-color: #2b222c !important;
}

    .scan-progress-bar.tag-sync-progress .progress-fill {
        background-color: var(--appearance-color) !important;
    }
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Models

@if (ShowIcon)
{
    @if (!string.IsNullOrWhiteSpace(Tag.Emoji))
    {
        <span class="tag-emoji">@Tag.Emoji</span>
    }
    else
    {
        <span class="tag-color" style="background-color: @Tag.ColorHex"></span>
    }
}

```

---

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagIcon
{
    [Parameter, EditorRequired] public required Tag Tag { get; set; }
    [Parameter] public bool ShowIcon { get; set; } = true;
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagIcon.razor.css`

```css
.tag-emoji {
        display: inline-block;
        width: 1.2em;
        text-align: center;
        margin-right: 6px;
        line-height: 1;
        font-size: 1.1em;
    }

    .tag-color {
        display: inline-block;
        width: 8px;
        height: 8px;
        border-radius: 50%;
        margin-right: 8px;
        flex-shrink: 0;
        border: 1px solid rgba(255,255,255,0.1);
    }
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagSelector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Models

<div class="tag-selector-wrapper">
    <input type="text"
           class="tag-selector-input"
           placeholder="Add tag..."
           autocomplete="off"
           @bind="SearchText"
           @bind:event="oninput"
           @onfocus="Open"
           @onblur="Close"
           @onkeydown="HandleKeyDown" />

    @if (_isOpen && (_filteredTags.Count > 0 || ShowCreateOption))
    {
        <div class="tag-selector-dropdown">
            @for (int i = 0; i < _filteredTags.Count; i++)
            {
                var tag = _filteredTags[i];
                int index = i;
                <div class="tag-dropdown-item @(_activeIndex == index ? "active" : "")"
                     @onmousedown="() => SelectTag(tag.Id)"
                     @onmousedown:preventDefault="true"
                     @onmouseover="() => _activeIndex = index">
                    <TagIcon Tag="tag" />
                    <span class="tag-name">@tag.Name</span>
                </div>
            }

            @if (ShowCreateOption)
            {
                <div class="tag-dropdown-item create-item @(_activeIndex == _filteredTags.Count ? "active" : "")"
                     @onmousedown="CreateAndSelectTag"
                     @onmousedown:preventDefault="true"
                     @onmouseover="() => _activeIndex = _filteredTags.Count">
                    <i class="fa-solid fa-plus icon"></i>
                    <span class="tag-name">Create tag "<strong>@_searchText</strong>"</span>
                </div>
            }
        </div>
    }
    else if (_isOpen && _filteredTags.Count == 0 && !ShowCreateOption)
    {
        <div class="tag-selector-dropdown empty">
            <span class="text-muted">No matching tags</span>
        </div>
    }
</div>
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagSelector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Tagium.Core.Data;
using Tagium.Core.Models;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class TagSelector
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TagRepository TagRepo { get; set; } = default!;

    [Parameter, EditorRequired] public List<Tag> AvailableTags { get; set; } = [];
    [Parameter, EditorRequired] public EventCallback<Guid> OnTagSelected { get; set; }
    [Parameter] public EventCallback<string> OnTagCreated { get; set; }

    private List<Tag> _filteredTags = [];
    private List<Tag> _oldAvailableTags = [];
    private int _activeIndex = 0;
    private bool _isOpen;

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                UpdateFilter();
            }
        }
    }

    private bool ShowCreateOption => OnTagCreated.HasDelegate &&
                                     !string.IsNullOrWhiteSpace(_searchText) &&
                                     TagRepo.ResolveTagId(_searchText) == null;

    protected override void OnParametersSet()
    {
        // Only reset the active index and filter if the actual tag list content has changed from the parent
        bool hasChanged = _oldAvailableTags.Count != AvailableTags.Count ||
                          !_oldAvailableTags.Select(t => t.Id).SequenceEqual(AvailableTags.Select(t => t.Id));

        if (hasChanged)
        {
            _oldAvailableTags = AvailableTags.ToList();
            UpdateFilter();
        }
    }

    private void UpdateFilter()
    {
        _filteredTags = string.IsNullOrWhiteSpace(_searchText)
            ? AvailableTags
            : AvailableTags.Where(t => t.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase)).ToList();

        _activeIndex = 0;
    }

    private void Open()
    {
        _isOpen = true;
        UpdateFilter();
    }

    private void Close()
    {
        _isOpen = false;
        _searchText = "";
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (!_isOpen)
        {
            return;
        }

        int maxCount = ShowCreateOption ? _filteredTags.Count + 1 : _filteredTags.Count;
        if (maxCount == 0)
        {
            return;
        }

        if (e.Key is "ArrowDown" or "Down")
        {
            _activeIndex++;
            if (_activeIndex >= maxCount)
            {
                _activeIndex = maxCount - 1;
            }
            await ScrollActiveItemIntoView();
        }
        else if (e.Key is "ArrowUp" or "Up")
        {
            _activeIndex--;
            if (_activeIndex < 0)
            {
                _activeIndex = 0;
            }
            await ScrollActiveItemIntoView();
        }
        else if (e.Key == "Enter")
        {
            if (_activeIndex >= 0 && _activeIndex < _filteredTags.Count)
            {
                await SelectTag(_filteredTags[_activeIndex].Id);
            }
            else if (ShowCreateOption && _activeIndex == _filteredTags.Count)
            {
                await CreateAndSelectTag();
            }
        }
        else if (e.Key == "Escape")
        {
            Close();
        }
    }

    private async Task ScrollActiveItemIntoView()
    {
        try
        {
            await JS.InvokeVoidAsync("eval", "var el = document.querySelector('.tag-dropdown-item.active'); if(el) el.scrollIntoView({block: 'nearest'});");
        }
        catch
        {
            // Gracefully catch evaluation context errors during rapid rendering transitions
        }
    }

    private async Task SelectTag(Guid id)
    {
        _searchText = "";
        await OnTagSelected.InvokeAsync(id);
    }

    private async Task CreateAndSelectTag()
    {
        string textToCreate = _searchText.Trim();
        _searchText = "";
        await OnTagCreated.InvokeAsync(textToCreate);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\TagSelector.razor.css`

```css
.tag-selector-wrapper {
    position: relative;
    width: 100%;
}

.tag-selector-input {
    width: 100%;
    padding: var(--space-2xs) var(--space-xs);
    background-color: var(--bg-color);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    color: var(--text-color);
    font-size: var(--font-size-sm);
    line-height: 1.2;
    outline: none;
    box-sizing: border-box;
    transition: border-color var(--transition-fast);
}

    .tag-selector-input:focus {
        border-color: var(--primary-color);
    }

.tag-selector-dropdown {
    position: absolute;
    top: 100%;
    left: 0;
    right: 0;
    margin-top: 4px;
    background-color: var(--panel-bg);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    max-height: 200px;
    overflow-y: auto;
    z-index: 1000;
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
}

.tag-dropdown-item {
    display: flex;
    align-items: center;
    padding: 6px var(--space-sm);
    cursor: pointer;
    transition: background-color var(--transition-fast), color var(--transition-fast);
}

    .tag-dropdown-item:hover {
        background-color: rgba(255, 255, 255, 0.08);
    }

    .tag-dropdown-item.active {
        background-color: var(--primary-color) !important;
        color: var(--text-on-primary) !important;
    }

        /* Coordinate text colors when active keyboard highlighting is used */
        .tag-dropdown-item.active .tag-name {
            color: var(--text-on-primary) !important;
        }

.tag-name {
    font-size: var(--font-size-sm);
    color: var(--text-color);
}

.tag-dropdown-item.create-item {
    border-top: 1px dashed var(--border-color);
    font-style: italic;
}

    .tag-dropdown-item.create-item .icon {
        color: var(--primary-color);
        margin-right: 8px;
        font-size: var(--font-size-sm);
        transition: color var(--transition-fast);
    }

    .tag-dropdown-item.create-item.active .icon {
        color: var(--text-on-primary) !important;
    }

.tag-selector-dropdown.empty {
    padding: var(--space-xs) var(--space-sm);
    font-size: var(--font-size-sm);
    color: var(--text-muted);
    text-align: center;
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor`

```
@namespace Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SettingsManager Settings

<div class="control-group">
    <input type="range" min="64" max="256" step="16"
           value="@Settings.Current.ThumbnailSize"
           @oninput="OnInput"
           @onchange="OnCommit"
           aria-label="Thumbnail size" />
</div>

```

---

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;

namespace Tagium.Photino.Blazor.Components.Shared;

public partial class ThumbnailSizeSlider
{
    private void OnInput(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out int val))
        {
            return;
        }

        Settings.Update(s => s.ThumbnailSize = val, save: false);
    }

    private void OnCommit(ChangeEventArgs e)
    {
        Settings.Save();
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Shared\ThumbnailSizeSlider.razor.css`

```css
.control-group {
    display: flex;
    align-items: center;
    gap: var(--space-xs);
}

input[type=range] {
    -webkit-appearance: none;
    width: 100px;
    background: transparent;
    margin: 0;
    height: 20px;
    cursor: pointer;
    vertical-align: middle;
}

    input[type=range]::-webkit-slider-runnable-track {
        width: 100%;
        height: 4px;
        cursor: pointer;
        background: #444;
        border-radius: 2px;
    }

    input[type=range]::-webkit-slider-thumb {
        -webkit-appearance: none;
        height: 12px;
        width: 12px;
        border-radius: 50%;
        background: var(--primary-color);
        margin-top: -4px;
        cursor: pointer;
        box-shadow: 0 0 2px rgba(0,0,0,0.5);
        transition: transform 0.1s;
    }

        input[type=range]::-webkit-slider-thumb:hover {
            transform: scale(1.2);
            background: var(--primary-hover);
        }
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor`

```
@namespace Tagium.Photino.Blazor.Components.Views
@using Tagium.Photino.Blazor.Components.Shared
@using Tagium.Core.Settings
@inject SearchInput SearchInput
@inject SettingsManager Settings

<div class="files-view-toolbar">
    <div class="search-container">
        <SearchInput @ref="SearchInput" />
    </div>

    <div class="view-mode-buttons">
        <button class="btn-icon"
                @onclick="() => SetViewMode(Settings.Current.FileViewMode == FileViewMode.Grid ? FileViewMode.List : FileViewMode.Grid)"
                title="@(Settings.Current.FileViewMode == FileViewMode.Grid ? "Switch to List View" : "Switch to Grid View")">
            @if (Settings.Current.FileViewMode == FileViewMode.Grid)
            {
                <i class="fa-solid fa-list"></i>
            }
            else
            {
                <i class="fa-solid fa-table-cells"></i>
            }
        </button>
    </div>

    <div class="view-options-wrapper">
        <button class="btn-icon @(IsMenuOpen ? "active" : "")"
                @onclick="ToggleMenu"
                title="View Options">
            <i class="fa-solid fa-eye"></i>
        </button>

        @if (IsMenuOpen)
        {
            <div class="view-menu-overlay" @onclick="CloseMenu"></div>
            <div class="view-menu-dropdown">
                <div class="menu-section">
                    <label>Sort By</label>
                    <SortControls />
                </div>
                <hr />
                <div class="menu-section">
                    <label>Thumbnail Size</label>
                    <ThumbnailSizeSlider />
                </div>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor.cs`

```csharp
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Components.Shared;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class FilesViewToolbar
{
    private bool IsMenuOpen = false;

    public async Task FocusSearchAsync()
    {
        await SearchInput.FocusAsync();
    }

    private void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
    }

    private void CloseMenu()
    {
        IsMenuOpen = false;
    }

    private void SetViewMode(FileViewMode mode)
    {
        Settings.Update(s => s.FileViewMode = mode);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Views\FilesViewToolbar.razor.css`

```css
.files-view-toolbar {
    display: flex;
    align-items: center;
    padding: var(--space-sm) var(--space-md);
    background: var(--panel-bg);
    border-bottom: 1px solid var(--border-color);
    gap: var(--space-sm);
    flex-shrink: 0;
    position: relative;
    z-index: 20;
}

.search-container {
    flex: 1;
    min-width: 0;
}

.view-options-wrapper {
    position: relative;
    display: flex;
    align-items: center;
}

.btn-icon {
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-muted);
    width: 30px;
    height: 30px;
    border-radius: 4px;
    cursor: pointer;
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 1.2em;
    transition: background 0.1s;
}

    .btn-icon:hover {
        background: var(--surface-hover);
        color: var(--text-color);
    }

    .btn-icon.active {
        background: var(--surface-hover);
        color: var(--primary-color);
        border-color: var(--border-color);
    }

/* Dropdown Menu */
.view-menu-dropdown {
    position: absolute;
    top: 100%;
    right: 0;
    margin-top: 8px;
    background: var(--panel-bg);
    border: 1px solid var(--border-color);
    border-radius: 6px;
    padding: 10px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
    z-index: 1001;
    min-width: 200px;
    animation: fadeIn 0.1s ease-out;
}

.view-menu-overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    z-index: 1000;
}

.menu-section {
    display: flex;
    flex-direction: column;
    gap: 5px;
}

    .menu-section label {
        font-size: 0.8em;
        color: var(--text-muted);
        font-weight: 600;
        text-transform: uppercase;
    }

hr {
    border: 0;
    border-top: 1px solid var(--border-color);
    margin: 10px 0;
}

@keyframes fadeIn {
    from {
        opacity: 0;
        transform: translateY(-5px);
    }

    to {
        opacity: 1;
        transform: translateY(0);
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor`

```
@namespace Tagium.Photino.Blazor.Components.Views
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject SettingsManager Settings

<div class="tag-manager-view">
    <div class="header-row">
        <h3>Manage Tags</h3>
        <div class="add-row">
            <input type="text" @bind="NewTagName" placeholder="New tag name..." @onkeyup="HandleKeyUp" />
            <button class="btn-primary" @onclick="AddTag">Add</button>
        </div>
    </div>

    <div class="sort-controls">
        <select @onchange="OnSortByChange" value="@Settings.Current.TagSortBy">
            <option value="@TagSortBy.Name">Name</option>
            <option value="@TagSortBy.FileCount">File Count</option>
        </select>
        <select @onchange="OnSortDirChange" value="@Settings.Current.TagSortDirection">
            <option value="@SortDirection.Asc">Asc</option>
            <option value="@SortDirection.Desc">Desc</option>
        </select>
    </div>

    <div class="tag-list">
        @foreach (var tag in Tags)
        {
            <div class="tag-row">
                <!-- Color Picker -->
                <div class="color-picker-wrapper">
                    <button class="color-circle"
                            style="background-color: @tag.ColorHex"
                            @onclick="() => ToggleColorPicker(tag.Id)"></button>

                    @if (ActiveColorPickerId == tag.Id)
                    {
                        <div class="color-popup" @onclick:stopPropagation="true">
                            <div class="color-grid">
                                @foreach (var color in Palette)
                                {
                                    <div class="swatch"
                                         style="background-color: @color"
                                         @onclick="() => UpdateTagColor(tag, color)"></div>
                                }
                            </div>
                            <div class="overlay" @onclick="() => ActiveColorPickerId = null"></div>
                        </div>
                    }
                </div>

                <!-- Emoji Input -->
                <input type="text" class="emoji-input"
                       value="@tag.Emoji"
                       @onchange="@(e => UpdateTagEmoji(tag, e.Value?.ToString()))"
                       maxlength="2" />

                <!-- Name Input -->
                <input type="text" class="name-input"
                       value="@tag.Name"
                       @onchange="@(e => UpdateTagName(tag, e.Value?.ToString()))" />

                <span class="count">@tag.FileCount files</span>

                <button class="btn-icon delete" @onclick="() => DeleteTag(tag.Id)" title="Delete">&times;</button>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Photino.Blazor.Components.Views;

public partial class TagManagerView
{
    private List<Tag> Tags = [];
    private string NewTagName = "";
    private Guid? ActiveColorPickerId = null;

    private static readonly List<string> Palette =
    [
        "#e53935",
        "#d81b60",
        "#8e24aa",
        "#5e35b1",
        "#3949ab",
        "#1e88e5",
        "#039be5",
        "#00acc1",
        "#00897b",
        "#43a047",
        "#7cb342",
        "#c0ca33",
        "#fdd835",
        "#ffb300",
        "#fb8c00",
        "#f4511e",
        "#6d4c41",
        "#757575",
        "#546e7a",
        "#FFFFFF",
        "#000000"
    ];

    protected override void OnInitialized()
    {
        RefreshTags();
        Library.LibraryChanged += RefreshData;
        Settings.SettingsChanged += StateHasChanged;
    }

    private void RefreshData()
    {
        _ = InvokeAsync(() =>
        {
            RefreshTags();
            StateHasChanged();
        });
    }

    private void RefreshTags()
    {
        Tags = Library.GetTags(Settings.Current.TagSortBy, Settings.Current.TagSortDirection);
    }

    private void HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key != "Enter")
        {
            return;
        }

        AddTag();
    }

    private void AddTag()
    {
        if (string.IsNullOrWhiteSpace(NewTagName))
        {
            return;
        }

        Library.AddTag(NewTagName);
        NewTagName = "";
    }

    private void DeleteTag(Guid id)
    {
        Library.DeleteTag(id);
    }

    private void UpdateTagName(Tag tag, string? newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName == tag.Name)
        {
            return;
        }

        tag.Name = newName;
        Library.UpdateTag(tag);
    }

    private void UpdateTagEmoji(Tag tag, string? newEmoji)
    {
        tag.Emoji = string.IsNullOrWhiteSpace(newEmoji) ? null : newEmoji;
        Library.UpdateTag(tag);
    }

    private void ToggleColorPicker(Guid id)
    {
        ActiveColorPickerId = ActiveColorPickerId == id ? null : id;
    }

    private void UpdateTagColor(Tag tag, string color)
    {
        tag.ColorHex = color;
        Library.UpdateTag(tag);
        ActiveColorPickerId = null;
    }

    private void OnSortByChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<TagSortBy>(e.Value?.ToString(), out TagSortBy result))
        {
            Settings.Update(s => s.TagSortBy = result);
            RefreshTags();
        }
    }

    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortDirection>(e.Value?.ToString(), out SortDirection result))
        {
            Settings.Update(s => s.TagSortDirection = result);
            RefreshTags();
        }
    }

    public void Dispose()
    {
        Library.LibraryChanged -= RefreshData;
        Settings.SettingsChanged -= StateHasChanged;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\Views\TagManagerView.razor.css`

```css
.tag-manager-view {
    height: 100%;
    display: flex;
    flex-direction: column;
    padding: var(--space-md);
    background: var(--bg-color);
    color: var(--text-color);
}

.header-row {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: var(--space-sm);
    border-bottom: 1px solid var(--border-color);
    padding-bottom: var(--space-sm);
}

    .header-row h3 {
        margin: 0;
        color: var(--text-color);
        font-size: var(--font-size-sm);
    }

.add-row {
    display: flex;
    gap: var(--space-xs);
    align-items: center;
}

    .add-row input {
        height: 26px;
        box-sizing: border-box;
        padding: var(--space-2xs) var(--space-xs);
        background: var(--surface-bg);
        border: 1px solid var(--border-color);
        color: var(--text-color);
        border-radius: var(--radius-sm);
        flex: 1;
        font-size: var(--font-size-sm);
        line-height: 1.2;
        outline: none;
    }

        .add-row input:focus {
            outline: none;
            border-color: var(--primary-color);
        }

.btn-primary {
    height: 26px;
    box-sizing: border-box;
    background: var(--primary-color);
    border: 1px solid transparent;
    color: var(--text-on-primary);
    padding: var(--space-2xs) var(--space-sm);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: var(--font-size-xs);
    font-weight: 500;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    line-height: 1.2;
    transition: background-color var(--transition-fast);
}

    .btn-primary:hover {
        background: var(--primary-hover);
    }

.sort-controls {
    display: flex;
    gap: var(--space-xs);
    margin-bottom: var(--space-sm);
    padding: var(--space-xs);
    background: var(--panel-bg);
    border-radius: var(--radius-sm);
}

    .sort-controls select {
        background: var(--bg-color);
        border: 1px solid var(--border-color);
        color: var(--text-color);
        padding: var(--space-2xs) var(--space-xs);
        border-radius: var(--radius-sm);
        font-size: var(--font-size-sm);
        line-height: 1.2;
        outline: none;
    }

        .sort-controls select:hover {
            border-color: var(--text-muted);
        }

.tag-list {
    flex: 1;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    gap: 2px;
}

.tag-row {
    display: flex;
    align-items: center;
    padding: var(--space-2xs) var(--space-sm);
    background: var(--surface-bg);
    border-radius: var(--radius-sm);
    gap: var(--space-xs);
    transition: background-color var(--transition-fast);
}

    .tag-row:hover {
        background: var(--panel-bg);
    }

.color-picker-wrapper {
    position: relative;
    display: flex;
    align-items: center;
}

.color-circle {
    width: 18px;
    height: 18px;
    border-radius: 50%;
    border: 2px solid var(--border-color);
    cursor: pointer;
    display: block;
}

.color-popup {
    position: absolute;
    top: 25px;
    left: 0;
    background: var(--panel-bg);
    border: 1px solid var(--border-color);
    padding: 6px;
    z-index: 1000;
    border-radius: var(--radius-md);
    width: 140px;
    box-shadow: 0 4px 12px rgba(0,0,0,0.5);
}

.color-grid {
    display: flex;
    flex-wrap: wrap;
    gap: 3px;
    position: relative;
    z-index: 2;
}

.swatch {
    width: 16px;
    height: 16px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    border: 1px solid rgba(255,255,255,0.1);
}

    .swatch:hover {
        border-color: white;
        transform: scale(1.1);
    }

.overlay {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
    z-index: 1;
}

.emoji-input {
    width: 24px;
    text-align: center;
    background: var(--bg-color);
    border: 1px solid var(--border-color);
    color: var(--text-color);
    border-radius: var(--radius-sm);
    padding: 2px 4px;
    font-size: var(--font-size-sm);
    outline: none;
}

    .emoji-input:focus {
        outline: none;
        border-color: var(--primary-color);
    }

.name-input {
    flex: 1;
    background: transparent;
    border: 1px solid transparent;
    color: var(--text-color);
    padding: 3px var(--space-xs);
    border-radius: var(--radius-sm);
    font-size: var(--font-size-sm);
    outline: none;
}

    .name-input:focus {
        background: var(--bg-color);
        border-color: var(--border-color);
    }

.count {
    color: var(--text-muted);
    font-size: var(--font-size-xs);
    width: 60px;
    text-align: right;
}

.btn-icon.delete {
    background: none;
    border: none;
    color: var(--danger-color);
    font-size: 1.2em;
    cursor: pointer;
    padding: 0 var(--space-xs);
    line-height: 1;
    display: flex;
    align-items: center;
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\Toast.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public class Toast
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Message { get; init; }
    public ToastType Type { get; init; }
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\ToastService.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public class ToastService
{
    public event Action? OnChange;
    public List<Toast> Toasts { get; } = [];

    public void Show(string message, ToastType type = ToastType.Info, int durationMs = 3000)
    {
        Toast toast = new()
        {
            Message = message,
            Type = type
        };

        Toasts.Add(toast);
        NotifyStateChanged();

        if (durationMs <= 0)
        {
            return;
        }

        _ = RemoveAfterDelay(toast, durationMs);
    }

    public void Remove(Toast toast)
    {
        if (!Toasts.Remove(toast))
        {
            return;
        }

        NotifyStateChanged();
    }

    private async Task RemoveAfterDelay(Toast toast, int delay)
    {
        await Task.Delay(delay);
        Remove(toast);
    }

    private void NotifyStateChanged()
    {
        OnChange?.Invoke();
    }
}
```

---

### `Tagium.Photino.Blazor\Services\Toasting\ToastType.cs`

```csharp
namespace Tagium.Photino.Blazor.Services.Toasting;

public enum ToastType
{
    Info,
    Success,
    Error
}
```

---

### `Tagium.Photino.Blazor\wwwroot\css\app.css`

```css
:root {
    /* Global Color Palette */
    --bg-color: #181818;
    --panel-bg: #202020;
    --surface-bg: #2D2D2D;
    --border-color: #383838;
    --text-color: #E0E0E0;
    --text-muted: #9E9E9E;
    --primary-color: #42a5f5;
    --primary-hover: #1e88e5;
    --text-on-primary: #121212;
    --danger-color: #d32f2f;
    --danger-hover: #b71c1c;
    /* Semantic Icon Accent Colors */
    --folder-color: #ffca28;
    --warning-color: #ff9800;
    --appearance-color: #ba68c8;
    /* Spacing Scale (8px system) */
    --space-2xs: 4px;
    --space-xs: 8px;
    --space-sm: 12px;
    --space-md: 16px;
    --space-lg: 24px;
    /* Layout Dimensions */
    --toolbar-height: 38px;
    --toolbar-icon-size: 28px;
    --font-size-xs: 11px;
    --font-size-sm: 12px;
    /* Border Radii */
    --radius-sm: 4px;
    --radius-md: 6px;
    --radius-lg: 8px;
    /* Transitions */
    --transition-fast: 0.1s ease-in-out;
    --transition-normal: 0.15s ease-in-out;
    /* Breakpoints */
    --breakpoint-sm: 700px;
}

@font-face {
    font-family: 'Apple Emoji';
    src: url('../fonts/AppleColorEmoji.ttf');
}

body {
    font-family: 'Segoe UI', 'Segoe UI Emoji', sans-serif;
    background-color: var(--bg-color);
    color: var(--text-color);
    margin: 0;
    padding: 0;
    height: 100%;
    overflow: hidden;
}

#app {
    height: 100%;
}

.drag-over::after {
    content: 'Drop files to add';
    position: absolute;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    background: rgba(66, 165, 245, 0.3);
    border: 4px dashed var(--primary-color);
    display: flex;
    justify-content: center;
    align-items: center;
    font-size: 2em;
    color: white;
    text-shadow: 0 2px 4px rgba(0,0,0,0.5);
    z-index: 9999;
    pointer-events: none;
    box-sizing: border-box;
}

.no-select {
    user-select: none;
}

::-webkit-scrollbar {
    width: 12px;
    height: 12px;
}

::-webkit-scrollbar-track {
    background: var(--bg-color);
}

::-webkit-scrollbar-thumb {
    background: var(--border-color);
    border-radius: var(--radius-sm);
    border: 3px solid var(--bg-color);
}

    ::-webkit-scrollbar-thumb:hover {
        background: var(--text-muted);
    }

    ::-webkit-scrollbar-thumb:active {
        background: var(--primary-color);
    }

::-webkit-scrollbar-corner {
    background: var(--bg-color);
}

.import-progress-overlay {
    position: fixed;
    bottom: 60px;
    left: 20px;
    right: 20px;
    height: 4px;
    background: var(--border-color);
    border-radius: var(--radius-sm);
    z-index: 9998;
}

.import-progress-bar {
    height: 100%;
    background: var(--primary-color);
    border-radius: var(--radius-sm);
    transition: width var(--transition-normal);
}

.import-progress-text {
    position: absolute;
    top: -20px;
    left: 0;
    font-size: var(--font-size-xs);
    color: var(--text-muted);
}
```

---

### `Tagium.Photino.Blazor\wwwroot\js\dragdrop.js`

```javascript
window.dragDrop = {
    init: (dotNetHelper) => {
        const dropZone = document.getElementById('app');

        // Prevent default browser actions (like opening the file in the tab)
        ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, preventDefaults, false);
        });

        function preventDefaults(e) {
            e.preventDefault();
            e.stopPropagation();
        }

        // Highlight visual feedback
        ['dragenter', 'dragover'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.add('drag-over'), false);
        });

        ['dragleave', 'drop'].forEach(eventName => {
            dropZone.addEventListener(eventName, () => dropZone.classList.remove('drag-over'), false);
        });

        // We do NOT handle the data transfer here.
        // The C# Native Window Handler catches the files before JS sees them.
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\grid.js`

```javascript
window.gridInteractions = {
    dotNetRef: null,
    containerId: '',
    selectionBox: null,
    isSelecting: false,
    startX: 0,
    startY: 0,

    init: function (dotNetRef, containerId) {
        this.dotNetRef = dotNetRef;
        this.containerId = containerId;

        // Create selection box element if it doesn't exist
        if (!document.getElementById('selection-box')) {
            this.selectionBox = document.createElement('div');
            this.selectionBox.id = 'selection-box';
            this.selectionBox.style.position = 'absolute';
            this.selectionBox.style.border = '1px solid #42a5f5';
            this.selectionBox.style.backgroundColor = 'rgba(66, 165, 245, 0.2)';
            this.selectionBox.style.display = 'none';
            this.selectionBox.style.zIndex = '9999';
            this.selectionBox.style.pointerEvents = 'none';
            document.body.appendChild(this.selectionBox);
        } else {
            this.selectionBox = document.getElementById('selection-box');
        }

        // Attach global events
        document.addEventListener('keydown', this.handleKeyDown.bind(this));

        const container = document.getElementById(containerId);
        if (container) {
            container.addEventListener('mousedown', this.handleMouseDown.bind(this));
        }
    },

    handleKeyDown: function (e) {
        // Ignore if focus is in an input
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;

        if (e.key === 'Delete') {
            this.dotNetRef.invokeMethodAsync('OnDeleteKey');
        } else if (e.key === 'Enter') {
            this.dotNetRef.invokeMethodAsync('OnEnterKey');
        } else if (e.key.startsWith('Arrow')) {
            e.preventDefault();
            this.navigate(e.key);
        } else if (e.key === 'a' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            this.dotNetRef.invokeMethodAsync('SelectAll');
        }
    },

    navigate: function (direction) {
        const items = Array.from(document.querySelectorAll('.file-card'));
        if (items.length === 0) return;

        const selected = document.querySelector('.file-card.selected');
        let index = selected ? items.indexOf(selected) : -1;

        if (index === -1) {
            index = 0; // Default to first
        } else {
            const containerWidth = document.getElementById(this.containerId).clientWidth;
            const itemWidth = items[0].getBoundingClientRect().width + 10; // + gap
            const cols = Math.floor(containerWidth / itemWidth) || 1;

            if (direction === 'ArrowRight') index++;
            else if (direction === 'ArrowLeft') index--;
            else if (direction === 'ArrowDown') index += cols;
            else if (direction === 'ArrowUp') index -= cols;
        }

        // Clamp
        if (index < 0) index = 0;
        if (index >= items.length) index = items.length - 1;

        // Get ID (as string)
        const id = items[index].getAttribute('data-id');
        items[index].scrollIntoView({ block: 'nearest' });

        this.dotNetRef.invokeMethodAsync('SelectFile', id);
    },

    handleMouseDown: function (e) {
        // Ignore clicks on items (handled by Blazor onclick)
        if (e.target.closest('.file-card')) return;

        // Detect if click is on scrollbar
        // If the target is the container itself, check if click is outside client area
        if (e.target.id === this.containerId) {
            const rect = e.target.getBoundingClientRect();
            const isVerticalScrollbar = e.clientX > rect.left + e.target.clientWidth;
            const isHorizontalScrollbar = e.clientY > rect.top + e.target.clientHeight;

            if (isVerticalScrollbar || isHorizontalScrollbar) {
                return;
            }
        }

        e.preventDefault();
        this.isSelecting = true;
        this.startX = e.pageX;
        this.startY = e.pageY;

        this.selectionBox.style.display = 'block';
        this.updateBox(e.pageX, e.pageY);

        document.addEventListener('mousemove', this.boundMouseMove = this.handleMouseMove.bind(this));
        document.addEventListener('mouseup', this.boundMouseUp = this.handleMouseUp.bind(this));
    },

    handleMouseMove: function (e) {
        if (!this.isSelecting) return;
        this.updateBox(e.pageX, e.pageY);
    },

    updateBox: function (currentX, currentY) {
        const left = Math.min(this.startX, currentX);
        const top = Math.min(this.startY, currentY);
        const width = Math.abs(currentX - this.startX);
        const height = Math.abs(currentY - this.startY);

        this.selectionBox.style.left = left + 'px';
        this.selectionBox.style.top = top + 'px';
        this.selectionBox.style.width = width + 'px';
        this.selectionBox.style.height = height + 'px';
    },

    handleMouseUp: function (e) {
        this.isSelecting = false;
        this.selectionBox.style.display = 'none';

        document.removeEventListener('mousemove', this.boundMouseMove);
        document.removeEventListener('mouseup', this.boundMouseUp);

        // Calculate intersection
        const boxRect = {
            left: Math.min(this.startX, e.pageX),
            top: Math.min(this.startY, e.pageY),
            right: Math.max(this.startX, e.pageX),
            bottom: Math.max(this.startY, e.pageY)
        };

        const ids = [];
        document.querySelectorAll('.file-card').forEach(item => {
            const rect = item.getBoundingClientRect();
            // Adjust rect to page coordinates
            const pageRect = {
                left: rect.left + window.scrollX,
                top: rect.top + window.scrollY,
                right: rect.right + window.scrollX,
                bottom: rect.bottom + window.scrollY
            };

            const intersect = !(boxRect.right < pageRect.left ||
                boxRect.left > pageRect.right ||
                boxRect.bottom < pageRect.top ||
                boxRect.top > pageRect.bottom);

            if (intersect) {
                ids.push(item.getAttribute('data-id')); // Keep as string
            }
        });

        if (ids.length > 0) {
            this.dotNetRef.invokeMethodAsync('SelectFiles', ids);
        } else {
            this.dotNetRef.invokeMethodAsync('ClearSelection');
        }
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\layout.js`

```javascript
window.layoutResizer = {
    init: function (dotNetRef, leftPanelId, leftSplitterId, rightPanelId, rightSplitterId) {
        const leftPanel = document.getElementById(leftPanelId);
        const rightPanel = document.getElementById(rightPanelId);
        const leftSplitter = document.getElementById(leftSplitterId);
        const rightSplitter = document.getElementById(rightSplitterId);

        if (leftSplitter && leftPanel) {
            setupResizer(leftSplitter, leftPanel, 'left');
        }

        if (rightSplitter && rightPanel) {
            setupResizer(rightSplitter, rightPanel, 'right');
        }

        function setupResizer(splitter, panel, side) {
            splitter.addEventListener('mousedown', function (e) {
                e.preventDefault();

                splitter.classList.add('active');
                document.body.style.cursor = 'col-resize';
                document.body.classList.add('no-select');

                function onMouseMove(e) {
                    if (side === 'left') {
                        const newWidth = Math.min(Math.max(e.clientX, 150), 600);
                        panel.style.width = newWidth + 'px';
                    } else {
                        const newWidth = Math.min(Math.max(window.innerWidth - e.clientX, 200), 800);
                        panel.style.width = newWidth + 'px';
                    }
                }

                function onMouseUp() {
                    splitter.classList.remove('active');
                    document.body.style.cursor = '';
                    document.body.classList.remove('no-select');

                    document.removeEventListener('mousemove', onMouseMove);
                    document.removeEventListener('mouseup', onMouseUp);

                    const width = parseInt(panel.style.width);
                    if (side === 'left') {
                        dotNetRef.invokeMethodAsync('SaveLeftSidebarWidth', width);
                    } else {
                        dotNetRef.invokeMethodAsync('SaveRightSidebarWidth', width);
                    }
                }

                document.addEventListener('mousemove', onMouseMove);
                document.addEventListener('mouseup', onMouseUp);
            });
        }
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\shortcuts.js`

```javascript
window.globalShortcuts = {
    init: function (dotNetRef) {
        document.addEventListener('keydown', function (e) {
            const isInput = e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA' || e.target.tagName === 'SELECT' || e.target.isContentEditable;

            if (e.ctrlKey || e.metaKey) {
                const key = e.key.toLowerCase();

                // Native zoom shortcuts must be intercepted globally even when typing in input controls
                if (['+', '=', '-', '0'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, e.ctrlKey || e.metaKey, e.shiftKey);
                    return;
                }

                if (isInput) {
                    return;
                }

                if (['z', 'f', 't', 'r'].includes(key)) {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync('HandleGlobalKeyDown', key, e.ctrlKey || e.metaKey, e.shiftKey);
                }
            }
        });

        // Intercept Ctrl + mousewheel scroll zooms globally to prevent native distortion
        document.addEventListener('wheel', function (e) {
            if (e.ctrlKey || e.metaKey) {
                e.preventDefault();
                const direction = e.deltaY < 0 ? 1 : -1;
                dotNetRef.invokeMethodAsync('HandleWheelZoom', direction);
            }
        }, { passive: false });
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\js\zoom.js`

```javascript
window.zoomTracker = {
    dotNetRef: null,
    osScale: 1.0,
    lastZoom: 100,

    init: function (dotNetRef, initialZoomSetting) {
        this.dotNetRef = dotNetRef;
        this.lastZoom = initialZoomSetting;

        const initialRatio = window.devicePixelRatio;
        this.osScale = initialRatio / (initialZoomSetting / 100.0);

        window.addEventListener('resize', this.checkZoom.bind(this));
    },

    checkZoom: function () {
        const currentRatio = window.devicePixelRatio;
        const computedZoom = Math.round((currentRatio / this.osScale) * 100);

        if (Math.abs(computedZoom - this.lastZoom) >= 2) {
            this.lastZoom = computedZoom;
            this.dotNetRef.invokeMethodAsync('OnNativeZoomChanged', computedZoom);
        }
    }
};
```

---

### `Tagium.Photino.Blazor\wwwroot\themes\classic-light.json`

```json
{
  "Name": "Classic Light",
  "Colors": {
    "--bg-color": "#F5F5F5",
    "--panel-bg": "#EAEAEA",
    "--surface-bg": "#FFFFFF",
    "--border-color": "#DCDCDC",
    "--text-color": "#212121",
    "--text-muted": "#666666",
    "--primary-color": "#1976D2",
    "--primary-hover": "#1565C0",
    "--text-on-primary": "#FFFFFF",
    "--danger-color": "#D32F2F",
    "--danger-hover": "#C62828",
    "--folder-color": "#E65100",
    "--warning-color": "#F57C00",
    "--appearance-color": "#8E24AA"
  }
}
```

---

### `Tagium.Photino.Blazor\wwwroot\themes\monokai-pro.json`

```json
{
  "Name": "Monokai Pro",
  "Colors": {
    "--bg-color": "#2d2a2e",
    "--panel-bg": "#221f22",
    "--surface-bg": "#3a373a",
    "--border-color": "#403e41",
    "--text-color": "#fcfcfa",
    "--text-muted": "#939293",
    "--primary-color": "#78dce8",
    "--primary-hover": "#5ecbda",
    "--text-on-primary": "#2d2a2e",
    "--danger-color": "#ff6188",
    "--danger-hover": "#f14e76",
    "--folder-color": "#ffd866",
    "--warning-color": "#fc9867",
    "--appearance-color": "#ab9df2"
  }
}
```

---

### `Tagium.Photino.Blazor\wwwroot\themes\nordic-frost.json`

```json
{
  "Name": "Nordic Frost",
  "Colors": {
    "--bg-color": "#0F141C",
    "--panel-bg": "#161B25",
    "--surface-bg": "#1F2430",
    "--border-color": "#2D323F",
    "--text-color": "#E0E6F0",
    "--text-muted": "#808D9C",
    "--primary-color": "#39ADB5",
    "--primary-hover": "#2E9AA1",
    "--text-on-primary": "#0b1017",
    "--danger-color": "#F07178",
    "--danger-hover": "#E55B62",
    "--folder-color": "#FFB454",
    "--warning-color": "#FF8F40",
    "--appearance-color": "#C249C4"
  }
}
```

---

