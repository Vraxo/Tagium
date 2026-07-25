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

This document serves as the absolute, non-negotiable contract for all visual developments in Tagium. All styles must strictly adhere to the tokens defined here. No "magic numbers" or localized hex codes are allowed.

## 1. Visual Design Philosophy
Tagium is a highly compact, professional, and performance-minded tool. Visual elements must feel snappy, structurally cohesive, and aligned with a strict semantic hierarchy. 

* **No Hex Codes**: Never write raw colors (e.g., `#1a1a1a` or `#333`) in any component or page. Always map colors to semantic custom CSS properties.
* **8px Grid System**: Layouts, padding, margins, and component gaps must be multipliers of the 8px grid. Use spacing custom properties.
* **Minimalist Feedback**: Keep hover states subtle and rounded. Ensure borders are thin (`1px`) and highly contrasting.

---

## 2. Global Semantic Tokens

All CSS variables are declared globally in `app.css`. Use them according to these functional definitions:

### Colors

| Token | CSS Variable | Semantic Purpose |
|---|---|---|
| Background | `var(--bg-color)` | Core app background (dark charcoal) |
| Panel Background | `var(--panel-bg)` | Sidebar and secondary panel background |
| Surface Background | `var(--surface-bg)` | Cards, input fields, and elevated elements |
| Surface Hover | `var(--surface-hover)` | Interactive hover state (semi-transparent white) |
| Border Color | `var(--border-color)` | Structural separation lines, thin borders |
| Text Primary | `var(--text-color)` | Primary readable content |
| Text Muted | `var(--text-muted)` | Supporting descriptors, labels, and timestamps |
| Primary Brand | `var(--primary-color)` | Focus rings, primary buttons, accents |
| Primary Hover | `var(--primary-hover)` | Active primary button hover states |
| Primary Text | `var(--text-on-primary)` | Contrasting text color sitting on primary-colored backgrounds to satisfy WCAG AA standards |
| Danger Accent | `var(--danger-color)` | Destructive buttons, warnings, missing alerts |
| Danger Hover | `var(--danger-hover)` | Active destructive hover states |
| Folder Amber | `var(--folder-color)` | Color token for directories, roots, and folder paths (`#ffca28`) |
| Alert Warning | `var(--warning-color)` | High-visibility alert orange for missing file flags (`#ff9800`) |
| Appearance Accent | `var(--appearance-color)` | Creative accent violet/magenta for theme preferences and palettes (`#ba68c8`) |

### Icon Color States

To maintain clean typography and consistent contrast, follow these color states for vector icons:
* **Inactive System Controls**: Rendered as `var(--text-muted)` by default.
* **System Folders**: Always use `var(--folder-color)` to mimic traditional file explorer elements.
* **Alarms & Warnings**: Always use `var(--warning-color)` for missing elements or alert triangles.
* **Selection Overrides**: When a container gains the active selection state (e.g., a sidebar row becomes selected), colored icons must revert to `color: var(--text-on-primary) !important` to stay readable against high-intensity primary background colors.
* **Interactive Deletions**: Tag-removal buttons, folder-removal trash cans, and delete controls should rest at a muted tone but instantly transition to `var(--danger-color)` on hover.

### Settings Panel Tab Accents

To allow users to navigate configuration panels by rapid visual association, tab icon colors are mapped to their specific domain:
* **Library Folders Tab**: Uses `var(--folder-color)` (amber) representing storage paths.
* **Appearance Tab**: Uses `var(--appearance-color)` (violet/purple) representing design themes and creative customization.
* **Maintenance Tab**: Uses `var(--primary-color)` (steel blue) representing structural utilities and database operations.
* **Active State**: When a tab is selected, its icon color shifts cleanly to `var(--primary-color)` to lock in with the highlighted text style.

### Spacing (8px Scale)

| Token | CSS Variable | Size | Purpose |
|---|---|---|---|
| `space-2xs` | `var(--space-2xs)` | 4px | Micro-paddings, element borders |
| `space-xs` | `var(--space-xs)` | 8px | Grid gap, label-to-input gap, list item vertical padding |
| `space-sm` | `var(--space-sm)` | 12px | Standard component padding, sub-panels |
| `space-md` | `var(--space-md)` | 16px | Workspace borders, grid container margins |
| `space-lg` | `var(--space-lg)` | 24px | Layout spacing, empty state paddings |

### Border Radii

| Token | CSS Variable | Value | Purpose |
|---|---|---|---|
| Small | `var(--radius-sm)` | 4px | Small buttons, badges, tag pills |
| Medium | `var(--radius-md)` | 6px | Input fields, file cards, small containers |
| Large | `var(--radius-lg)` | 8px | Modals, preview windows, dialog frames |

### Transitions

* **Fast Interactive**: `var(--transition-fast)` (0.1s ease-in-out) — hover, background transitions, selections.
* **Standard Interactive**: `var(--transition-normal)` (0.15s ease-in-out) — drawers, modal overlay fades, progress bars.

---

## 3. Component Sizing & Sizing Standards (Compact Desktop Scale)

To preserve high visual density and a standard desktop utility feel, all inputs, selects, and action widgets must strictly adhere to this compact horizontal and vertical scale:

### Utility Buttons, Dropdowns & Inputs
All standard utility buttons, search input fields, and dropdown selectors must sit strictly between **24px and 26px** in height.
* **Padding Formula**: Set vertical padding to `var(--space-2xs)` (4px) and horizontal padding to `var(--space-xs)` (8px) or `var(--space-sm)` (12px).
* **Line Height**: Lock the element's CSS `line-height` to `1.2` or `1.3` to prevent browser rendering engine skew.
* **Dropdown Alignment**: Dropdown select boxes must match the exact same vertical padding and line-height as adjacent buttons to align perfectly on a horizontal plane.

### Hardware-Style Toggle Switches
Slices and toggle elements inside preferences or listing grids are locked to an ultra-sleek, compact hardware profile:
* **Outer Profile**: Width must be exactly `30px` and height exactly `16px`.
* **Visual Slider Thumb**: A perfect circle with a diameter of exactly `10px`.
* **Internal Margins**: Centered with exactly `2px` of clearance on all sides (`left: 2px` and `bottom: 2px` on the thumb element).
* **Animation Transition**: The horizontal slide transition is locked to exactly `translateX(14px)`.
* **Active Thumb Contrast**: The sliding circular thumb's background must map to `var(--text-on-primary)` to ensure high contrast when sliding over light/pastel brand colors.

---

## 4. Mandatory AI Directives
When building, modifying, or refactoring UI components for Tagium:
1. **Never use inline styles (`style="..."`)** for colors, padding, margins, or heights.
2. **Never define new local colors** unless explicitly instructed.
3. **Always keep CSS isolated** to the companion `.razor.css` file where applicable.
4. **Ensure high contrast** and preserve readable dark-theme hierarchy at all times.
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
        width: 100% !important;
        height: calc(var(--thumb-size) + 30px) !important; /* Explicitly lock row height to prevent virtualizer math skewing */
        box-sizing: border-box !important;
        margin-bottom: 16px !important;
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
     style="--thumb-size: @(Settings.Current.ThumbnailSize)px">

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
    private int _columnsCount = 5;
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
    <div class="inspector-header">
        <span class="title">Inspector</span>
        <span class="badge">@AppState.SelectedFileIds.Count selected</span>
    </div>

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
                <p>Select a file to view details</p>
            </div>
        }
        else if (SelectionMode == InspectorMode.Single && SelectedFile is not null)
        {
            <SingleFileInspector File="SelectedFile"
                                 AssignedTags="AssignedTags"
                                 AvailableTags="AvailableTags" />
        }
        else if (SelectionMode == InspectorMode.Multi)
        {
            <MultiFileInspector CommonTags="CommonTags"
                                AvailableTags="AvailableTags" />
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
    private List<Tag> AssignedTags = [];
    private List<Tag> CommonTags = [];
    private List<Tag> AvailableTags = [];
    private List<Tag> _allTagsCache = [];
    private CancellationTokenSource? _cts;

    protected override void OnInitialized()
    {
        LoadAllTags();
        AppState.OnSelectionChange += OnSelectionChanged;
        Library.LibraryChanged += OnLibraryChanged;
        _ = LoadInspectorDataAsync();
    }

    private void LoadAllTags()
    {
        _allTagsCache = Library.GetTags();
    }

    private void OnSelectionChanged()
    {
        _ = LoadInspectorDataAsync();
    }

    private void OnLibraryChanged()
    {
        LoadAllTags(); // Refresh tag cache
        _ = LoadInspectorDataAsync();
    }

    private async Task LoadInspectorDataAsync()
    {
        // Cancel any previous ongoing operation
        _cts?.Cancel();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        try
        {
            // Capture selected IDs on UI thread
            var selectedIds = AppState.SelectedFileIds.ToList();
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

                    var assigned = file.Tags ?? [];
                    var available = GetAvailableTags(assigned);

                    // Update UI on UI thread
                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Single;
                        SelectedFile = file;
                        AssignedTags = assigned;
                        AvailableTags = available;
                        StateHasChanged();
                    });
                }
                else
                {
                    var common = Library.GetCommonTags(selectedIds);
                    var available = GetAvailableTags(common);

                    await InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        SelectionMode = InspectorMode.Multi;
                        SelectedFile = null;
                        CommonTags = common;
                        AvailableTags = available;
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

    private List<Tag> GetAvailableTags(List<Tag> existingTags)
    {
        HashSet<Guid> existingIds = [.. existingTags.Select(t => t.Id)];
        return [.. _allTagsCache.Where(t => !existingIds.Contains(t.Id))];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        AppState.OnSelectionChange -= OnSelectionChanged;
        Library.LibraryChanged -= OnLibraryChanged;
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
    min-width: 0; /* Allow container to shrink below intrinsic width */
}

.inspector-header {
    padding: 8px var(--space-sm);
    background: var(--surface-bg);
    border-bottom: 1px solid var(--border-color);
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    flex-shrink: 0;
}

.title {
    font-weight: 600;
    font-size: var(--font-size-xs);
    text-transform: uppercase;
    color: var(--text-muted);
}

.badge {
    font-size: var(--font-size-xs);
    background: rgba(255, 255, 255, 0.1);
    padding: 2px 6px;
    border-radius: 4px;
    color: var(--text-muted);
}

.inspector-content {
    flex: 1;
    overflow-y: auto;
    overflow-x: hidden; /* Explicitly prevent horizontal scrolling */
    padding: var(--space-sm);
    position: relative;
    min-width: 0; /* Critical: allows flex item to shrink below content size */
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
@using Tagium.Photino.Blazor.Services
@inject AppState AppState
@inject LibraryService Library

<div class="multi-file-inspector">
    <div class="inspector-multi-header">
        <h3 class="multi-title">@AppState.SelectedFileIds.Count Files Selected</h3>
    </div>

    <div class="inspector-tags">
        <h4 class="section-title">Common Tags</h4>
        <div class="tag-pills">
            @foreach (var tag in CommonTags)
            {
                <span class="tag-pill" style="background-color: @tag.ColorHex">
                    @if (!string.IsNullOrEmpty(tag.Emoji))
                    {
                        <span class="pill-emoji">@tag.Emoji</span>
                    }
                    <span class="pill-text">@tag.Name</span>
                    <button class="btn-remove-tag" @onclick="() => BulkRemoveTag(tag.Id)">
                        <i class="fa-solid fa-xmark"></i>
                    </button>
                </span>
            }
            @if (CommonTags.Count == 0)
            {
                <span class="empty-tag">No tags in common.</span>
            }
        </div>

        @if (AvailableTags.Count > 0)
        {
            <div class="add-tag-section">
                <select class="add-tag-select" @onchange="@(e => { if (Guid.TryParse(e.Value?.ToString(), out var tid)) BulkAddTag(tid); })">
                    <option value="">Add tag to all...</option>
                    @foreach (var tag in AvailableTags)
                    {
                        <option value="@tag.Id">@tag.Name</option>
                    }
                </select>
            </div>
        }
    </div>
</div>
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\MultiFileInspector.razor.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tagium.Core.Models;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Components.Inspectors;

public partial class MultiFileInspector
{
    [Parameter] public List<Tag> CommonTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private void BulkAddTag(Guid tagId)
    {
        Library.AddTagToFiles(AppState.SelectedFileIds, tagId);
    }

    private void BulkRemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFiles(AppState.SelectedFileIds, tagId);
    }
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

.add-tag-select {
    width: 100%;
    padding: var(--space-2xs) var(--space-xs);
    background-color: var(--bg-color);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    color: var(--text-color);
    font-size: var(--font-size-sm);
    line-height: 1.2;
    outline: none;
    transition: border-color var(--transition-fast);
}

    .add-tag-select:focus {
        border-color: var(--primary-color);
    }
```

---

### `Tagium.Photino.Blazor\Components\Inspectors\SingleFileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components.Inspectors
@inject LibraryService Library

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

    <div class="inspector-tags">
        <h4 class="section-title">Tags</h4>
        <div class="tag-pills">
            @foreach (var tag in AssignedTags)
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
        </div>

        @if (AvailableTags.Count > 0)
        {
            <div class="add-tag-section">
                <select class="add-tag-select" @onchange="@(e => { if (Guid.TryParse(e.Value?.ToString(), out var tid)) AddTag(tid); })">
                    <option value="">Add tag...</option>
                    @foreach (var tag in AvailableTags)
                    {
                        <option value="@tag.Id">@tag.Name</option>
                    }
                </select>
            </div>
        }
    </div>

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
    [Parameter] public List<Tag> AssignedTags { get; set; } = [];
    [Parameter] public List<Tag> AvailableTags { get; set; } = [];

    private bool IsImage => CheckExtension([".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"]);
    private bool IsVideo => CheckExtension([".mp4", ".webm", ".mov", ".mkv"]);

    private bool CheckExtension(string[] exts)
    {
        string ext = Path.GetExtension(File.Path);
        return exts.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private void AddTag(Guid tagId)
    {
        Library.AddTagToFile(File.Id, tagId);
    }

    private void RemoveTag(Guid tagId)
    {
        Library.RemoveTagFromFile(File.Id, tagId);
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

.add-tag-section {
    margin-top: var(--space-2xs);
}

.add-tag-select {
    width: 100%;
    padding: var(--space-2xs) var(--space-xs);
    background-color: var(--bg-color);
    border: 1px solid var(--border-color);
    border-radius: var(--radius-sm);
    color: var(--text-color);
    font-size: var(--font-size-sm);
    line-height: 1.2;
    outline: none;
    transition: border-color var(--transition-fast);
}

    .add-tag-select:focus {
        border-color: var(--primary-color);
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

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\all.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa-solid,
.fa-regular,
.fa-brands,
.fa-classic,
.fas,
.far,
.fab,
.fa {
  --_fa-family: var(--fa-family, var(--fa-style-family, "Font Awesome 7 Free"));
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
  display: var(--fa-display, inline-block);
  font-family: var(--_fa-family);
  font-feature-settings: normal;
  font-style: normal;
  font-synthesis: none;
  font-variant: normal;
  font-weight: var(--fa-style, 900);
  line-height: 1;
  text-align: center;
  text-rendering: auto;
  width: var(--fa-width, 1.25em);
}

:is(.fas,
.far,
.fab,
.fa-solid,
.fa-regular,
.fa-brands,
.fa-classic,
.fa)::before {
  content: var(--fa)/"";
}

@supports not (content: ""/"") {
  :is(.fas,
  .far,
  .fab,
  .fa-solid,
  .fa-regular,
  .fa-brands,
  .fa-classic,
  .fa)::before {
    content: var(--fa);
  }
}
.fa-1x {
  font-size: 1em;
}

.fa-2x {
  font-size: 2em;
}

.fa-3x {
  font-size: 3em;
}

.fa-4x {
  font-size: 4em;
}

.fa-5x {
  font-size: 5em;
}

.fa-6x {
  font-size: 6em;
}

.fa-7x {
  font-size: 7em;
}

.fa-8x {
  font-size: 8em;
}

.fa-9x {
  font-size: 9em;
}

.fa-10x {
  font-size: 10em;
}

.fa-2xs {
  font-size: calc(10 / 16 * 1em); /* converts a 10px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 10 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 10 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xs {
  font-size: calc(12 / 16 * 1em); /* converts a 12px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 12 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 12 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-sm {
  font-size: calc(14 / 16 * 1em); /* converts a 14px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 14 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 14 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-lg {
  font-size: calc(20 / 16 * 1em); /* converts a 20px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 20 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 20 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xl {
  font-size: calc(24 / 16 * 1em); /* converts a 24px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 24 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 24 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-2xl {
  font-size: calc(32 / 16 * 1em); /* converts a 32px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 32 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 32 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-width-auto {
  --fa-width: auto;
}

.fa-fw,
.fa-width-fixed {
  --fa-width: 1.25em;
}

.fa-ul {
  list-style-type: none;
  margin-inline-start: var(--fa-li-margin, 2.5em);
  padding-inline-start: 0;
}
.fa-ul > li {
  position: relative;
}

.fa-li {
  inset-inline-start: calc(-1 * var(--fa-li-width, 2em));
  position: absolute;
  text-align: center;
  width: var(--fa-li-width, 2em);
  line-height: inherit;
}

/* Heads Up: Bordered Icons will not be supported in the future!
  - This feature will be deprecated in the next major release of Font Awesome (v8)!
  - You may continue to use it in this version *v7), but it will not be supported in Font Awesome v8.
*/
/* Notes:
* --@{v.$css-prefix}-border-width = 1/16 by default (to render as ~1px based on a 16px default font-size)
* --@{v.$css-prefix}-border-padding =
  ** 3/16 for vertical padding (to give ~2px of vertical whitespace around an icon considering it's vertical alignment)
  ** 4/16 for horizontal padding (to give ~4px of horizontal whitespace around an icon)
*/
.fa-border {
  border-color: var(--fa-border-color, #eee);
  border-radius: var(--fa-border-radius, 0.1em);
  border-style: var(--fa-border-style, solid);
  border-width: var(--fa-border-width, 0.0625em);
  box-sizing: var(--fa-border-box-sizing, content-box);
  padding: var(--fa-border-padding, 0.1875em 0.25em);
}

.fa-pull-left,
.fa-pull-start {
  float: inline-start;
  margin-inline-end: var(--fa-pull-margin, 0.3em);
}

.fa-pull-right,
.fa-pull-end {
  float: inline-end;
  margin-inline-start: var(--fa-pull-margin, 0.3em);
}

.fa-beat {
  animation-name: fa-beat;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-bounce {
  animation-name: fa-bounce;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.28, 0.84, 0.42, 1));
}

.fa-fade {
  animation-name: fa-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-beat-fade {
  animation-name: fa-beat-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-flip {
  animation-name: fa-flip;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-shake {
  animation-name: fa-shake;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin {
  animation-name: fa-spin;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 2s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin-reverse {
  --fa-animation-direction: reverse;
}

.fa-pulse,
.fa-spin-pulse {
  animation-name: fa-spin;
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, steps(8));
}

@media (prefers-reduced-motion: reduce) {
  .fa-beat,
  .fa-bounce,
  .fa-fade,
  .fa-beat-fade,
  .fa-flip,
  .fa-pulse,
  .fa-shake,
  .fa-spin,
  .fa-spin-pulse {
    animation: none !important;
    transition: none !important;
  }
}
@keyframes fa-beat {
  0%, 90% {
    transform: scale(1);
  }
  45% {
    transform: scale(var(--fa-beat-scale, 1.25));
  }
}
@keyframes fa-bounce {
  0% {
    transform: scale(1, 1) translateY(0);
  }
  10% {
    transform: scale(var(--fa-bounce-start-scale-x, 1.1), var(--fa-bounce-start-scale-y, 0.9)) translateY(0);
  }
  30% {
    transform: scale(var(--fa-bounce-jump-scale-x, 0.9), var(--fa-bounce-jump-scale-y, 1.1)) translateY(var(--fa-bounce-height, -0.5em));
  }
  50% {
    transform: scale(var(--fa-bounce-land-scale-x, 1.05), var(--fa-bounce-land-scale-y, 0.95)) translateY(0);
  }
  57% {
    transform: scale(1, 1) translateY(var(--fa-bounce-rebound, -0.125em));
  }
  64% {
    transform: scale(1, 1) translateY(0);
  }
  100% {
    transform: scale(1, 1) translateY(0);
  }
}
@keyframes fa-fade {
  50% {
    opacity: var(--fa-fade-opacity, 0.4);
  }
}
@keyframes fa-beat-fade {
  0%, 100% {
    opacity: var(--fa-beat-fade-opacity, 0.4);
    transform: scale(1);
  }
  50% {
    opacity: 1;
    transform: scale(var(--fa-beat-fade-scale, 1.125));
  }
}
@keyframes fa-flip {
  50% {
    transform: rotate3d(var(--fa-flip-x, 0), var(--fa-flip-y, 1), var(--fa-flip-z, 0), var(--fa-flip-angle, -180deg));
  }
}
@keyframes fa-shake {
  0% {
    transform: rotate(-15deg);
  }
  4% {
    transform: rotate(15deg);
  }
  8%, 24% {
    transform: rotate(-18deg);
  }
  12%, 28% {
    transform: rotate(18deg);
  }
  16% {
    transform: rotate(-22deg);
  }
  20% {
    transform: rotate(22deg);
  }
  32% {
    transform: rotate(-12deg);
  }
  36% {
    transform: rotate(12deg);
  }
  40%, 100% {
    transform: rotate(0deg);
  }
}
@keyframes fa-spin {
  0% {
    transform: rotate(0deg);
  }
  100% {
    transform: rotate(360deg);
  }
}
.fa-rotate-90 {
  transform: rotate(90deg);
}

.fa-rotate-180 {
  transform: rotate(180deg);
}

.fa-rotate-270 {
  transform: rotate(270deg);
}

.fa-flip-horizontal {
  transform: scale(-1, 1);
}

.fa-flip-vertical {
  transform: scale(1, -1);
}

.fa-flip-both,
.fa-flip-horizontal.fa-flip-vertical {
  transform: scale(-1, -1);
}

.fa-rotate-by {
  transform: rotate(var(--fa-rotate-angle, 0));
}

.fa-stack {
  display: inline-block;
  height: 2em;
  line-height: 2em;
  position: relative;
  vertical-align: middle;
  width: 2.5em;
}

.fa-stack-1x,
.fa-stack-2x {
  --fa-width: 100%;
  inset: 0;
  position: absolute;
  text-align: center;
  width: var(--fa-width);
  z-index: var(--fa-stack-z-index, auto);
}

.fa-stack-1x {
  line-height: inherit;
}

.fa-stack-2x {
  font-size: 2em;
}

.fa-inverse {
  color: var(--fa-inverse, #fff);
}

/* Font Awesome uses the Unicode Private Use Area (PUA) to ensure screen
   readers do not read off random characters that represent icons */

.fa-0 {
  --fa: "\30 ";
}

.fa-1 {
  --fa: "\31 ";
}

.fa-2 {
  --fa: "\32 ";
}

.fa-3 {
  --fa: "\33 ";
}

.fa-4 {
  --fa: "\34 ";
}

.fa-5 {
  --fa: "\35 ";
}

.fa-6 {
  --fa: "\36 ";
}

.fa-7 {
  --fa: "\37 ";
}

.fa-8 {
  --fa: "\38 ";
}

.fa-9 {
  --fa: "\39 ";
}

.fa-exclamation {
  --fa: "\!";
}

.fa-hashtag {
  --fa: "\#";
}

.fa-dollar-sign {
  --fa: "\$";
}

.fa-dollar {
  --fa: "\$";
}

.fa-usd {
  --fa: "\$";
}

.fa-percent {
  --fa: "\%";
}

.fa-percentage {
  --fa: "\%";
}

.fa-asterisk {
  --fa: "\*";
}

.fa-plus {
  --fa: "\+";
}

.fa-add {
  --fa: "\+";
}

.fa-less-than {
  --fa: "\<";
}

.fa-equals {
  --fa: "\=";
}

.fa-greater-than {
  --fa: "\>";
}

.fa-question {
  --fa: "\?";
}

.fa-at {
  --fa: "\@";
}

.fa-a {
  --fa: "A";
}

.fa-b {
  --fa: "B";
}

.fa-c {
  --fa: "C";
}

.fa-d {
  --fa: "D";
}

.fa-e {
  --fa: "E";
}

.fa-f {
  --fa: "F";
}

.fa-g {
  --fa: "G";
}

.fa-h {
  --fa: "H";
}

.fa-i {
  --fa: "I";
}

.fa-j {
  --fa: "J";
}

.fa-k {
  --fa: "K";
}

.fa-l {
  --fa: "L";
}

.fa-m {
  --fa: "M";
}

.fa-n {
  --fa: "N";
}

.fa-o {
  --fa: "O";
}

.fa-p {
  --fa: "P";
}

.fa-q {
  --fa: "Q";
}

.fa-r {
  --fa: "R";
}

.fa-s {
  --fa: "S";
}

.fa-t {
  --fa: "T";
}

.fa-u {
  --fa: "U";
}

.fa-v {
  --fa: "V";
}

.fa-w {
  --fa: "W";
}

.fa-x {
  --fa: "X";
}

.fa-y {
  --fa: "Y";
}

.fa-z {
  --fa: "Z";
}

.fa-faucet {
  --fa: "\e005";
}

.fa-faucet-drip {
  --fa: "\e006";
}

.fa-house-chimney-window {
  --fa: "\e00d";
}

.fa-house-signal {
  --fa: "\e012";
}

.fa-temperature-arrow-down {
  --fa: "\e03f";
}

.fa-temperature-down {
  --fa: "\e03f";
}

.fa-temperature-arrow-up {
  --fa: "\e040";
}

.fa-temperature-up {
  --fa: "\e040";
}

.fa-trailer {
  --fa: "\e041";
}

.fa-bacteria {
  --fa: "\e059";
}

.fa-bacterium {
  --fa: "\e05a";
}

.fa-box-tissue {
  --fa: "\e05b";
}

.fa-hand-holding-medical {
  --fa: "\e05c";
}

.fa-hand-sparkles {
  --fa: "\e05d";
}

.fa-hands-bubbles {
  --fa: "\e05e";
}

.fa-hands-wash {
  --fa: "\e05e";
}

.fa-handshake-slash {
  --fa: "\e060";
}

.fa-handshake-alt-slash {
  --fa: "\e060";
}

.fa-handshake-simple-slash {
  --fa: "\e060";
}

.fa-head-side-cough {
  --fa: "\e061";
}

.fa-head-side-cough-slash {
  --fa: "\e062";
}

.fa-head-side-mask {
  --fa: "\e063";
}

.fa-head-side-virus {
  --fa: "\e064";
}

.fa-house-chimney-user {
  --fa: "\e065";
}

.fa-house-laptop {
  --fa: "\e066";
}

.fa-laptop-house {
  --fa: "\e066";
}

.fa-lungs-virus {
  --fa: "\e067";
}

.fa-people-arrows {
  --fa: "\e068";
}

.fa-people-arrows-left-right {
  --fa: "\e068";
}

.fa-plane-slash {
  --fa: "\e069";
}

.fa-pump-medical {
  --fa: "\e06a";
}

.fa-pump-soap {
  --fa: "\e06b";
}

.fa-shield-virus {
  --fa: "\e06c";
}

.fa-sink {
  --fa: "\e06d";
}

.fa-soap {
  --fa: "\e06e";
}

.fa-stopwatch-20 {
  --fa: "\e06f";
}

.fa-shop-slash {
  --fa: "\e070";
}

.fa-store-alt-slash {
  --fa: "\e070";
}

.fa-store-slash {
  --fa: "\e071";
}

.fa-toilet-paper-slash {
  --fa: "\e072";
}

.fa-users-slash {
  --fa: "\e073";
}

.fa-virus {
  --fa: "\e074";
}

.fa-virus-slash {
  --fa: "\e075";
}

.fa-viruses {
  --fa: "\e076";
}

.fa-vest {
  --fa: "\e085";
}

.fa-vest-patches {
  --fa: "\e086";
}

.fa-arrow-trend-down {
  --fa: "\e097";
}

.fa-arrow-trend-up {
  --fa: "\e098";
}

.fa-arrow-up-from-bracket {
  --fa: "\e09a";
}

.fa-austral-sign {
  --fa: "\e0a9";
}

.fa-baht-sign {
  --fa: "\e0ac";
}

.fa-bitcoin-sign {
  --fa: "\e0b4";
}

.fa-bolt-lightning {
  --fa: "\e0b7";
}

.fa-book-bookmark {
  --fa: "\e0bb";
}

.fa-camera-rotate {
  --fa: "\e0d8";
}

.fa-cedi-sign {
  --fa: "\e0df";
}

.fa-chart-column {
  --fa: "\e0e3";
}

.fa-chart-gantt {
  --fa: "\e0e4";
}

.fa-clapperboard {
  --fa: "\e131";
}

.fa-clover {
  --fa: "\e139";
}

.fa-code-compare {
  --fa: "\e13a";
}

.fa-code-fork {
  --fa: "\e13b";
}

.fa-code-pull-request {
  --fa: "\e13c";
}

.fa-colon-sign {
  --fa: "\e140";
}

.fa-cruzeiro-sign {
  --fa: "\e152";
}

.fa-display {
  --fa: "\e163";
}

.fa-dong-sign {
  --fa: "\e169";
}

.fa-elevator {
  --fa: "\e16d";
}

.fa-filter-circle-xmark {
  --fa: "\e17b";
}

.fa-florin-sign {
  --fa: "\e184";
}

.fa-folder-closed {
  --fa: "\e185";
}

.fa-franc-sign {
  --fa: "\e18f";
}

.fa-guarani-sign {
  --fa: "\e19a";
}

.fa-gun {
  --fa: "\e19b";
}

.fa-hands-clapping {
  --fa: "\e1a8";
}

.fa-house-user {
  --fa: "\e1b0";
}

.fa-home-user {
  --fa: "\e1b0";
}

.fa-indian-rupee-sign {
  --fa: "\e1bc";
}

.fa-indian-rupee {
  --fa: "\e1bc";
}

.fa-inr {
  --fa: "\e1bc";
}

.fa-kip-sign {
  --fa: "\e1c4";
}

.fa-lari-sign {
  --fa: "\e1c8";
}

.fa-litecoin-sign {
  --fa: "\e1d3";
}

.fa-manat-sign {
  --fa: "\e1d5";
}

.fa-mask-face {
  --fa: "\e1d7";
}

.fa-mill-sign {
  --fa: "\e1ed";
}

.fa-money-bills {
  --fa: "\e1f3";
}

.fa-naira-sign {
  --fa: "\e1f6";
}

.fa-notdef {
  --fa: "\e1fe";
}

.fa-panorama {
  --fa: "\e209";
}

.fa-peseta-sign {
  --fa: "\e221";
}

.fa-peso-sign {
  --fa: "\e222";
}

.fa-plane-up {
  --fa: "\e22d";
}

.fa-rupiah-sign {
  --fa: "\e23d";
}

.fa-stairs {
  --fa: "\e289";
}

.fa-timeline {
  --fa: "\e29c";
}

.fa-truck-front {
  --fa: "\e2b7";
}

.fa-turkish-lira-sign {
  --fa: "\e2bb";
}

.fa-try {
  --fa: "\e2bb";
}

.fa-turkish-lira {
  --fa: "\e2bb";
}

.fa-vault {
  --fa: "\e2c5";
}

.fa-wand-magic-sparkles {
  --fa: "\e2ca";
}

.fa-magic-wand-sparkles {
  --fa: "\e2ca";
}

.fa-wheat-awn {
  --fa: "\e2cd";
}

.fa-wheat-alt {
  --fa: "\e2cd";
}

.fa-wheelchair-move {
  --fa: "\e2ce";
}

.fa-wheelchair-alt {
  --fa: "\e2ce";
}

.fa-bangladeshi-taka-sign {
  --fa: "\e2e6";
}

.fa-bowl-rice {
  --fa: "\e2eb";
}

.fa-person-pregnant {
  --fa: "\e31e";
}

.fa-house-chimney {
  --fa: "\e3af";
}

.fa-home-lg {
  --fa: "\e3af";
}

.fa-house-crack {
  --fa: "\e3b1";
}

.fa-house-medical {
  --fa: "\e3b2";
}

.fa-cent-sign {
  --fa: "\e3f5";
}

.fa-plus-minus {
  --fa: "\e43c";
}

.fa-sailboat {
  --fa: "\e445";
}

.fa-section {
  --fa: "\e447";
}

.fa-shrimp {
  --fa: "\e448";
}

.fa-brazilian-real-sign {
  --fa: "\e46c";
}

.fa-chart-simple {
  --fa: "\e473";
}

.fa-diagram-next {
  --fa: "\e476";
}

.fa-diagram-predecessor {
  --fa: "\e477";
}

.fa-diagram-successor {
  --fa: "\e47a";
}

.fa-earth-oceania {
  --fa: "\e47b";
}

.fa-globe-oceania {
  --fa: "\e47b";
}

.fa-bug-slash {
  --fa: "\e490";
}

.fa-file-circle-plus {
  --fa: "\e494";
}

.fa-shop-lock {
  --fa: "\e4a5";
}

.fa-virus-covid {
  --fa: "\e4a8";
}

.fa-virus-covid-slash {
  --fa: "\e4a9";
}

.fa-anchor-circle-check {
  --fa: "\e4aa";
}

.fa-anchor-circle-exclamation {
  --fa: "\e4ab";
}

.fa-anchor-circle-xmark {
  --fa: "\e4ac";
}

.fa-anchor-lock {
  --fa: "\e4ad";
}

.fa-arrow-down-up-across-line {
  --fa: "\e4af";
}

.fa-arrow-down-up-lock {
  --fa: "\e4b0";
}

.fa-arrow-right-to-city {
  --fa: "\e4b3";
}

.fa-arrow-up-from-ground-water {
  --fa: "\e4b5";
}

.fa-arrow-up-from-water-pump {
  --fa: "\e4b6";
}

.fa-arrow-up-right-dots {
  --fa: "\e4b7";
}

.fa-arrows-down-to-line {
  --fa: "\e4b8";
}

.fa-arrows-down-to-people {
  --fa: "\e4b9";
}

.fa-arrows-left-right-to-line {
  --fa: "\e4ba";
}

.fa-arrows-spin {
  --fa: "\e4bb";
}

.fa-arrows-split-up-and-left {
  --fa: "\e4bc";
}

.fa-arrows-to-circle {
  --fa: "\e4bd";
}

.fa-arrows-to-dot {
  --fa: "\e4be";
}

.fa-arrows-to-eye {
  --fa: "\e4bf";
}

.fa-arrows-turn-right {
  --fa: "\e4c0";
}

.fa-arrows-turn-to-dots {
  --fa: "\e4c1";
}

.fa-arrows-up-to-line {
  --fa: "\e4c2";
}

.fa-bore-hole {
  --fa: "\e4c3";
}

.fa-bottle-droplet {
  --fa: "\e4c4";
}

.fa-bottle-water {
  --fa: "\e4c5";
}

.fa-bowl-food {
  --fa: "\e4c6";
}

.fa-boxes-packing {
  --fa: "\e4c7";
}

.fa-bridge {
  --fa: "\e4c8";
}

.fa-bridge-circle-check {
  --fa: "\e4c9";
}

.fa-bridge-circle-exclamation {
  --fa: "\e4ca";
}

.fa-bridge-circle-xmark {
  --fa: "\e4cb";
}

.fa-bridge-lock {
  --fa: "\e4cc";
}

.fa-bridge-water {
  --fa: "\e4ce";
}

.fa-bucket {
  --fa: "\e4cf";
}

.fa-bugs {
  --fa: "\e4d0";
}

.fa-building-circle-arrow-right {
  --fa: "\e4d1";
}

.fa-building-circle-check {
  --fa: "\e4d2";
}

.fa-building-circle-exclamation {
  --fa: "\e4d3";
}

.fa-building-circle-xmark {
  --fa: "\e4d4";
}

.fa-building-flag {
  --fa: "\e4d5";
}

.fa-building-lock {
  --fa: "\e4d6";
}

.fa-building-ngo {
  --fa: "\e4d7";
}

.fa-building-shield {
  --fa: "\e4d8";
}

.fa-building-un {
  --fa: "\e4d9";
}

.fa-building-user {
  --fa: "\e4da";
}

.fa-building-wheat {
  --fa: "\e4db";
}

.fa-burst {
  --fa: "\e4dc";
}

.fa-car-on {
  --fa: "\e4dd";
}

.fa-car-tunnel {
  --fa: "\e4de";
}

.fa-child-combatant {
  --fa: "\e4e0";
}

.fa-child-rifle {
  --fa: "\e4e0";
}

.fa-children {
  --fa: "\e4e1";
}

.fa-circle-nodes {
  --fa: "\e4e2";
}

.fa-clipboard-question {
  --fa: "\e4e3";
}

.fa-cloud-showers-water {
  --fa: "\e4e4";
}

.fa-computer {
  --fa: "\e4e5";
}

.fa-cubes-stacked {
  --fa: "\e4e6";
}

.fa-envelope-circle-check {
  --fa: "\e4e8";
}

.fa-explosion {
  --fa: "\e4e9";
}

.fa-ferry {
  --fa: "\e4ea";
}

.fa-file-circle-exclamation {
  --fa: "\e4eb";
}

.fa-file-circle-minus {
  --fa: "\e4ed";
}

.fa-file-circle-question {
  --fa: "\e4ef";
}

.fa-file-shield {
  --fa: "\e4f0";
}

.fa-fire-burner {
  --fa: "\e4f1";
}

.fa-fish-fins {
  --fa: "\e4f2";
}

.fa-flask-vial {
  --fa: "\e4f3";
}

.fa-glass-water {
  --fa: "\e4f4";
}

.fa-glass-water-droplet {
  --fa: "\e4f5";
}

.fa-group-arrows-rotate {
  --fa: "\e4f6";
}

.fa-hand-holding-hand {
  --fa: "\e4f7";
}

.fa-handcuffs {
  --fa: "\e4f8";
}

.fa-hands-bound {
  --fa: "\e4f9";
}

.fa-hands-holding-child {
  --fa: "\e4fa";
}

.fa-hands-holding-circle {
  --fa: "\e4fb";
}

.fa-heart-circle-bolt {
  --fa: "\e4fc";
}

.fa-heart-circle-check {
  --fa: "\e4fd";
}

.fa-heart-circle-exclamation {
  --fa: "\e4fe";
}

.fa-heart-circle-minus {
  --fa: "\e4ff";
}

.fa-heart-circle-plus {
  --fa: "\e500";
}

.fa-heart-circle-xmark {
  --fa: "\e501";
}

.fa-helicopter-symbol {
  --fa: "\e502";
}

.fa-helmet-un {
  --fa: "\e503";
}

.fa-hill-avalanche {
  --fa: "\e507";
}

.fa-hill-rockslide {
  --fa: "\e508";
}

.fa-house-circle-check {
  --fa: "\e509";
}

.fa-house-circle-exclamation {
  --fa: "\e50a";
}

.fa-house-circle-xmark {
  --fa: "\e50b";
}

.fa-house-fire {
  --fa: "\e50c";
}

.fa-house-flag {
  --fa: "\e50d";
}

.fa-house-flood-water {
  --fa: "\e50e";
}

.fa-house-flood-water-circle-arrow-right {
  --fa: "\e50f";
}

.fa-house-lock {
  --fa: "\e510";
}

.fa-house-medical-circle-check {
  --fa: "\e511";
}

.fa-house-medical-circle-exclamation {
  --fa: "\e512";
}

.fa-house-medical-circle-xmark {
  --fa: "\e513";
}

.fa-house-medical-flag {
  --fa: "\e514";
}

.fa-house-tsunami {
  --fa: "\e515";
}

.fa-jar {
  --fa: "\e516";
}

.fa-jar-wheat {
  --fa: "\e517";
}

.fa-jet-fighter-up {
  --fa: "\e518";
}

.fa-jug-detergent {
  --fa: "\e519";
}

.fa-kitchen-set {
  --fa: "\e51a";
}

.fa-land-mine-on {
  --fa: "\e51b";
}

.fa-landmark-flag {
  --fa: "\e51c";
}

.fa-laptop-file {
  --fa: "\e51d";
}

.fa-lines-leaning {
  --fa: "\e51e";
}

.fa-location-pin-lock {
  --fa: "\e51f";
}

.fa-locust {
  --fa: "\e520";
}

.fa-magnifying-glass-arrow-right {
  --fa: "\e521";
}

.fa-magnifying-glass-chart {
  --fa: "\e522";
}

.fa-mars-and-venus-burst {
  --fa: "\e523";
}

.fa-mask-ventilator {
  --fa: "\e524";
}

.fa-mattress-pillow {
  --fa: "\e525";
}

.fa-mobile-retro {
  --fa: "\e527";
}

.fa-money-bill-transfer {
  --fa: "\e528";
}

.fa-money-bill-trend-up {
  --fa: "\e529";
}

.fa-money-bill-wheat {
  --fa: "\e52a";
}

.fa-mosquito {
  --fa: "\e52b";
}

.fa-mosquito-net {
  --fa: "\e52c";
}

.fa-mound {
  --fa: "\e52d";
}

.fa-mountain-city {
  --fa: "\e52e";
}

.fa-mountain-sun {
  --fa: "\e52f";
}

.fa-oil-well {
  --fa: "\e532";
}

.fa-people-group {
  --fa: "\e533";
}

.fa-people-line {
  --fa: "\e534";
}

.fa-people-pulling {
  --fa: "\e535";
}

.fa-people-robbery {
  --fa: "\e536";
}

.fa-people-roof {
  --fa: "\e537";
}

.fa-person-arrow-down-to-line {
  --fa: "\e538";
}

.fa-person-arrow-up-from-line {
  --fa: "\e539";
}

.fa-person-breastfeeding {
  --fa: "\e53a";
}

.fa-person-burst {
  --fa: "\e53b";
}

.fa-person-cane {
  --fa: "\e53c";
}

.fa-person-chalkboard {
  --fa: "\e53d";
}

.fa-person-circle-check {
  --fa: "\e53e";
}

.fa-person-circle-exclamation {
  --fa: "\e53f";
}

.fa-person-circle-minus {
  --fa: "\e540";
}

.fa-person-circle-plus {
  --fa: "\e541";
}

.fa-person-circle-question {
  --fa: "\e542";
}

.fa-person-circle-xmark {
  --fa: "\e543";
}

.fa-person-dress-burst {
  --fa: "\e544";
}

.fa-person-drowning {
  --fa: "\e545";
}

.fa-person-falling {
  --fa: "\e546";
}

.fa-person-falling-burst {
  --fa: "\e547";
}

.fa-person-half-dress {
  --fa: "\e548";
}

.fa-person-harassing {
  --fa: "\e549";
}

.fa-person-military-pointing {
  --fa: "\e54a";
}

.fa-person-military-rifle {
  --fa: "\e54b";
}

.fa-person-military-to-person {
  --fa: "\e54c";
}

.fa-person-rays {
  --fa: "\e54d";
}

.fa-person-rifle {
  --fa: "\e54e";
}

.fa-person-shelter {
  --fa: "\e54f";
}

.fa-person-walking-arrow-loop-left {
  --fa: "\e551";
}

.fa-person-walking-arrow-right {
  --fa: "\e552";
}

.fa-person-walking-dashed-line-arrow-right {
  --fa: "\e553";
}

.fa-person-walking-luggage {
  --fa: "\e554";
}

.fa-plane-circle-check {
  --fa: "\e555";
}

.fa-plane-circle-exclamation {
  --fa: "\e556";
}

.fa-plane-circle-xmark {
  --fa: "\e557";
}

.fa-plane-lock {
  --fa: "\e558";
}

.fa-plate-wheat {
  --fa: "\e55a";
}

.fa-plug-circle-bolt {
  --fa: "\e55b";
}

.fa-plug-circle-check {
  --fa: "\e55c";
}

.fa-plug-circle-exclamation {
  --fa: "\e55d";
}

.fa-plug-circle-minus {
  --fa: "\e55e";
}

.fa-plug-circle-plus {
  --fa: "\e55f";
}

.fa-plug-circle-xmark {
  --fa: "\e560";
}

.fa-ranking-star {
  --fa: "\e561";
}

.fa-road-barrier {
  --fa: "\e562";
}

.fa-road-bridge {
  --fa: "\e563";
}

.fa-road-circle-check {
  --fa: "\e564";
}

.fa-road-circle-exclamation {
  --fa: "\e565";
}

.fa-road-circle-xmark {
  --fa: "\e566";
}

.fa-road-lock {
  --fa: "\e567";
}

.fa-road-spikes {
  --fa: "\e568";
}

.fa-rug {
  --fa: "\e569";
}

.fa-sack-xmark {
  --fa: "\e56a";
}

.fa-school-circle-check {
  --fa: "\e56b";
}

.fa-school-circle-exclamation {
  --fa: "\e56c";
}

.fa-school-circle-xmark {
  --fa: "\e56d";
}

.fa-school-flag {
  --fa: "\e56e";
}

.fa-school-lock {
  --fa: "\e56f";
}

.fa-sheet-plastic {
  --fa: "\e571";
}

.fa-shield-cat {
  --fa: "\e572";
}

.fa-shield-dog {
  --fa: "\e573";
}

.fa-shield-heart {
  --fa: "\e574";
}

.fa-square-nfi {
  --fa: "\e576";
}

.fa-square-person-confined {
  --fa: "\e577";
}

.fa-square-virus {
  --fa: "\e578";
}

.fa-staff-snake {
  --fa: "\e579";
}

.fa-rod-asclepius {
  --fa: "\e579";
}

.fa-rod-snake {
  --fa: "\e579";
}

.fa-staff-aesculapius {
  --fa: "\e579";
}

.fa-sun-plant-wilt {
  --fa: "\e57a";
}

.fa-tarp {
  --fa: "\e57b";
}

.fa-tarp-droplet {
  --fa: "\e57c";
}

.fa-tent {
  --fa: "\e57d";
}

.fa-tent-arrow-down-to-line {
  --fa: "\e57e";
}

.fa-tent-arrow-left-right {
  --fa: "\e57f";
}

.fa-tent-arrow-turn-left {
  --fa: "\e580";
}

.fa-tent-arrows-down {
  --fa: "\e581";
}

.fa-tents {
  --fa: "\e582";
}

.fa-toilet-portable {
  --fa: "\e583";
}

.fa-toilets-portable {
  --fa: "\e584";
}

.fa-tower-cell {
  --fa: "\e585";
}

.fa-tower-observation {
  --fa: "\e586";
}

.fa-tree-city {
  --fa: "\e587";
}

.fa-trowel {
  --fa: "\e589";
}

.fa-trowel-bricks {
  --fa: "\e58a";
}

.fa-truck-arrow-right {
  --fa: "\e58b";
}

.fa-truck-droplet {
  --fa: "\e58c";
}

.fa-truck-field {
  --fa: "\e58d";
}

.fa-truck-field-un {
  --fa: "\e58e";
}

.fa-truck-plane {
  --fa: "\e58f";
}

.fa-users-between-lines {
  --fa: "\e591";
}

.fa-users-line {
  --fa: "\e592";
}

.fa-users-rays {
  --fa: "\e593";
}

.fa-users-rectangle {
  --fa: "\e594";
}

.fa-users-viewfinder {
  --fa: "\e595";
}

.fa-vial-circle-check {
  --fa: "\e596";
}

.fa-vial-virus {
  --fa: "\e597";
}

.fa-wheat-awn-circle-exclamation {
  --fa: "\e598";
}

.fa-worm {
  --fa: "\e599";
}

.fa-xmarks-lines {
  --fa: "\e59a";
}

.fa-child-dress {
  --fa: "\e59c";
}

.fa-child-reaching {
  --fa: "\e59d";
}

.fa-file-circle-check {
  --fa: "\e5a0";
}

.fa-file-circle-xmark {
  --fa: "\e5a1";
}

.fa-person-through-window {
  --fa: "\e5a9";
}

.fa-plant-wilt {
  --fa: "\e5aa";
}

.fa-stapler {
  --fa: "\e5af";
}

.fa-train-tram {
  --fa: "\e5b4";
}

.fa-table-cells-column-lock {
  --fa: "\e678";
}

.fa-table-cells-row-lock {
  --fa: "\e67a";
}

.fa-web-awesome {
  --fa: "\e682";
}

.fa-thumbtack-slash {
  --fa: "\e68f";
}

.fa-thumb-tack-slash {
  --fa: "\e68f";
}

.fa-table-cells-row-unlock {
  --fa: "\e691";
}

.fa-chart-diagram {
  --fa: "\e695";
}

.fa-comment-nodes {
  --fa: "\e696";
}

.fa-file-fragment {
  --fa: "\e697";
}

.fa-file-half-dashed {
  --fa: "\e698";
}

.fa-hexagon-nodes {
  --fa: "\e699";
}

.fa-hexagon-nodes-bolt {
  --fa: "\e69a";
}

.fa-square-binary {
  --fa: "\e69b";
}

.fa-pentagon {
  --fa: "\e790";
}

.fa-non-binary {
  --fa: "\e807";
}

.fa-spiral {
  --fa: "\e80a";
}

.fa-mobile-vibrate {
  --fa: "\e816";
}

.fa-single-quote-left {
  --fa: "\e81b";
}

.fa-single-quote-right {
  --fa: "\e81c";
}

.fa-bus-side {
  --fa: "\e81d";
}

.fa-septagon {
  --fa: "\e820";
}

.fa-heptagon {
  --fa: "\e820";
}

.fa-martini-glass-empty {
  --fa: "\f000";
}

.fa-glass-martini {
  --fa: "\f000";
}

.fa-music {
  --fa: "\f001";
}

.fa-magnifying-glass {
  --fa: "\f002";
}

.fa-search {
  --fa: "\f002";
}

.fa-heart {
  --fa: "\f004";
}

.fa-star {
  --fa: "\f005";
}

.fa-user {
  --fa: "\f007";
}

.fa-user-alt {
  --fa: "\f007";
}

.fa-user-large {
  --fa: "\f007";
}

.fa-film {
  --fa: "\f008";
}

.fa-film-alt {
  --fa: "\f008";
}

.fa-film-simple {
  --fa: "\f008";
}

.fa-table-cells-large {
  --fa: "\f009";
}

.fa-th-large {
  --fa: "\f009";
}

.fa-table-cells {
  --fa: "\f00a";
}

.fa-th {
  --fa: "\f00a";
}

.fa-table-list {
  --fa: "\f00b";
}

.fa-th-list {
  --fa: "\f00b";
}

.fa-check {
  --fa: "\f00c";
}

.fa-xmark {
  --fa: "\f00d";
}

.fa-close {
  --fa: "\f00d";
}

.fa-multiply {
  --fa: "\f00d";
}

.fa-remove {
  --fa: "\f00d";
}

.fa-times {
  --fa: "\f00d";
}

.fa-magnifying-glass-plus {
  --fa: "\f00e";
}

.fa-search-plus {
  --fa: "\f00e";
}

.fa-magnifying-glass-minus {
  --fa: "\f010";
}

.fa-search-minus {
  --fa: "\f010";
}

.fa-power-off {
  --fa: "\f011";
}

.fa-signal {
  --fa: "\f012";
}

.fa-signal-5 {
  --fa: "\f012";
}

.fa-signal-perfect {
  --fa: "\f012";
}

.fa-gear {
  --fa: "\f013";
}

.fa-cog {
  --fa: "\f013";
}

.fa-house {
  --fa: "\f015";
}

.fa-home {
  --fa: "\f015";
}

.fa-home-alt {
  --fa: "\f015";
}

.fa-home-lg-alt {
  --fa: "\f015";
}

.fa-clock {
  --fa: "\f017";
}

.fa-clock-four {
  --fa: "\f017";
}

.fa-road {
  --fa: "\f018";
}

.fa-download {
  --fa: "\f019";
}

.fa-inbox {
  --fa: "\f01c";
}

.fa-arrow-rotate-right {
  --fa: "\f01e";
}

.fa-arrow-right-rotate {
  --fa: "\f01e";
}

.fa-arrow-rotate-forward {
  --fa: "\f01e";
}

.fa-redo {
  --fa: "\f01e";
}

.fa-arrows-rotate {
  --fa: "\f021";
}

.fa-refresh {
  --fa: "\f021";
}

.fa-sync {
  --fa: "\f021";
}

.fa-rectangle-list {
  --fa: "\f022";
}

.fa-list-alt {
  --fa: "\f022";
}

.fa-lock {
  --fa: "\f023";
}

.fa-flag {
  --fa: "\f024";
}

.fa-headphones {
  --fa: "\f025";
}

.fa-headphones-alt {
  --fa: "\f025";
}

.fa-headphones-simple {
  --fa: "\f025";
}

.fa-volume-off {
  --fa: "\f026";
}

.fa-volume-low {
  --fa: "\f027";
}

.fa-volume-down {
  --fa: "\f027";
}

.fa-volume-high {
  --fa: "\f028";
}

.fa-volume-up {
  --fa: "\f028";
}

.fa-qrcode {
  --fa: "\f029";
}

.fa-barcode {
  --fa: "\f02a";
}

.fa-tag {
  --fa: "\f02b";
}

.fa-tags {
  --fa: "\f02c";
}

.fa-book {
  --fa: "\f02d";
}

.fa-bookmark {
  --fa: "\f02e";
}

.fa-print {
  --fa: "\f02f";
}

.fa-camera {
  --fa: "\f030";
}

.fa-camera-alt {
  --fa: "\f030";
}

.fa-font {
  --fa: "\f031";
}

.fa-bold {
  --fa: "\f032";
}

.fa-italic {
  --fa: "\f033";
}

.fa-text-height {
  --fa: "\f034";
}

.fa-text-width {
  --fa: "\f035";
}

.fa-align-left {
  --fa: "\f036";
}

.fa-align-center {
  --fa: "\f037";
}

.fa-align-right {
  --fa: "\f038";
}

.fa-align-justify {
  --fa: "\f039";
}

.fa-list {
  --fa: "\f03a";
}

.fa-list-squares {
  --fa: "\f03a";
}

.fa-outdent {
  --fa: "\f03b";
}

.fa-dedent {
  --fa: "\f03b";
}

.fa-indent {
  --fa: "\f03c";
}

.fa-video {
  --fa: "\f03d";
}

.fa-video-camera {
  --fa: "\f03d";
}

.fa-image {
  --fa: "\f03e";
}

.fa-location-pin {
  --fa: "\f041";
}

.fa-map-marker {
  --fa: "\f041";
}

.fa-circle-half-stroke {
  --fa: "\f042";
}

.fa-adjust {
  --fa: "\f042";
}

.fa-droplet {
  --fa: "\f043";
}

.fa-tint {
  --fa: "\f043";
}

.fa-pen-to-square {
  --fa: "\f044";
}

.fa-edit {
  --fa: "\f044";
}

.fa-arrows-up-down-left-right {
  --fa: "\f047";
}

.fa-arrows {
  --fa: "\f047";
}

.fa-backward-step {
  --fa: "\f048";
}

.fa-step-backward {
  --fa: "\f048";
}

.fa-backward-fast {
  --fa: "\f049";
}

.fa-fast-backward {
  --fa: "\f049";
}

.fa-backward {
  --fa: "\f04a";
}

.fa-play {
  --fa: "\f04b";
}

.fa-pause {
  --fa: "\f04c";
}

.fa-stop {
  --fa: "\f04d";
}

.fa-forward {
  --fa: "\f04e";
}

.fa-forward-fast {
  --fa: "\f050";
}

.fa-fast-forward {
  --fa: "\f050";
}

.fa-forward-step {
  --fa: "\f051";
}

.fa-step-forward {
  --fa: "\f051";
}

.fa-eject {
  --fa: "\f052";
}

.fa-chevron-left {
  --fa: "\f053";
}

.fa-chevron-right {
  --fa: "\f054";
}

.fa-circle-plus {
  --fa: "\f055";
}

.fa-plus-circle {
  --fa: "\f055";
}

.fa-circle-minus {
  --fa: "\f056";
}

.fa-minus-circle {
  --fa: "\f056";
}

.fa-circle-xmark {
  --fa: "\f057";
}

.fa-times-circle {
  --fa: "\f057";
}

.fa-xmark-circle {
  --fa: "\f057";
}

.fa-circle-check {
  --fa: "\f058";
}

.fa-check-circle {
  --fa: "\f058";
}

.fa-circle-question {
  --fa: "\f059";
}

.fa-question-circle {
  --fa: "\f059";
}

.fa-circle-info {
  --fa: "\f05a";
}

.fa-info-circle {
  --fa: "\f05a";
}

.fa-crosshairs {
  --fa: "\f05b";
}

.fa-ban {
  --fa: "\f05e";
}

.fa-cancel {
  --fa: "\f05e";
}

.fa-arrow-left {
  --fa: "\f060";
}

.fa-arrow-right {
  --fa: "\f061";
}

.fa-arrow-up {
  --fa: "\f062";
}

.fa-arrow-down {
  --fa: "\f063";
}

.fa-share {
  --fa: "\f064";
}

.fa-mail-forward {
  --fa: "\f064";
}

.fa-expand {
  --fa: "\f065";
}

.fa-compress {
  --fa: "\f066";
}

.fa-minus {
  --fa: "\f068";
}

.fa-subtract {
  --fa: "\f068";
}

.fa-circle-exclamation {
  --fa: "\f06a";
}

.fa-exclamation-circle {
  --fa: "\f06a";
}

.fa-gift {
  --fa: "\f06b";
}

.fa-leaf {
  --fa: "\f06c";
}

.fa-fire {
  --fa: "\f06d";
}

.fa-eye {
  --fa: "\f06e";
}

.fa-eye-slash {
  --fa: "\f070";
}

.fa-triangle-exclamation {
  --fa: "\f071";
}

.fa-exclamation-triangle {
  --fa: "\f071";
}

.fa-warning {
  --fa: "\f071";
}

.fa-plane {
  --fa: "\f072";
}

.fa-calendar-days {
  --fa: "\f073";
}

.fa-calendar-alt {
  --fa: "\f073";
}

.fa-shuffle {
  --fa: "\f074";
}

.fa-random {
  --fa: "\f074";
}

.fa-comment {
  --fa: "\f075";
}

.fa-magnet {
  --fa: "\f076";
}

.fa-chevron-up {
  --fa: "\f077";
}

.fa-chevron-down {
  --fa: "\f078";
}

.fa-retweet {
  --fa: "\f079";
}

.fa-cart-shopping {
  --fa: "\f07a";
}

.fa-shopping-cart {
  --fa: "\f07a";
}

.fa-folder {
  --fa: "\f07b";
}

.fa-folder-blank {
  --fa: "\f07b";
}

.fa-folder-open {
  --fa: "\f07c";
}

.fa-arrows-up-down {
  --fa: "\f07d";
}

.fa-arrows-v {
  --fa: "\f07d";
}

.fa-arrows-left-right {
  --fa: "\f07e";
}

.fa-arrows-h {
  --fa: "\f07e";
}

.fa-chart-bar {
  --fa: "\f080";
}

.fa-bar-chart {
  --fa: "\f080";
}

.fa-camera-retro {
  --fa: "\f083";
}

.fa-key {
  --fa: "\f084";
}

.fa-gears {
  --fa: "\f085";
}

.fa-cogs {
  --fa: "\f085";
}

.fa-comments {
  --fa: "\f086";
}

.fa-star-half {
  --fa: "\f089";
}

.fa-arrow-right-from-bracket {
  --fa: "\f08b";
}

.fa-sign-out {
  --fa: "\f08b";
}

.fa-thumbtack {
  --fa: "\f08d";
}

.fa-thumb-tack {
  --fa: "\f08d";
}

.fa-arrow-up-right-from-square {
  --fa: "\f08e";
}

.fa-external-link {
  --fa: "\f08e";
}

.fa-arrow-right-to-bracket {
  --fa: "\f090";
}

.fa-sign-in {
  --fa: "\f090";
}

.fa-trophy {
  --fa: "\f091";
}

.fa-upload {
  --fa: "\f093";
}

.fa-lemon {
  --fa: "\f094";
}

.fa-phone {
  --fa: "\f095";
}

.fa-square-phone {
  --fa: "\f098";
}

.fa-phone-square {
  --fa: "\f098";
}

.fa-unlock {
  --fa: "\f09c";
}

.fa-credit-card {
  --fa: "\f09d";
}

.fa-credit-card-alt {
  --fa: "\f09d";
}

.fa-rss {
  --fa: "\f09e";
}

.fa-feed {
  --fa: "\f09e";
}

.fa-hard-drive {
  --fa: "\f0a0";
}

.fa-hdd {
  --fa: "\f0a0";
}

.fa-bullhorn {
  --fa: "\f0a1";
}

.fa-certificate {
  --fa: "\f0a3";
}

.fa-hand-point-right {
  --fa: "\f0a4";
}

.fa-hand-point-left {
  --fa: "\f0a5";
}

.fa-hand-point-up {
  --fa: "\f0a6";
}

.fa-hand-point-down {
  --fa: "\f0a7";
}

.fa-circle-arrow-left {
  --fa: "\f0a8";
}

.fa-arrow-circle-left {
  --fa: "\f0a8";
}

.fa-circle-arrow-right {
  --fa: "\f0a9";
}

.fa-arrow-circle-right {
  --fa: "\f0a9";
}

.fa-circle-arrow-up {
  --fa: "\f0aa";
}

.fa-arrow-circle-up {
  --fa: "\f0aa";
}

.fa-circle-arrow-down {
  --fa: "\f0ab";
}

.fa-arrow-circle-down {
  --fa: "\f0ab";
}

.fa-globe {
  --fa: "\f0ac";
}

.fa-wrench {
  --fa: "\f0ad";
}

.fa-list-check {
  --fa: "\f0ae";
}

.fa-tasks {
  --fa: "\f0ae";
}

.fa-filter {
  --fa: "\f0b0";
}

.fa-briefcase {
  --fa: "\f0b1";
}

.fa-up-down-left-right {
  --fa: "\f0b2";
}

.fa-arrows-alt {
  --fa: "\f0b2";
}

.fa-users {
  --fa: "\f0c0";
}

.fa-link {
  --fa: "\f0c1";
}

.fa-chain {
  --fa: "\f0c1";
}

.fa-cloud {
  --fa: "\f0c2";
}

.fa-flask {
  --fa: "\f0c3";
}

.fa-scissors {
  --fa: "\f0c4";
}

.fa-cut {
  --fa: "\f0c4";
}

.fa-copy {
  --fa: "\f0c5";
}

.fa-paperclip {
  --fa: "\f0c6";
}

.fa-floppy-disk {
  --fa: "\f0c7";
}

.fa-save {
  --fa: "\f0c7";
}

.fa-square {
  --fa: "\f0c8";
}

.fa-bars {
  --fa: "\f0c9";
}

.fa-navicon {
  --fa: "\f0c9";
}

.fa-list-ul {
  --fa: "\f0ca";
}

.fa-list-dots {
  --fa: "\f0ca";
}

.fa-list-ol {
  --fa: "\f0cb";
}

.fa-list-1-2 {
  --fa: "\f0cb";
}

.fa-list-numeric {
  --fa: "\f0cb";
}

.fa-strikethrough {
  --fa: "\f0cc";
}

.fa-underline {
  --fa: "\f0cd";
}

.fa-table {
  --fa: "\f0ce";
}

.fa-wand-magic {
  --fa: "\f0d0";
}

.fa-magic {
  --fa: "\f0d0";
}

.fa-truck {
  --fa: "\f0d1";
}

.fa-money-bill {
  --fa: "\f0d6";
}

.fa-caret-down {
  --fa: "\f0d7";
}

.fa-caret-up {
  --fa: "\f0d8";
}

.fa-caret-left {
  --fa: "\f0d9";
}

.fa-caret-right {
  --fa: "\f0da";
}

.fa-table-columns {
  --fa: "\f0db";
}

.fa-columns {
  --fa: "\f0db";
}

.fa-sort {
  --fa: "\f0dc";
}

.fa-unsorted {
  --fa: "\f0dc";
}

.fa-sort-down {
  --fa: "\f0dd";
}

.fa-sort-desc {
  --fa: "\f0dd";
}

.fa-sort-up {
  --fa: "\f0de";
}

.fa-sort-asc {
  --fa: "\f0de";
}

.fa-envelope {
  --fa: "\f0e0";
}

.fa-arrow-rotate-left {
  --fa: "\f0e2";
}

.fa-arrow-left-rotate {
  --fa: "\f0e2";
}

.fa-arrow-rotate-back {
  --fa: "\f0e2";
}

.fa-arrow-rotate-backward {
  --fa: "\f0e2";
}

.fa-undo {
  --fa: "\f0e2";
}

.fa-gavel {
  --fa: "\f0e3";
}

.fa-legal {
  --fa: "\f0e3";
}

.fa-bolt {
  --fa: "\f0e7";
}

.fa-zap {
  --fa: "\f0e7";
}

.fa-sitemap {
  --fa: "\f0e8";
}

.fa-umbrella {
  --fa: "\f0e9";
}

.fa-paste {
  --fa: "\f0ea";
}

.fa-file-clipboard {
  --fa: "\f0ea";
}

.fa-lightbulb {
  --fa: "\f0eb";
}

.fa-arrow-right-arrow-left {
  --fa: "\f0ec";
}

.fa-exchange {
  --fa: "\f0ec";
}

.fa-cloud-arrow-down {
  --fa: "\f0ed";
}

.fa-cloud-download {
  --fa: "\f0ed";
}

.fa-cloud-download-alt {
  --fa: "\f0ed";
}

.fa-cloud-arrow-up {
  --fa: "\f0ee";
}

.fa-cloud-upload {
  --fa: "\f0ee";
}

.fa-cloud-upload-alt {
  --fa: "\f0ee";
}

.fa-user-doctor {
  --fa: "\f0f0";
}

.fa-user-md {
  --fa: "\f0f0";
}

.fa-stethoscope {
  --fa: "\f0f1";
}

.fa-suitcase {
  --fa: "\f0f2";
}

.fa-bell {
  --fa: "\f0f3";
}

.fa-mug-saucer {
  --fa: "\f0f4";
}

.fa-coffee {
  --fa: "\f0f4";
}

.fa-hospital {
  --fa: "\f0f8";
}

.fa-hospital-alt {
  --fa: "\f0f8";
}

.fa-hospital-wide {
  --fa: "\f0f8";
}

.fa-truck-medical {
  --fa: "\f0f9";
}

.fa-ambulance {
  --fa: "\f0f9";
}

.fa-suitcase-medical {
  --fa: "\f0fa";
}

.fa-medkit {
  --fa: "\f0fa";
}

.fa-jet-fighter {
  --fa: "\f0fb";
}

.fa-fighter-jet {
  --fa: "\f0fb";
}

.fa-beer-mug-empty {
  --fa: "\f0fc";
}

.fa-beer {
  --fa: "\f0fc";
}

.fa-square-h {
  --fa: "\f0fd";
}

.fa-h-square {
  --fa: "\f0fd";
}

.fa-square-plus {
  --fa: "\f0fe";
}

.fa-plus-square {
  --fa: "\f0fe";
}

.fa-angles-left {
  --fa: "\f100";
}

.fa-angle-double-left {
  --fa: "\f100";
}

.fa-angles-right {
  --fa: "\f101";
}

.fa-angle-double-right {
  --fa: "\f101";
}

.fa-angles-up {
  --fa: "\f102";
}

.fa-angle-double-up {
  --fa: "\f102";
}

.fa-angles-down {
  --fa: "\f103";
}

.fa-angle-double-down {
  --fa: "\f103";
}

.fa-angle-left {
  --fa: "\f104";
}

.fa-angle-right {
  --fa: "\f105";
}

.fa-angle-up {
  --fa: "\f106";
}

.fa-angle-down {
  --fa: "\f107";
}

.fa-laptop {
  --fa: "\f109";
}

.fa-tablet-button {
  --fa: "\f10a";
}

.fa-mobile-button {
  --fa: "\f10b";
}

.fa-quote-left {
  --fa: "\f10d";
}

.fa-quote-left-alt {
  --fa: "\f10d";
}

.fa-quote-right {
  --fa: "\f10e";
}

.fa-quote-right-alt {
  --fa: "\f10e";
}

.fa-spinner {
  --fa: "\f110";
}

.fa-circle {
  --fa: "\f111";
}

.fa-face-smile {
  --fa: "\f118";
}

.fa-smile {
  --fa: "\f118";
}

.fa-face-frown {
  --fa: "\f119";
}

.fa-frown {
  --fa: "\f119";
}

.fa-face-meh {
  --fa: "\f11a";
}

.fa-meh {
  --fa: "\f11a";
}

.fa-gamepad {
  --fa: "\f11b";
}

.fa-keyboard {
  --fa: "\f11c";
}

.fa-flag-checkered {
  --fa: "\f11e";
}

.fa-terminal {
  --fa: "\f120";
}

.fa-code {
  --fa: "\f121";
}

.fa-reply-all {
  --fa: "\f122";
}

.fa-mail-reply-all {
  --fa: "\f122";
}

.fa-location-arrow {
  --fa: "\f124";
}

.fa-crop {
  --fa: "\f125";
}

.fa-code-branch {
  --fa: "\f126";
}

.fa-link-slash {
  --fa: "\f127";
}

.fa-chain-broken {
  --fa: "\f127";
}

.fa-chain-slash {
  --fa: "\f127";
}

.fa-unlink {
  --fa: "\f127";
}

.fa-info {
  --fa: "\f129";
}

.fa-superscript {
  --fa: "\f12b";
}

.fa-subscript {
  --fa: "\f12c";
}

.fa-eraser {
  --fa: "\f12d";
}

.fa-puzzle-piece {
  --fa: "\f12e";
}

.fa-microphone {
  --fa: "\f130";
}

.fa-microphone-slash {
  --fa: "\f131";
}

.fa-shield {
  --fa: "\f132";
}

.fa-shield-blank {
  --fa: "\f132";
}

.fa-calendar {
  --fa: "\f133";
}

.fa-fire-extinguisher {
  --fa: "\f134";
}

.fa-rocket {
  --fa: "\f135";
}

.fa-circle-chevron-left {
  --fa: "\f137";
}

.fa-chevron-circle-left {
  --fa: "\f137";
}

.fa-circle-chevron-right {
  --fa: "\f138";
}

.fa-chevron-circle-right {
  --fa: "\f138";
}

.fa-circle-chevron-up {
  --fa: "\f139";
}

.fa-chevron-circle-up {
  --fa: "\f139";
}

.fa-circle-chevron-down {
  --fa: "\f13a";
}

.fa-chevron-circle-down {
  --fa: "\f13a";
}

.fa-anchor {
  --fa: "\f13d";
}

.fa-unlock-keyhole {
  --fa: "\f13e";
}

.fa-unlock-alt {
  --fa: "\f13e";
}

.fa-bullseye {
  --fa: "\f140";
}

.fa-ellipsis {
  --fa: "\f141";
}

.fa-ellipsis-h {
  --fa: "\f141";
}

.fa-ellipsis-vertical {
  --fa: "\f142";
}

.fa-ellipsis-v {
  --fa: "\f142";
}

.fa-square-rss {
  --fa: "\f143";
}

.fa-rss-square {
  --fa: "\f143";
}

.fa-circle-play {
  --fa: "\f144";
}

.fa-play-circle {
  --fa: "\f144";
}

.fa-ticket {
  --fa: "\f145";
}

.fa-square-minus {
  --fa: "\f146";
}

.fa-minus-square {
  --fa: "\f146";
}

.fa-arrow-turn-up {
  --fa: "\f148";
}

.fa-level-up {
  --fa: "\f148";
}

.fa-arrow-turn-down {
  --fa: "\f149";
}

.fa-level-down {
  --fa: "\f149";
}

.fa-square-check {
  --fa: "\f14a";
}

.fa-check-square {
  --fa: "\f14a";
}

.fa-square-pen {
  --fa: "\f14b";
}

.fa-pen-square {
  --fa: "\f14b";
}

.fa-pencil-square {
  --fa: "\f14b";
}

.fa-square-arrow-up-right {
  --fa: "\f14c";
}

.fa-external-link-square {
  --fa: "\f14c";
}

.fa-share-from-square {
  --fa: "\f14d";
}

.fa-share-square {
  --fa: "\f14d";
}

.fa-compass {
  --fa: "\f14e";
}

.fa-square-caret-down {
  --fa: "\f150";
}

.fa-caret-square-down {
  --fa: "\f150";
}

.fa-square-caret-up {
  --fa: "\f151";
}

.fa-caret-square-up {
  --fa: "\f151";
}

.fa-square-caret-right {
  --fa: "\f152";
}

.fa-caret-square-right {
  --fa: "\f152";
}

.fa-euro-sign {
  --fa: "\f153";
}

.fa-eur {
  --fa: "\f153";
}

.fa-euro {
  --fa: "\f153";
}

.fa-sterling-sign {
  --fa: "\f154";
}

.fa-gbp {
  --fa: "\f154";
}

.fa-pound-sign {
  --fa: "\f154";
}

.fa-rupee-sign {
  --fa: "\f156";
}

.fa-rupee {
  --fa: "\f156";
}

.fa-yen-sign {
  --fa: "\f157";
}

.fa-cny {
  --fa: "\f157";
}

.fa-jpy {
  --fa: "\f157";
}

.fa-rmb {
  --fa: "\f157";
}

.fa-yen {
  --fa: "\f157";
}

.fa-ruble-sign {
  --fa: "\f158";
}

.fa-rouble {
  --fa: "\f158";
}

.fa-rub {
  --fa: "\f158";
}

.fa-ruble {
  --fa: "\f158";
}

.fa-won-sign {
  --fa: "\f159";
}

.fa-krw {
  --fa: "\f159";
}

.fa-won {
  --fa: "\f159";
}

.fa-file {
  --fa: "\f15b";
}

.fa-file-lines {
  --fa: "\f15c";
}

.fa-file-alt {
  --fa: "\f15c";
}

.fa-file-text {
  --fa: "\f15c";
}

.fa-arrow-down-a-z {
  --fa: "\f15d";
}

.fa-sort-alpha-asc {
  --fa: "\f15d";
}

.fa-sort-alpha-down {
  --fa: "\f15d";
}

.fa-arrow-up-a-z {
  --fa: "\f15e";
}

.fa-sort-alpha-up {
  --fa: "\f15e";
}

.fa-arrow-down-wide-short {
  --fa: "\f160";
}

.fa-sort-amount-asc {
  --fa: "\f160";
}

.fa-sort-amount-down {
  --fa: "\f160";
}

.fa-arrow-up-wide-short {
  --fa: "\f161";
}

.fa-sort-amount-up {
  --fa: "\f161";
}

.fa-arrow-down-1-9 {
  --fa: "\f162";
}

.fa-sort-numeric-asc {
  --fa: "\f162";
}

.fa-sort-numeric-down {
  --fa: "\f162";
}

.fa-arrow-up-1-9 {
  --fa: "\f163";
}

.fa-sort-numeric-up {
  --fa: "\f163";
}

.fa-thumbs-up {
  --fa: "\f164";
}

.fa-thumbs-down {
  --fa: "\f165";
}

.fa-arrow-down-long {
  --fa: "\f175";
}

.fa-long-arrow-down {
  --fa: "\f175";
}

.fa-arrow-up-long {
  --fa: "\f176";
}

.fa-long-arrow-up {
  --fa: "\f176";
}

.fa-arrow-left-long {
  --fa: "\f177";
}

.fa-long-arrow-left {
  --fa: "\f177";
}

.fa-arrow-right-long {
  --fa: "\f178";
}

.fa-long-arrow-right {
  --fa: "\f178";
}

.fa-person-dress {
  --fa: "\f182";
}

.fa-female {
  --fa: "\f182";
}

.fa-person {
  --fa: "\f183";
}

.fa-male {
  --fa: "\f183";
}

.fa-sun {
  --fa: "\f185";
}

.fa-moon {
  --fa: "\f186";
}

.fa-box-archive {
  --fa: "\f187";
}

.fa-archive {
  --fa: "\f187";
}

.fa-bug {
  --fa: "\f188";
}

.fa-square-caret-left {
  --fa: "\f191";
}

.fa-caret-square-left {
  --fa: "\f191";
}

.fa-circle-dot {
  --fa: "\f192";
}

.fa-dot-circle {
  --fa: "\f192";
}

.fa-wheelchair {
  --fa: "\f193";
}

.fa-lira-sign {
  --fa: "\f195";
}

.fa-shuttle-space {
  --fa: "\f197";
}

.fa-space-shuttle {
  --fa: "\f197";
}

.fa-square-envelope {
  --fa: "\f199";
}

.fa-envelope-square {
  --fa: "\f199";
}

.fa-building-columns {
  --fa: "\f19c";
}

.fa-bank {
  --fa: "\f19c";
}

.fa-institution {
  --fa: "\f19c";
}

.fa-museum {
  --fa: "\f19c";
}

.fa-university {
  --fa: "\f19c";
}

.fa-graduation-cap {
  --fa: "\f19d";
}

.fa-mortar-board {
  --fa: "\f19d";
}

.fa-language {
  --fa: "\f1ab";
}

.fa-fax {
  --fa: "\f1ac";
}

.fa-building {
  --fa: "\f1ad";
}

.fa-child {
  --fa: "\f1ae";
}

.fa-paw {
  --fa: "\f1b0";
}

.fa-cube {
  --fa: "\f1b2";
}

.fa-cubes {
  --fa: "\f1b3";
}

.fa-recycle {
  --fa: "\f1b8";
}

.fa-car {
  --fa: "\f1b9";
}

.fa-automobile {
  --fa: "\f1b9";
}

.fa-taxi {
  --fa: "\f1ba";
}

.fa-cab {
  --fa: "\f1ba";
}

.fa-tree {
  --fa: "\f1bb";
}

.fa-database {
  --fa: "\f1c0";
}

.fa-file-pdf {
  --fa: "\f1c1";
}

.fa-file-word {
  --fa: "\f1c2";
}

.fa-file-excel {
  --fa: "\f1c3";
}

.fa-file-powerpoint {
  --fa: "\f1c4";
}

.fa-file-image {
  --fa: "\f1c5";
}

.fa-file-zipper {
  --fa: "\f1c6";
}

.fa-file-archive {
  --fa: "\f1c6";
}

.fa-file-audio {
  --fa: "\f1c7";
}

.fa-file-video {
  --fa: "\f1c8";
}

.fa-file-code {
  --fa: "\f1c9";
}

.fa-life-ring {
  --fa: "\f1cd";
}

.fa-circle-notch {
  --fa: "\f1ce";
}

.fa-paper-plane {
  --fa: "\f1d8";
}

.fa-clock-rotate-left {
  --fa: "\f1da";
}

.fa-history {
  --fa: "\f1da";
}

.fa-heading {
  --fa: "\f1dc";
}

.fa-header {
  --fa: "\f1dc";
}

.fa-paragraph {
  --fa: "\f1dd";
}

.fa-sliders {
  --fa: "\f1de";
}

.fa-sliders-h {
  --fa: "\f1de";
}

.fa-share-nodes {
  --fa: "\f1e0";
}

.fa-share-alt {
  --fa: "\f1e0";
}

.fa-square-share-nodes {
  --fa: "\f1e1";
}

.fa-share-alt-square {
  --fa: "\f1e1";
}

.fa-bomb {
  --fa: "\f1e2";
}

.fa-futbol {
  --fa: "\f1e3";
}

.fa-futbol-ball {
  --fa: "\f1e3";
}

.fa-soccer-ball {
  --fa: "\f1e3";
}

.fa-tty {
  --fa: "\f1e4";
}

.fa-teletype {
  --fa: "\f1e4";
}

.fa-binoculars {
  --fa: "\f1e5";
}

.fa-plug {
  --fa: "\f1e6";
}

.fa-newspaper {
  --fa: "\f1ea";
}

.fa-wifi {
  --fa: "\f1eb";
}

.fa-wifi-3 {
  --fa: "\f1eb";
}

.fa-wifi-strong {
  --fa: "\f1eb";
}

.fa-calculator {
  --fa: "\f1ec";
}

.fa-bell-slash {
  --fa: "\f1f6";
}

.fa-trash {
  --fa: "\f1f8";
}

.fa-copyright {
  --fa: "\f1f9";
}

.fa-eye-dropper {
  --fa: "\f1fb";
}

.fa-eye-dropper-empty {
  --fa: "\f1fb";
}

.fa-eyedropper {
  --fa: "\f1fb";
}

.fa-paintbrush {
  --fa: "\f1fc";
}

.fa-paint-brush {
  --fa: "\f1fc";
}

.fa-cake-candles {
  --fa: "\f1fd";
}

.fa-birthday-cake {
  --fa: "\f1fd";
}

.fa-cake {
  --fa: "\f1fd";
}

.fa-chart-area {
  --fa: "\f1fe";
}

.fa-area-chart {
  --fa: "\f1fe";
}

.fa-chart-pie {
  --fa: "\f200";
}

.fa-pie-chart {
  --fa: "\f200";
}

.fa-chart-line {
  --fa: "\f201";
}

.fa-line-chart {
  --fa: "\f201";
}

.fa-toggle-off {
  --fa: "\f204";
}

.fa-toggle-on {
  --fa: "\f205";
}

.fa-bicycle {
  --fa: "\f206";
}

.fa-bus {
  --fa: "\f207";
}

.fa-closed-captioning {
  --fa: "\f20a";
}

.fa-shekel-sign {
  --fa: "\f20b";
}

.fa-ils {
  --fa: "\f20b";
}

.fa-shekel {
  --fa: "\f20b";
}

.fa-sheqel {
  --fa: "\f20b";
}

.fa-sheqel-sign {
  --fa: "\f20b";
}

.fa-cart-plus {
  --fa: "\f217";
}

.fa-cart-arrow-down {
  --fa: "\f218";
}

.fa-diamond {
  --fa: "\f219";
}

.fa-ship {
  --fa: "\f21a";
}

.fa-user-secret {
  --fa: "\f21b";
}

.fa-motorcycle {
  --fa: "\f21c";
}

.fa-street-view {
  --fa: "\f21d";
}

.fa-heart-pulse {
  --fa: "\f21e";
}

.fa-heartbeat {
  --fa: "\f21e";
}

.fa-venus {
  --fa: "\f221";
}

.fa-mars {
  --fa: "\f222";
}

.fa-mercury {
  --fa: "\f223";
}

.fa-mars-and-venus {
  --fa: "\f224";
}

.fa-transgender {
  --fa: "\f225";
}

.fa-transgender-alt {
  --fa: "\f225";
}

.fa-venus-double {
  --fa: "\f226";
}

.fa-mars-double {
  --fa: "\f227";
}

.fa-venus-mars {
  --fa: "\f228";
}

.fa-mars-stroke {
  --fa: "\f229";
}

.fa-mars-stroke-up {
  --fa: "\f22a";
}

.fa-mars-stroke-v {
  --fa: "\f22a";
}

.fa-mars-stroke-right {
  --fa: "\f22b";
}

.fa-mars-stroke-h {
  --fa: "\f22b";
}

.fa-neuter {
  --fa: "\f22c";
}

.fa-genderless {
  --fa: "\f22d";
}

.fa-server {
  --fa: "\f233";
}

.fa-user-plus {
  --fa: "\f234";
}

.fa-user-xmark {
  --fa: "\f235";
}

.fa-user-times {
  --fa: "\f235";
}

.fa-bed {
  --fa: "\f236";
}

.fa-train {
  --fa: "\f238";
}

.fa-train-subway {
  --fa: "\f239";
}

.fa-subway {
  --fa: "\f239";
}

.fa-battery-full {
  --fa: "\f240";
}

.fa-battery {
  --fa: "\f240";
}

.fa-battery-5 {
  --fa: "\f240";
}

.fa-battery-three-quarters {
  --fa: "\f241";
}

.fa-battery-4 {
  --fa: "\f241";
}

.fa-battery-half {
  --fa: "\f242";
}

.fa-battery-3 {
  --fa: "\f242";
}

.fa-battery-quarter {
  --fa: "\f243";
}

.fa-battery-2 {
  --fa: "\f243";
}

.fa-battery-empty {
  --fa: "\f244";
}

.fa-battery-0 {
  --fa: "\f244";
}

.fa-arrow-pointer {
  --fa: "\f245";
}

.fa-mouse-pointer {
  --fa: "\f245";
}

.fa-i-cursor {
  --fa: "\f246";
}

.fa-object-group {
  --fa: "\f247";
}

.fa-object-ungroup {
  --fa: "\f248";
}

.fa-note-sticky {
  --fa: "\f249";
}

.fa-sticky-note {
  --fa: "\f249";
}

.fa-clone {
  --fa: "\f24d";
}

.fa-scale-balanced {
  --fa: "\f24e";
}

.fa-balance-scale {
  --fa: "\f24e";
}

.fa-hourglass-start {
  --fa: "\f251";
}

.fa-hourglass-1 {
  --fa: "\f251";
}

.fa-hourglass-half {
  --fa: "\f252";
}

.fa-hourglass-2 {
  --fa: "\f252";
}

.fa-hourglass-end {
  --fa: "\f253";
}

.fa-hourglass-3 {
  --fa: "\f253";
}

.fa-hourglass {
  --fa: "\f254";
}

.fa-hourglass-empty {
  --fa: "\f254";
}

.fa-hand-back-fist {
  --fa: "\f255";
}

.fa-hand-rock {
  --fa: "\f255";
}

.fa-hand {
  --fa: "\f256";
}

.fa-hand-paper {
  --fa: "\f256";
}

.fa-hand-scissors {
  --fa: "\f257";
}

.fa-hand-lizard {
  --fa: "\f258";
}

.fa-hand-spock {
  --fa: "\f259";
}

.fa-hand-pointer {
  --fa: "\f25a";
}

.fa-hand-peace {
  --fa: "\f25b";
}

.fa-trademark {
  --fa: "\f25c";
}

.fa-registered {
  --fa: "\f25d";
}

.fa-tv {
  --fa: "\f26c";
}

.fa-television {
  --fa: "\f26c";
}

.fa-tv-alt {
  --fa: "\f26c";
}

.fa-calendar-plus {
  --fa: "\f271";
}

.fa-calendar-minus {
  --fa: "\f272";
}

.fa-calendar-xmark {
  --fa: "\f273";
}

.fa-calendar-times {
  --fa: "\f273";
}

.fa-calendar-check {
  --fa: "\f274";
}

.fa-industry {
  --fa: "\f275";
}

.fa-map-pin {
  --fa: "\f276";
}

.fa-signs-post {
  --fa: "\f277";
}

.fa-map-signs {
  --fa: "\f277";
}

.fa-map {
  --fa: "\f279";
}

.fa-message {
  --fa: "\f27a";
}

.fa-comment-alt {
  --fa: "\f27a";
}

.fa-circle-pause {
  --fa: "\f28b";
}

.fa-pause-circle {
  --fa: "\f28b";
}

.fa-circle-stop {
  --fa: "\f28d";
}

.fa-stop-circle {
  --fa: "\f28d";
}

.fa-bag-shopping {
  --fa: "\f290";
}

.fa-shopping-bag {
  --fa: "\f290";
}

.fa-basket-shopping {
  --fa: "\f291";
}

.fa-shopping-basket {
  --fa: "\f291";
}

.fa-universal-access {
  --fa: "\f29a";
}

.fa-person-walking-with-cane {
  --fa: "\f29d";
}

.fa-blind {
  --fa: "\f29d";
}

.fa-audio-description {
  --fa: "\f29e";
}

.fa-phone-volume {
  --fa: "\f2a0";
}

.fa-volume-control-phone {
  --fa: "\f2a0";
}

.fa-braille {
  --fa: "\f2a1";
}

.fa-ear-listen {
  --fa: "\f2a2";
}

.fa-assistive-listening-systems {
  --fa: "\f2a2";
}

.fa-hands-asl-interpreting {
  --fa: "\f2a3";
}

.fa-american-sign-language-interpreting {
  --fa: "\f2a3";
}

.fa-asl-interpreting {
  --fa: "\f2a3";
}

.fa-hands-american-sign-language-interpreting {
  --fa: "\f2a3";
}

.fa-ear-deaf {
  --fa: "\f2a4";
}

.fa-deaf {
  --fa: "\f2a4";
}

.fa-deafness {
  --fa: "\f2a4";
}

.fa-hard-of-hearing {
  --fa: "\f2a4";
}

.fa-hands {
  --fa: "\f2a7";
}

.fa-sign-language {
  --fa: "\f2a7";
}

.fa-signing {
  --fa: "\f2a7";
}

.fa-eye-low-vision {
  --fa: "\f2a8";
}

.fa-low-vision {
  --fa: "\f2a8";
}

.fa-font-awesome {
  --fa: "\f2b4";
}

.fa-font-awesome-flag {
  --fa: "\f2b4";
}

.fa-font-awesome-logo-full {
  --fa: "\f2b4";
}

.fa-handshake {
  --fa: "\f2b5";
}

.fa-handshake-alt {
  --fa: "\f2b5";
}

.fa-handshake-simple {
  --fa: "\f2b5";
}

.fa-envelope-open {
  --fa: "\f2b6";
}

.fa-address-book {
  --fa: "\f2b9";
}

.fa-contact-book {
  --fa: "\f2b9";
}

.fa-address-card {
  --fa: "\f2bb";
}

.fa-contact-card {
  --fa: "\f2bb";
}

.fa-vcard {
  --fa: "\f2bb";
}

.fa-circle-user {
  --fa: "\f2bd";
}

.fa-user-circle {
  --fa: "\f2bd";
}

.fa-id-badge {
  --fa: "\f2c1";
}

.fa-id-card {
  --fa: "\f2c2";
}

.fa-drivers-license {
  --fa: "\f2c2";
}

.fa-temperature-full {
  --fa: "\f2c7";
}

.fa-temperature-4 {
  --fa: "\f2c7";
}

.fa-thermometer-4 {
  --fa: "\f2c7";
}

.fa-thermometer-full {
  --fa: "\f2c7";
}

.fa-temperature-three-quarters {
  --fa: "\f2c8";
}

.fa-temperature-3 {
  --fa: "\f2c8";
}

.fa-thermometer-3 {
  --fa: "\f2c8";
}

.fa-thermometer-three-quarters {
  --fa: "\f2c8";
}

.fa-temperature-half {
  --fa: "\f2c9";
}

.fa-temperature-2 {
  --fa: "\f2c9";
}

.fa-thermometer-2 {
  --fa: "\f2c9";
}

.fa-thermometer-half {
  --fa: "\f2c9";
}

.fa-temperature-quarter {
  --fa: "\f2ca";
}

.fa-temperature-1 {
  --fa: "\f2ca";
}

.fa-thermometer-1 {
  --fa: "\f2ca";
}

.fa-thermometer-quarter {
  --fa: "\f2ca";
}

.fa-temperature-empty {
  --fa: "\f2cb";
}

.fa-temperature-0 {
  --fa: "\f2cb";
}

.fa-thermometer-0 {
  --fa: "\f2cb";
}

.fa-thermometer-empty {
  --fa: "\f2cb";
}

.fa-shower {
  --fa: "\f2cc";
}

.fa-bath {
  --fa: "\f2cd";
}

.fa-bathtub {
  --fa: "\f2cd";
}

.fa-podcast {
  --fa: "\f2ce";
}

.fa-window-maximize {
  --fa: "\f2d0";
}

.fa-window-minimize {
  --fa: "\f2d1";
}

.fa-window-restore {
  --fa: "\f2d2";
}

.fa-square-xmark {
  --fa: "\f2d3";
}

.fa-times-square {
  --fa: "\f2d3";
}

.fa-xmark-square {
  --fa: "\f2d3";
}

.fa-microchip {
  --fa: "\f2db";
}

.fa-snowflake {
  --fa: "\f2dc";
}

.fa-spoon {
  --fa: "\f2e5";
}

.fa-utensil-spoon {
  --fa: "\f2e5";
}

.fa-utensils {
  --fa: "\f2e7";
}

.fa-cutlery {
  --fa: "\f2e7";
}

.fa-rotate-left {
  --fa: "\f2ea";
}

.fa-rotate-back {
  --fa: "\f2ea";
}

.fa-rotate-backward {
  --fa: "\f2ea";
}

.fa-undo-alt {
  --fa: "\f2ea";
}

.fa-trash-can {
  --fa: "\f2ed";
}

.fa-trash-alt {
  --fa: "\f2ed";
}

.fa-rotate {
  --fa: "\f2f1";
}

.fa-sync-alt {
  --fa: "\f2f1";
}

.fa-stopwatch {
  --fa: "\f2f2";
}

.fa-right-from-bracket {
  --fa: "\f2f5";
}

.fa-sign-out-alt {
  --fa: "\f2f5";
}

.fa-right-to-bracket {
  --fa: "\f2f6";
}

.fa-sign-in-alt {
  --fa: "\f2f6";
}

.fa-rotate-right {
  --fa: "\f2f9";
}

.fa-redo-alt {
  --fa: "\f2f9";
}

.fa-rotate-forward {
  --fa: "\f2f9";
}

.fa-poo {
  --fa: "\f2fe";
}

.fa-images {
  --fa: "\f302";
}

.fa-pencil {
  --fa: "\f303";
}

.fa-pencil-alt {
  --fa: "\f303";
}

.fa-pen {
  --fa: "\f304";
}

.fa-pen-clip {
  --fa: "\f305";
}

.fa-pen-alt {
  --fa: "\f305";
}

.fa-octagon {
  --fa: "\f306";
}

.fa-down-long {
  --fa: "\f309";
}

.fa-long-arrow-alt-down {
  --fa: "\f309";
}

.fa-left-long {
  --fa: "\f30a";
}

.fa-long-arrow-alt-left {
  --fa: "\f30a";
}

.fa-right-long {
  --fa: "\f30b";
}

.fa-long-arrow-alt-right {
  --fa: "\f30b";
}

.fa-up-long {
  --fa: "\f30c";
}

.fa-long-arrow-alt-up {
  --fa: "\f30c";
}

.fa-hexagon {
  --fa: "\f312";
}

.fa-file-pen {
  --fa: "\f31c";
}

.fa-file-edit {
  --fa: "\f31c";
}

.fa-maximize {
  --fa: "\f31e";
}

.fa-expand-arrows-alt {
  --fa: "\f31e";
}

.fa-clipboard {
  --fa: "\f328";
}

.fa-left-right {
  --fa: "\f337";
}

.fa-arrows-alt-h {
  --fa: "\f337";
}

.fa-up-down {
  --fa: "\f338";
}

.fa-arrows-alt-v {
  --fa: "\f338";
}

.fa-alarm-clock {
  --fa: "\f34e";
}

.fa-circle-down {
  --fa: "\f358";
}

.fa-arrow-alt-circle-down {
  --fa: "\f358";
}

.fa-circle-left {
  --fa: "\f359";
}

.fa-arrow-alt-circle-left {
  --fa: "\f359";
}

.fa-circle-right {
  --fa: "\f35a";
}

.fa-arrow-alt-circle-right {
  --fa: "\f35a";
}

.fa-circle-up {
  --fa: "\f35b";
}

.fa-arrow-alt-circle-up {
  --fa: "\f35b";
}

.fa-up-right-from-square {
  --fa: "\f35d";
}

.fa-external-link-alt {
  --fa: "\f35d";
}

.fa-square-up-right {
  --fa: "\f360";
}

.fa-external-link-square-alt {
  --fa: "\f360";
}

.fa-right-left {
  --fa: "\f362";
}

.fa-exchange-alt {
  --fa: "\f362";
}

.fa-repeat {
  --fa: "\f363";
}

.fa-code-commit {
  --fa: "\f386";
}

.fa-code-merge {
  --fa: "\f387";
}

.fa-desktop {
  --fa: "\f390";
}

.fa-desktop-alt {
  --fa: "\f390";
}

.fa-gem {
  --fa: "\f3a5";
}

.fa-turn-down {
  --fa: "\f3be";
}

.fa-level-down-alt {
  --fa: "\f3be";
}

.fa-turn-up {
  --fa: "\f3bf";
}

.fa-level-up-alt {
  --fa: "\f3bf";
}

.fa-lock-open {
  --fa: "\f3c1";
}

.fa-location-dot {
  --fa: "\f3c5";
}

.fa-map-marker-alt {
  --fa: "\f3c5";
}

.fa-microphone-lines {
  --fa: "\f3c9";
}

.fa-microphone-alt {
  --fa: "\f3c9";
}

.fa-mobile-screen-button {
  --fa: "\f3cd";
}

.fa-mobile-alt {
  --fa: "\f3cd";
}

.fa-mobile {
  --fa: "\f3ce";
}

.fa-mobile-android {
  --fa: "\f3ce";
}

.fa-mobile-phone {
  --fa: "\f3ce";
}

.fa-mobile-screen {
  --fa: "\f3cf";
}

.fa-mobile-android-alt {
  --fa: "\f3cf";
}

.fa-money-bill-1 {
  --fa: "\f3d1";
}

.fa-money-bill-alt {
  --fa: "\f3d1";
}

.fa-phone-slash {
  --fa: "\f3dd";
}

.fa-image-portrait {
  --fa: "\f3e0";
}

.fa-portrait {
  --fa: "\f3e0";
}

.fa-reply {
  --fa: "\f3e5";
}

.fa-mail-reply {
  --fa: "\f3e5";
}

.fa-shield-halved {
  --fa: "\f3ed";
}

.fa-shield-alt {
  --fa: "\f3ed";
}

.fa-tablet-screen-button {
  --fa: "\f3fa";
}

.fa-tablet-alt {
  --fa: "\f3fa";
}

.fa-tablet {
  --fa: "\f3fb";
}

.fa-tablet-android {
  --fa: "\f3fb";
}

.fa-ticket-simple {
  --fa: "\f3ff";
}

.fa-ticket-alt {
  --fa: "\f3ff";
}

.fa-rectangle-xmark {
  --fa: "\f410";
}

.fa-rectangle-times {
  --fa: "\f410";
}

.fa-times-rectangle {
  --fa: "\f410";
}

.fa-window-close {
  --fa: "\f410";
}

.fa-down-left-and-up-right-to-center {
  --fa: "\f422";
}

.fa-compress-alt {
  --fa: "\f422";
}

.fa-up-right-and-down-left-from-center {
  --fa: "\f424";
}

.fa-expand-alt {
  --fa: "\f424";
}

.fa-baseball-bat-ball {
  --fa: "\f432";
}

.fa-baseball {
  --fa: "\f433";
}

.fa-baseball-ball {
  --fa: "\f433";
}

.fa-basketball {
  --fa: "\f434";
}

.fa-basketball-ball {
  --fa: "\f434";
}

.fa-bowling-ball {
  --fa: "\f436";
}

.fa-chess {
  --fa: "\f439";
}

.fa-chess-bishop {
  --fa: "\f43a";
}

.fa-chess-board {
  --fa: "\f43c";
}

.fa-chess-king {
  --fa: "\f43f";
}

.fa-chess-knight {
  --fa: "\f441";
}

.fa-chess-pawn {
  --fa: "\f443";
}

.fa-chess-queen {
  --fa: "\f445";
}

.fa-chess-rook {
  --fa: "\f447";
}

.fa-dumbbell {
  --fa: "\f44b";
}

.fa-football {
  --fa: "\f44e";
}

.fa-football-ball {
  --fa: "\f44e";
}

.fa-golf-ball-tee {
  --fa: "\f450";
}

.fa-golf-ball {
  --fa: "\f450";
}

.fa-hockey-puck {
  --fa: "\f453";
}

.fa-broom-ball {
  --fa: "\f458";
}

.fa-quidditch {
  --fa: "\f458";
}

.fa-quidditch-broom-ball {
  --fa: "\f458";
}

.fa-square-full {
  --fa: "\f45c";
}

.fa-table-tennis-paddle-ball {
  --fa: "\f45d";
}

.fa-ping-pong-paddle-ball {
  --fa: "\f45d";
}

.fa-table-tennis {
  --fa: "\f45d";
}

.fa-volleyball {
  --fa: "\f45f";
}

.fa-volleyball-ball {
  --fa: "\f45f";
}

.fa-hand-dots {
  --fa: "\f461";
}

.fa-allergies {
  --fa: "\f461";
}

.fa-bandage {
  --fa: "\f462";
}

.fa-band-aid {
  --fa: "\f462";
}

.fa-box {
  --fa: "\f466";
}

.fa-boxes-stacked {
  --fa: "\f468";
}

.fa-boxes {
  --fa: "\f468";
}

.fa-boxes-alt {
  --fa: "\f468";
}

.fa-briefcase-medical {
  --fa: "\f469";
}

.fa-fire-flame-simple {
  --fa: "\f46a";
}

.fa-burn {
  --fa: "\f46a";
}

.fa-capsules {
  --fa: "\f46b";
}

.fa-clipboard-check {
  --fa: "\f46c";
}

.fa-clipboard-list {
  --fa: "\f46d";
}

.fa-person-dots-from-line {
  --fa: "\f470";
}

.fa-diagnoses {
  --fa: "\f470";
}

.fa-dna {
  --fa: "\f471";
}

.fa-dolly {
  --fa: "\f472";
}

.fa-dolly-box {
  --fa: "\f472";
}

.fa-cart-flatbed {
  --fa: "\f474";
}

.fa-dolly-flatbed {
  --fa: "\f474";
}

.fa-file-medical {
  --fa: "\f477";
}

.fa-file-waveform {
  --fa: "\f478";
}

.fa-file-medical-alt {
  --fa: "\f478";
}

.fa-kit-medical {
  --fa: "\f479";
}

.fa-first-aid {
  --fa: "\f479";
}

.fa-circle-h {
  --fa: "\f47e";
}

.fa-hospital-symbol {
  --fa: "\f47e";
}

.fa-id-card-clip {
  --fa: "\f47f";
}

.fa-id-card-alt {
  --fa: "\f47f";
}

.fa-notes-medical {
  --fa: "\f481";
}

.fa-pallet {
  --fa: "\f482";
}

.fa-pills {
  --fa: "\f484";
}

.fa-prescription-bottle {
  --fa: "\f485";
}

.fa-prescription-bottle-medical {
  --fa: "\f486";
}

.fa-prescription-bottle-alt {
  --fa: "\f486";
}

.fa-bed-pulse {
  --fa: "\f487";
}

.fa-procedures {
  --fa: "\f487";
}

.fa-truck-fast {
  --fa: "\f48b";
}

.fa-shipping-fast {
  --fa: "\f48b";
}

.fa-smoking {
  --fa: "\f48d";
}

.fa-syringe {
  --fa: "\f48e";
}

.fa-tablets {
  --fa: "\f490";
}

.fa-thermometer {
  --fa: "\f491";
}

.fa-vial {
  --fa: "\f492";
}

.fa-vials {
  --fa: "\f493";
}

.fa-warehouse {
  --fa: "\f494";
}

.fa-weight-scale {
  --fa: "\f496";
}

.fa-weight {
  --fa: "\f496";
}

.fa-x-ray {
  --fa: "\f497";
}

.fa-box-open {
  --fa: "\f49e";
}

.fa-comment-dots {
  --fa: "\f4ad";
}

.fa-commenting {
  --fa: "\f4ad";
}

.fa-comment-slash {
  --fa: "\f4b3";
}

.fa-couch {
  --fa: "\f4b8";
}

.fa-circle-dollar-to-slot {
  --fa: "\f4b9";
}

.fa-donate {
  --fa: "\f4b9";
}

.fa-dove {
  --fa: "\f4ba";
}

.fa-hand-holding {
  --fa: "\f4bd";
}

.fa-hand-holding-heart {
  --fa: "\f4be";
}

.fa-hand-holding-dollar {
  --fa: "\f4c0";
}

.fa-hand-holding-usd {
  --fa: "\f4c0";
}

.fa-hand-holding-droplet {
  --fa: "\f4c1";
}

.fa-hand-holding-water {
  --fa: "\f4c1";
}

.fa-hands-holding {
  --fa: "\f4c2";
}

.fa-handshake-angle {
  --fa: "\f4c4";
}

.fa-hands-helping {
  --fa: "\f4c4";
}

.fa-parachute-box {
  --fa: "\f4cd";
}

.fa-people-carry-box {
  --fa: "\f4ce";
}

.fa-people-carry {
  --fa: "\f4ce";
}

.fa-piggy-bank {
  --fa: "\f4d3";
}

.fa-ribbon {
  --fa: "\f4d6";
}

.fa-route {
  --fa: "\f4d7";
}

.fa-seedling {
  --fa: "\f4d8";
}

.fa-sprout {
  --fa: "\f4d8";
}

.fa-sign-hanging {
  --fa: "\f4d9";
}

.fa-sign {
  --fa: "\f4d9";
}

.fa-face-smile-wink {
  --fa: "\f4da";
}

.fa-smile-wink {
  --fa: "\f4da";
}

.fa-tape {
  --fa: "\f4db";
}

.fa-truck-ramp-box {
  --fa: "\f4de";
}

.fa-truck-loading {
  --fa: "\f4de";
}

.fa-truck-moving {
  --fa: "\f4df";
}

.fa-video-slash {
  --fa: "\f4e2";
}

.fa-wine-glass {
  --fa: "\f4e3";
}

.fa-user-astronaut {
  --fa: "\f4fb";
}

.fa-user-check {
  --fa: "\f4fc";
}

.fa-user-clock {
  --fa: "\f4fd";
}

.fa-user-gear {
  --fa: "\f4fe";
}

.fa-user-cog {
  --fa: "\f4fe";
}

.fa-user-pen {
  --fa: "\f4ff";
}

.fa-user-edit {
  --fa: "\f4ff";
}

.fa-user-group {
  --fa: "\f500";
}

.fa-user-friends {
  --fa: "\f500";
}

.fa-user-graduate {
  --fa: "\f501";
}

.fa-user-lock {
  --fa: "\f502";
}

.fa-user-minus {
  --fa: "\f503";
}

.fa-user-ninja {
  --fa: "\f504";
}

.fa-user-shield {
  --fa: "\f505";
}

.fa-user-slash {
  --fa: "\f506";
}

.fa-user-alt-slash {
  --fa: "\f506";
}

.fa-user-large-slash {
  --fa: "\f506";
}

.fa-user-tag {
  --fa: "\f507";
}

.fa-user-tie {
  --fa: "\f508";
}

.fa-users-gear {
  --fa: "\f509";
}

.fa-users-cog {
  --fa: "\f509";
}

.fa-scale-unbalanced {
  --fa: "\f515";
}

.fa-balance-scale-left {
  --fa: "\f515";
}

.fa-scale-unbalanced-flip {
  --fa: "\f516";
}

.fa-balance-scale-right {
  --fa: "\f516";
}

.fa-blender {
  --fa: "\f517";
}

.fa-book-open {
  --fa: "\f518";
}

.fa-tower-broadcast {
  --fa: "\f519";
}

.fa-broadcast-tower {
  --fa: "\f519";
}

.fa-broom {
  --fa: "\f51a";
}

.fa-chalkboard {
  --fa: "\f51b";
}

.fa-blackboard {
  --fa: "\f51b";
}

.fa-chalkboard-user {
  --fa: "\f51c";
}

.fa-chalkboard-teacher {
  --fa: "\f51c";
}

.fa-church {
  --fa: "\f51d";
}

.fa-coins {
  --fa: "\f51e";
}

.fa-compact-disc {
  --fa: "\f51f";
}

.fa-crow {
  --fa: "\f520";
}

.fa-crown {
  --fa: "\f521";
}

.fa-dice {
  --fa: "\f522";
}

.fa-dice-five {
  --fa: "\f523";
}

.fa-dice-four {
  --fa: "\f524";
}

.fa-dice-one {
  --fa: "\f525";
}

.fa-dice-six {
  --fa: "\f526";
}

.fa-dice-three {
  --fa: "\f527";
}

.fa-dice-two {
  --fa: "\f528";
}

.fa-divide {
  --fa: "\f529";
}

.fa-door-closed {
  --fa: "\f52a";
}

.fa-door-open {
  --fa: "\f52b";
}

.fa-feather {
  --fa: "\f52d";
}

.fa-frog {
  --fa: "\f52e";
}

.fa-gas-pump {
  --fa: "\f52f";
}

.fa-glasses {
  --fa: "\f530";
}

.fa-greater-than-equal {
  --fa: "\f532";
}

.fa-helicopter {
  --fa: "\f533";
}

.fa-infinity {
  --fa: "\f534";
}

.fa-kiwi-bird {
  --fa: "\f535";
}

.fa-less-than-equal {
  --fa: "\f537";
}

.fa-memory {
  --fa: "\f538";
}

.fa-microphone-lines-slash {
  --fa: "\f539";
}

.fa-microphone-alt-slash {
  --fa: "\f539";
}

.fa-money-bill-wave {
  --fa: "\f53a";
}

.fa-money-bill-1-wave {
  --fa: "\f53b";
}

.fa-money-bill-wave-alt {
  --fa: "\f53b";
}

.fa-money-check {
  --fa: "\f53c";
}

.fa-money-check-dollar {
  --fa: "\f53d";
}

.fa-money-check-alt {
  --fa: "\f53d";
}

.fa-not-equal {
  --fa: "\f53e";
}

.fa-palette {
  --fa: "\f53f";
}

.fa-square-parking {
  --fa: "\f540";
}

.fa-parking {
  --fa: "\f540";
}

.fa-diagram-project {
  --fa: "\f542";
}

.fa-project-diagram {
  --fa: "\f542";
}

.fa-receipt {
  --fa: "\f543";
}

.fa-robot {
  --fa: "\f544";
}

.fa-ruler {
  --fa: "\f545";
}

.fa-ruler-combined {
  --fa: "\f546";
}

.fa-ruler-horizontal {
  --fa: "\f547";
}

.fa-ruler-vertical {
  --fa: "\f548";
}

.fa-school {
  --fa: "\f549";
}

.fa-screwdriver {
  --fa: "\f54a";
}

.fa-shoe-prints {
  --fa: "\f54b";
}

.fa-skull {
  --fa: "\f54c";
}

.fa-ban-smoking {
  --fa: "\f54d";
}

.fa-smoking-ban {
  --fa: "\f54d";
}

.fa-store {
  --fa: "\f54e";
}

.fa-shop {
  --fa: "\f54f";
}

.fa-store-alt {
  --fa: "\f54f";
}

.fa-bars-staggered {
  --fa: "\f550";
}

.fa-reorder {
  --fa: "\f550";
}

.fa-stream {
  --fa: "\f550";
}

.fa-stroopwafel {
  --fa: "\f551";
}

.fa-toolbox {
  --fa: "\f552";
}

.fa-shirt {
  --fa: "\f553";
}

.fa-t-shirt {
  --fa: "\f553";
}

.fa-tshirt {
  --fa: "\f553";
}

.fa-person-walking {
  --fa: "\f554";
}

.fa-walking {
  --fa: "\f554";
}

.fa-wallet {
  --fa: "\f555";
}

.fa-face-angry {
  --fa: "\f556";
}

.fa-angry {
  --fa: "\f556";
}

.fa-archway {
  --fa: "\f557";
}

.fa-book-atlas {
  --fa: "\f558";
}

.fa-atlas {
  --fa: "\f558";
}

.fa-award {
  --fa: "\f559";
}

.fa-delete-left {
  --fa: "\f55a";
}

.fa-backspace {
  --fa: "\f55a";
}

.fa-bezier-curve {
  --fa: "\f55b";
}

.fa-bong {
  --fa: "\f55c";
}

.fa-brush {
  --fa: "\f55d";
}

.fa-bus-simple {
  --fa: "\f55e";
}

.fa-bus-alt {
  --fa: "\f55e";
}

.fa-cannabis {
  --fa: "\f55f";
}

.fa-check-double {
  --fa: "\f560";
}

.fa-martini-glass-citrus {
  --fa: "\f561";
}

.fa-cocktail {
  --fa: "\f561";
}

.fa-bell-concierge {
  --fa: "\f562";
}

.fa-concierge-bell {
  --fa: "\f562";
}

.fa-cookie {
  --fa: "\f563";
}

.fa-cookie-bite {
  --fa: "\f564";
}

.fa-crop-simple {
  --fa: "\f565";
}

.fa-crop-alt {
  --fa: "\f565";
}

.fa-tachograph-digital {
  --fa: "\f566";
}

.fa-digital-tachograph {
  --fa: "\f566";
}

.fa-face-dizzy {
  --fa: "\f567";
}

.fa-dizzy {
  --fa: "\f567";
}

.fa-compass-drafting {
  --fa: "\f568";
}

.fa-drafting-compass {
  --fa: "\f568";
}

.fa-drum {
  --fa: "\f569";
}

.fa-drum-steelpan {
  --fa: "\f56a";
}

.fa-feather-pointed {
  --fa: "\f56b";
}

.fa-feather-alt {
  --fa: "\f56b";
}

.fa-file-contract {
  --fa: "\f56c";
}

.fa-file-arrow-down {
  --fa: "\f56d";
}

.fa-file-download {
  --fa: "\f56d";
}

.fa-file-export {
  --fa: "\f56e";
}

.fa-arrow-right-from-file {
  --fa: "\f56e";
}

.fa-file-import {
  --fa: "\f56f";
}

.fa-arrow-right-to-file {
  --fa: "\f56f";
}

.fa-file-invoice {
  --fa: "\f570";
}

.fa-file-invoice-dollar {
  --fa: "\f571";
}

.fa-file-prescription {
  --fa: "\f572";
}

.fa-file-signature {
  --fa: "\f573";
}

.fa-file-arrow-up {
  --fa: "\f574";
}

.fa-file-upload {
  --fa: "\f574";
}

.fa-fill {
  --fa: "\f575";
}

.fa-fill-drip {
  --fa: "\f576";
}

.fa-fingerprint {
  --fa: "\f577";
}

.fa-fish {
  --fa: "\f578";
}

.fa-face-flushed {
  --fa: "\f579";
}

.fa-flushed {
  --fa: "\f579";
}

.fa-face-frown-open {
  --fa: "\f57a";
}

.fa-frown-open {
  --fa: "\f57a";
}

.fa-martini-glass {
  --fa: "\f57b";
}

.fa-glass-martini-alt {
  --fa: "\f57b";
}

.fa-earth-africa {
  --fa: "\f57c";
}

.fa-globe-africa {
  --fa: "\f57c";
}

.fa-earth-americas {
  --fa: "\f57d";
}

.fa-earth {
  --fa: "\f57d";
}

.fa-earth-america {
  --fa: "\f57d";
}

.fa-globe-americas {
  --fa: "\f57d";
}

.fa-earth-asia {
  --fa: "\f57e";
}

.fa-globe-asia {
  --fa: "\f57e";
}

.fa-face-grimace {
  --fa: "\f57f";
}

.fa-grimace {
  --fa: "\f57f";
}

.fa-face-grin {
  --fa: "\f580";
}

.fa-grin {
  --fa: "\f580";
}

.fa-face-grin-wide {
  --fa: "\f581";
}

.fa-grin-alt {
  --fa: "\f581";
}

.fa-face-grin-beam {
  --fa: "\f582";
}

.fa-grin-beam {
  --fa: "\f582";
}

.fa-face-grin-beam-sweat {
  --fa: "\f583";
}

.fa-grin-beam-sweat {
  --fa: "\f583";
}

.fa-face-grin-hearts {
  --fa: "\f584";
}

.fa-grin-hearts {
  --fa: "\f584";
}

.fa-face-grin-squint {
  --fa: "\f585";
}

.fa-grin-squint {
  --fa: "\f585";
}

.fa-face-grin-squint-tears {
  --fa: "\f586";
}

.fa-grin-squint-tears {
  --fa: "\f586";
}

.fa-face-grin-stars {
  --fa: "\f587";
}

.fa-grin-stars {
  --fa: "\f587";
}

.fa-face-grin-tears {
  --fa: "\f588";
}

.fa-grin-tears {
  --fa: "\f588";
}

.fa-face-grin-tongue {
  --fa: "\f589";
}

.fa-grin-tongue {
  --fa: "\f589";
}

.fa-face-grin-tongue-squint {
  --fa: "\f58a";
}

.fa-grin-tongue-squint {
  --fa: "\f58a";
}

.fa-face-grin-tongue-wink {
  --fa: "\f58b";
}

.fa-grin-tongue-wink {
  --fa: "\f58b";
}

.fa-face-grin-wink {
  --fa: "\f58c";
}

.fa-grin-wink {
  --fa: "\f58c";
}

.fa-grip {
  --fa: "\f58d";
}

.fa-grid-horizontal {
  --fa: "\f58d";
}

.fa-grip-horizontal {
  --fa: "\f58d";
}

.fa-grip-vertical {
  --fa: "\f58e";
}

.fa-grid-vertical {
  --fa: "\f58e";
}

.fa-headset {
  --fa: "\f590";
}

.fa-highlighter {
  --fa: "\f591";
}

.fa-hot-tub-person {
  --fa: "\f593";
}

.fa-hot-tub {
  --fa: "\f593";
}

.fa-hotel {
  --fa: "\f594";
}

.fa-joint {
  --fa: "\f595";
}

.fa-face-kiss {
  --fa: "\f596";
}

.fa-kiss {
  --fa: "\f596";
}

.fa-face-kiss-beam {
  --fa: "\f597";
}

.fa-kiss-beam {
  --fa: "\f597";
}

.fa-face-kiss-wink-heart {
  --fa: "\f598";
}

.fa-kiss-wink-heart {
  --fa: "\f598";
}

.fa-face-laugh {
  --fa: "\f599";
}

.fa-laugh {
  --fa: "\f599";
}

.fa-face-laugh-beam {
  --fa: "\f59a";
}

.fa-laugh-beam {
  --fa: "\f59a";
}

.fa-face-laugh-squint {
  --fa: "\f59b";
}

.fa-laugh-squint {
  --fa: "\f59b";
}

.fa-face-laugh-wink {
  --fa: "\f59c";
}

.fa-laugh-wink {
  --fa: "\f59c";
}

.fa-cart-flatbed-suitcase {
  --fa: "\f59d";
}

.fa-luggage-cart {
  --fa: "\f59d";
}

.fa-map-location {
  --fa: "\f59f";
}

.fa-map-marked {
  --fa: "\f59f";
}

.fa-map-location-dot {
  --fa: "\f5a0";
}

.fa-map-marked-alt {
  --fa: "\f5a0";
}

.fa-marker {
  --fa: "\f5a1";
}

.fa-medal {
  --fa: "\f5a2";
}

.fa-face-meh-blank {
  --fa: "\f5a4";
}

.fa-meh-blank {
  --fa: "\f5a4";
}

.fa-face-rolling-eyes {
  --fa: "\f5a5";
}

.fa-meh-rolling-eyes {
  --fa: "\f5a5";
}

.fa-monument {
  --fa: "\f5a6";
}

.fa-mortar-pestle {
  --fa: "\f5a7";
}

.fa-paint-roller {
  --fa: "\f5aa";
}

.fa-passport {
  --fa: "\f5ab";
}

.fa-pen-fancy {
  --fa: "\f5ac";
}

.fa-pen-nib {
  --fa: "\f5ad";
}

.fa-pen-ruler {
  --fa: "\f5ae";
}

.fa-pencil-ruler {
  --fa: "\f5ae";
}

.fa-plane-arrival {
  --fa: "\f5af";
}

.fa-plane-departure {
  --fa: "\f5b0";
}

.fa-prescription {
  --fa: "\f5b1";
}

.fa-face-sad-cry {
  --fa: "\f5b3";
}

.fa-sad-cry {
  --fa: "\f5b3";
}

.fa-face-sad-tear {
  --fa: "\f5b4";
}

.fa-sad-tear {
  --fa: "\f5b4";
}

.fa-van-shuttle {
  --fa: "\f5b6";
}

.fa-shuttle-van {
  --fa: "\f5b6";
}

.fa-signature {
  --fa: "\f5b7";
}

.fa-face-smile-beam {
  --fa: "\f5b8";
}

.fa-smile-beam {
  --fa: "\f5b8";
}

.fa-solar-panel {
  --fa: "\f5ba";
}

.fa-spa {
  --fa: "\f5bb";
}

.fa-splotch {
  --fa: "\f5bc";
}

.fa-spray-can {
  --fa: "\f5bd";
}

.fa-stamp {
  --fa: "\f5bf";
}

.fa-star-half-stroke {
  --fa: "\f5c0";
}

.fa-star-half-alt {
  --fa: "\f5c0";
}

.fa-suitcase-rolling {
  --fa: "\f5c1";
}

.fa-face-surprise {
  --fa: "\f5c2";
}

.fa-surprise {
  --fa: "\f5c2";
}

.fa-swatchbook {
  --fa: "\f5c3";
}

.fa-person-swimming {
  --fa: "\f5c4";
}

.fa-swimmer {
  --fa: "\f5c4";
}

.fa-water-ladder {
  --fa: "\f5c5";
}

.fa-ladder-water {
  --fa: "\f5c5";
}

.fa-swimming-pool {
  --fa: "\f5c5";
}

.fa-droplet-slash {
  --fa: "\f5c7";
}

.fa-tint-slash {
  --fa: "\f5c7";
}

.fa-face-tired {
  --fa: "\f5c8";
}

.fa-tired {
  --fa: "\f5c8";
}

.fa-tooth {
  --fa: "\f5c9";
}

.fa-umbrella-beach {
  --fa: "\f5ca";
}

.fa-weight-hanging {
  --fa: "\f5cd";
}

.fa-wine-glass-empty {
  --fa: "\f5ce";
}

.fa-wine-glass-alt {
  --fa: "\f5ce";
}

.fa-spray-can-sparkles {
  --fa: "\f5d0";
}

.fa-air-freshener {
  --fa: "\f5d0";
}

.fa-apple-whole {
  --fa: "\f5d1";
}

.fa-apple-alt {
  --fa: "\f5d1";
}

.fa-atom {
  --fa: "\f5d2";
}

.fa-bone {
  --fa: "\f5d7";
}

.fa-book-open-reader {
  --fa: "\f5da";
}

.fa-book-reader {
  --fa: "\f5da";
}

.fa-brain {
  --fa: "\f5dc";
}

.fa-car-rear {
  --fa: "\f5de";
}

.fa-car-alt {
  --fa: "\f5de";
}

.fa-car-battery {
  --fa: "\f5df";
}

.fa-battery-car {
  --fa: "\f5df";
}

.fa-car-burst {
  --fa: "\f5e1";
}

.fa-car-crash {
  --fa: "\f5e1";
}

.fa-car-side {
  --fa: "\f5e4";
}

.fa-charging-station {
  --fa: "\f5e7";
}

.fa-diamond-turn-right {
  --fa: "\f5eb";
}

.fa-directions {
  --fa: "\f5eb";
}

.fa-draw-polygon {
  --fa: "\f5ee";
}

.fa-vector-polygon {
  --fa: "\f5ee";
}

.fa-laptop-code {
  --fa: "\f5fc";
}

.fa-layer-group {
  --fa: "\f5fd";
}

.fa-location-crosshairs {
  --fa: "\f601";
}

.fa-location {
  --fa: "\f601";
}

.fa-lungs {
  --fa: "\f604";
}

.fa-microscope {
  --fa: "\f610";
}

.fa-oil-can {
  --fa: "\f613";
}

.fa-poop {
  --fa: "\f619";
}

.fa-shapes {
  --fa: "\f61f";
}

.fa-triangle-circle-square {
  --fa: "\f61f";
}

.fa-star-of-life {
  --fa: "\f621";
}

.fa-gauge {
  --fa: "\f624";
}

.fa-dashboard {
  --fa: "\f624";
}

.fa-gauge-med {
  --fa: "\f624";
}

.fa-tachometer-alt-average {
  --fa: "\f624";
}

.fa-gauge-high {
  --fa: "\f625";
}

.fa-tachometer-alt {
  --fa: "\f625";
}

.fa-tachometer-alt-fast {
  --fa: "\f625";
}

.fa-gauge-simple {
  --fa: "\f629";
}

.fa-gauge-simple-med {
  --fa: "\f629";
}

.fa-tachometer-average {
  --fa: "\f629";
}

.fa-gauge-simple-high {
  --fa: "\f62a";
}

.fa-tachometer {
  --fa: "\f62a";
}

.fa-tachometer-fast {
  --fa: "\f62a";
}

.fa-teeth {
  --fa: "\f62e";
}

.fa-teeth-open {
  --fa: "\f62f";
}

.fa-masks-theater {
  --fa: "\f630";
}

.fa-theater-masks {
  --fa: "\f630";
}

.fa-traffic-light {
  --fa: "\f637";
}

.fa-truck-monster {
  --fa: "\f63b";
}

.fa-truck-pickup {
  --fa: "\f63c";
}

.fa-rectangle-ad {
  --fa: "\f641";
}

.fa-ad {
  --fa: "\f641";
}

.fa-ankh {
  --fa: "\f644";
}

.fa-book-bible {
  --fa: "\f647";
}

.fa-bible {
  --fa: "\f647";
}

.fa-business-time {
  --fa: "\f64a";
}

.fa-briefcase-clock {
  --fa: "\f64a";
}

.fa-city {
  --fa: "\f64f";
}

.fa-comment-dollar {
  --fa: "\f651";
}

.fa-comments-dollar {
  --fa: "\f653";
}

.fa-cross {
  --fa: "\f654";
}

.fa-dharmachakra {
  --fa: "\f655";
}

.fa-envelope-open-text {
  --fa: "\f658";
}

.fa-folder-minus {
  --fa: "\f65d";
}

.fa-folder-plus {
  --fa: "\f65e";
}

.fa-filter-circle-dollar {
  --fa: "\f662";
}

.fa-funnel-dollar {
  --fa: "\f662";
}

.fa-gopuram {
  --fa: "\f664";
}

.fa-hamsa {
  --fa: "\f665";
}

.fa-bahai {
  --fa: "\f666";
}

.fa-haykal {
  --fa: "\f666";
}

.fa-jedi {
  --fa: "\f669";
}

.fa-book-journal-whills {
  --fa: "\f66a";
}

.fa-journal-whills {
  --fa: "\f66a";
}

.fa-kaaba {
  --fa: "\f66b";
}

.fa-khanda {
  --fa: "\f66d";
}

.fa-landmark {
  --fa: "\f66f";
}

.fa-envelopes-bulk {
  --fa: "\f674";
}

.fa-mail-bulk {
  --fa: "\f674";
}

.fa-menorah {
  --fa: "\f676";
}

.fa-mosque {
  --fa: "\f678";
}

.fa-om {
  --fa: "\f679";
}

.fa-spaghetti-monster-flying {
  --fa: "\f67b";
}

.fa-pastafarianism {
  --fa: "\f67b";
}

.fa-peace {
  --fa: "\f67c";
}

.fa-place-of-worship {
  --fa: "\f67f";
}

.fa-square-poll-vertical {
  --fa: "\f681";
}

.fa-poll {
  --fa: "\f681";
}

.fa-square-poll-horizontal {
  --fa: "\f682";
}

.fa-poll-h {
  --fa: "\f682";
}

.fa-person-praying {
  --fa: "\f683";
}

.fa-pray {
  --fa: "\f683";
}

.fa-hands-praying {
  --fa: "\f684";
}

.fa-praying-hands {
  --fa: "\f684";
}

.fa-book-quran {
  --fa: "\f687";
}

.fa-quran {
  --fa: "\f687";
}

.fa-magnifying-glass-dollar {
  --fa: "\f688";
}

.fa-search-dollar {
  --fa: "\f688";
}

.fa-magnifying-glass-location {
  --fa: "\f689";
}

.fa-search-location {
  --fa: "\f689";
}

.fa-socks {
  --fa: "\f696";
}

.fa-square-root-variable {
  --fa: "\f698";
}

.fa-square-root-alt {
  --fa: "\f698";
}

.fa-star-and-crescent {
  --fa: "\f699";
}

.fa-star-of-david {
  --fa: "\f69a";
}

.fa-synagogue {
  --fa: "\f69b";
}

.fa-scroll-torah {
  --fa: "\f6a0";
}

.fa-torah {
  --fa: "\f6a0";
}

.fa-torii-gate {
  --fa: "\f6a1";
}

.fa-vihara {
  --fa: "\f6a7";
}

.fa-volume-xmark {
  --fa: "\f6a9";
}

.fa-volume-mute {
  --fa: "\f6a9";
}

.fa-volume-times {
  --fa: "\f6a9";
}

.fa-yin-yang {
  --fa: "\f6ad";
}

.fa-blender-phone {
  --fa: "\f6b6";
}

.fa-book-skull {
  --fa: "\f6b7";
}

.fa-book-dead {
  --fa: "\f6b7";
}

.fa-campground {
  --fa: "\f6bb";
}

.fa-cat {
  --fa: "\f6be";
}

.fa-chair {
  --fa: "\f6c0";
}

.fa-cloud-moon {
  --fa: "\f6c3";
}

.fa-cloud-sun {
  --fa: "\f6c4";
}

.fa-cow {
  --fa: "\f6c8";
}

.fa-dice-d20 {
  --fa: "\f6cf";
}

.fa-dice-d6 {
  --fa: "\f6d1";
}

.fa-dog {
  --fa: "\f6d3";
}

.fa-dragon {
  --fa: "\f6d5";
}

.fa-drumstick-bite {
  --fa: "\f6d7";
}

.fa-dungeon {
  --fa: "\f6d9";
}

.fa-file-csv {
  --fa: "\f6dd";
}

.fa-hand-fist {
  --fa: "\f6de";
}

.fa-fist-raised {
  --fa: "\f6de";
}

.fa-ghost {
  --fa: "\f6e2";
}

.fa-hammer {
  --fa: "\f6e3";
}

.fa-hanukiah {
  --fa: "\f6e6";
}

.fa-hat-wizard {
  --fa: "\f6e8";
}

.fa-person-hiking {
  --fa: "\f6ec";
}

.fa-hiking {
  --fa: "\f6ec";
}

.fa-hippo {
  --fa: "\f6ed";
}

.fa-horse {
  --fa: "\f6f0";
}

.fa-house-chimney-crack {
  --fa: "\f6f1";
}

.fa-house-damage {
  --fa: "\f6f1";
}

.fa-hryvnia-sign {
  --fa: "\f6f2";
}

.fa-hryvnia {
  --fa: "\f6f2";
}

.fa-mask {
  --fa: "\f6fa";
}

.fa-mountain {
  --fa: "\f6fc";
}

.fa-network-wired {
  --fa: "\f6ff";
}

.fa-otter {
  --fa: "\f700";
}

.fa-ring {
  --fa: "\f70b";
}

.fa-person-running {
  --fa: "\f70c";
}

.fa-running {
  --fa: "\f70c";
}

.fa-scroll {
  --fa: "\f70e";
}

.fa-skull-crossbones {
  --fa: "\f714";
}

.fa-slash {
  --fa: "\f715";
}

.fa-spider {
  --fa: "\f717";
}

.fa-toilet-paper {
  --fa: "\f71e";
}

.fa-toilet-paper-alt {
  --fa: "\f71e";
}

.fa-toilet-paper-blank {
  --fa: "\f71e";
}

.fa-tractor {
  --fa: "\f722";
}

.fa-user-injured {
  --fa: "\f728";
}

.fa-vr-cardboard {
  --fa: "\f729";
}

.fa-wand-sparkles {
  --fa: "\f72b";
}

.fa-wind {
  --fa: "\f72e";
}

.fa-wine-bottle {
  --fa: "\f72f";
}

.fa-cloud-meatball {
  --fa: "\f73b";
}

.fa-cloud-moon-rain {
  --fa: "\f73c";
}

.fa-cloud-rain {
  --fa: "\f73d";
}

.fa-cloud-showers-heavy {
  --fa: "\f740";
}

.fa-cloud-sun-rain {
  --fa: "\f743";
}

.fa-democrat {
  --fa: "\f747";
}

.fa-flag-usa {
  --fa: "\f74d";
}

.fa-hurricane {
  --fa: "\f751";
}

.fa-landmark-dome {
  --fa: "\f752";
}

.fa-landmark-alt {
  --fa: "\f752";
}

.fa-meteor {
  --fa: "\f753";
}

.fa-person-booth {
  --fa: "\f756";
}

.fa-poo-storm {
  --fa: "\f75a";
}

.fa-poo-bolt {
  --fa: "\f75a";
}

.fa-rainbow {
  --fa: "\f75b";
}

.fa-republican {
  --fa: "\f75e";
}

.fa-smog {
  --fa: "\f75f";
}

.fa-temperature-high {
  --fa: "\f769";
}

.fa-temperature-low {
  --fa: "\f76b";
}

.fa-cloud-bolt {
  --fa: "\f76c";
}

.fa-thunderstorm {
  --fa: "\f76c";
}

.fa-tornado {
  --fa: "\f76f";
}

.fa-volcano {
  --fa: "\f770";
}

.fa-check-to-slot {
  --fa: "\f772";
}

.fa-vote-yea {
  --fa: "\f772";
}

.fa-water {
  --fa: "\f773";
}

.fa-baby {
  --fa: "\f77c";
}

.fa-baby-carriage {
  --fa: "\f77d";
}

.fa-carriage-baby {
  --fa: "\f77d";
}

.fa-biohazard {
  --fa: "\f780";
}

.fa-blog {
  --fa: "\f781";
}

.fa-calendar-day {
  --fa: "\f783";
}

.fa-calendar-week {
  --fa: "\f784";
}

.fa-candy-cane {
  --fa: "\f786";
}

.fa-carrot {
  --fa: "\f787";
}

.fa-cash-register {
  --fa: "\f788";
}

.fa-minimize {
  --fa: "\f78c";
}

.fa-compress-arrows-alt {
  --fa: "\f78c";
}

.fa-dumpster {
  --fa: "\f793";
}

.fa-dumpster-fire {
  --fa: "\f794";
}

.fa-ethernet {
  --fa: "\f796";
}

.fa-gifts {
  --fa: "\f79c";
}

.fa-champagne-glasses {
  --fa: "\f79f";
}

.fa-glass-cheers {
  --fa: "\f79f";
}

.fa-whiskey-glass {
  --fa: "\f7a0";
}

.fa-glass-whiskey {
  --fa: "\f7a0";
}

.fa-earth-europe {
  --fa: "\f7a2";
}

.fa-globe-europe {
  --fa: "\f7a2";
}

.fa-grip-lines {
  --fa: "\f7a4";
}

.fa-grip-lines-vertical {
  --fa: "\f7a5";
}

.fa-guitar {
  --fa: "\f7a6";
}

.fa-heart-crack {
  --fa: "\f7a9";
}

.fa-heart-broken {
  --fa: "\f7a9";
}

.fa-holly-berry {
  --fa: "\f7aa";
}

.fa-horse-head {
  --fa: "\f7ab";
}

.fa-icicles {
  --fa: "\f7ad";
}

.fa-igloo {
  --fa: "\f7ae";
}

.fa-mitten {
  --fa: "\f7b5";
}

.fa-mug-hot {
  --fa: "\f7b6";
}

.fa-radiation {
  --fa: "\f7b9";
}

.fa-circle-radiation {
  --fa: "\f7ba";
}

.fa-radiation-alt {
  --fa: "\f7ba";
}

.fa-restroom {
  --fa: "\f7bd";
}

.fa-satellite {
  --fa: "\f7bf";
}

.fa-satellite-dish {
  --fa: "\f7c0";
}

.fa-sd-card {
  --fa: "\f7c2";
}

.fa-sim-card {
  --fa: "\f7c4";
}

.fa-person-skating {
  --fa: "\f7c5";
}

.fa-skating {
  --fa: "\f7c5";
}

.fa-person-skiing {
  --fa: "\f7c9";
}

.fa-skiing {
  --fa: "\f7c9";
}

.fa-person-skiing-nordic {
  --fa: "\f7ca";
}

.fa-skiing-nordic {
  --fa: "\f7ca";
}

.fa-sleigh {
  --fa: "\f7cc";
}

.fa-comment-sms {
  --fa: "\f7cd";
}

.fa-sms {
  --fa: "\f7cd";
}

.fa-person-snowboarding {
  --fa: "\f7ce";
}

.fa-snowboarding {
  --fa: "\f7ce";
}

.fa-snowman {
  --fa: "\f7d0";
}

.fa-snowplow {
  --fa: "\f7d2";
}

.fa-tenge-sign {
  --fa: "\f7d7";
}

.fa-tenge {
  --fa: "\f7d7";
}

.fa-toilet {
  --fa: "\f7d8";
}

.fa-screwdriver-wrench {
  --fa: "\f7d9";
}

.fa-tools {
  --fa: "\f7d9";
}

.fa-cable-car {
  --fa: "\f7da";
}

.fa-tram {
  --fa: "\f7da";
}

.fa-fire-flame-curved {
  --fa: "\f7e4";
}

.fa-fire-alt {
  --fa: "\f7e4";
}

.fa-bacon {
  --fa: "\f7e5";
}

.fa-book-medical {
  --fa: "\f7e6";
}

.fa-bread-slice {
  --fa: "\f7ec";
}

.fa-cheese {
  --fa: "\f7ef";
}

.fa-house-chimney-medical {
  --fa: "\f7f2";
}

.fa-clinic-medical {
  --fa: "\f7f2";
}

.fa-clipboard-user {
  --fa: "\f7f3";
}

.fa-comment-medical {
  --fa: "\f7f5";
}

.fa-crutch {
  --fa: "\f7f7";
}

.fa-disease {
  --fa: "\f7fa";
}

.fa-egg {
  --fa: "\f7fb";
}

.fa-folder-tree {
  --fa: "\f802";
}

.fa-burger {
  --fa: "\f805";
}

.fa-hamburger {
  --fa: "\f805";
}

.fa-hand-middle-finger {
  --fa: "\f806";
}

.fa-helmet-safety {
  --fa: "\f807";
}

.fa-hard-hat {
  --fa: "\f807";
}

.fa-hat-hard {
  --fa: "\f807";
}

.fa-hospital-user {
  --fa: "\f80d";
}

.fa-hotdog {
  --fa: "\f80f";
}

.fa-ice-cream {
  --fa: "\f810";
}

.fa-laptop-medical {
  --fa: "\f812";
}

.fa-pager {
  --fa: "\f815";
}

.fa-pepper-hot {
  --fa: "\f816";
}

.fa-pizza-slice {
  --fa: "\f818";
}

.fa-sack-dollar {
  --fa: "\f81d";
}

.fa-book-tanakh {
  --fa: "\f827";
}

.fa-tanakh {
  --fa: "\f827";
}

.fa-bars-progress {
  --fa: "\f828";
}

.fa-tasks-alt {
  --fa: "\f828";
}

.fa-trash-arrow-up {
  --fa: "\f829";
}

.fa-trash-restore {
  --fa: "\f829";
}

.fa-trash-can-arrow-up {
  --fa: "\f82a";
}

.fa-trash-restore-alt {
  --fa: "\f82a";
}

.fa-user-nurse {
  --fa: "\f82f";
}

.fa-wave-square {
  --fa: "\f83e";
}

.fa-person-biking {
  --fa: "\f84a";
}

.fa-biking {
  --fa: "\f84a";
}

.fa-border-all {
  --fa: "\f84c";
}

.fa-border-none {
  --fa: "\f850";
}

.fa-border-top-left {
  --fa: "\f853";
}

.fa-border-style {
  --fa: "\f853";
}

.fa-person-digging {
  --fa: "\f85e";
}

.fa-digging {
  --fa: "\f85e";
}

.fa-fan {
  --fa: "\f863";
}

.fa-icons {
  --fa: "\f86d";
}

.fa-heart-music-camera-bolt {
  --fa: "\f86d";
}

.fa-phone-flip {
  --fa: "\f879";
}

.fa-phone-alt {
  --fa: "\f879";
}

.fa-square-phone-flip {
  --fa: "\f87b";
}

.fa-phone-square-alt {
  --fa: "\f87b";
}

.fa-photo-film {
  --fa: "\f87c";
}

.fa-photo-video {
  --fa: "\f87c";
}

.fa-text-slash {
  --fa: "\f87d";
}

.fa-remove-format {
  --fa: "\f87d";
}

.fa-arrow-down-z-a {
  --fa: "\f881";
}

.fa-sort-alpha-desc {
  --fa: "\f881";
}

.fa-sort-alpha-down-alt {
  --fa: "\f881";
}

.fa-arrow-up-z-a {
  --fa: "\f882";
}

.fa-sort-alpha-up-alt {
  --fa: "\f882";
}

.fa-arrow-down-short-wide {
  --fa: "\f884";
}

.fa-sort-amount-desc {
  --fa: "\f884";
}

.fa-sort-amount-down-alt {
  --fa: "\f884";
}

.fa-arrow-up-short-wide {
  --fa: "\f885";
}

.fa-sort-amount-up-alt {
  --fa: "\f885";
}

.fa-arrow-down-9-1 {
  --fa: "\f886";
}

.fa-sort-numeric-desc {
  --fa: "\f886";
}

.fa-sort-numeric-down-alt {
  --fa: "\f886";
}

.fa-arrow-up-9-1 {
  --fa: "\f887";
}

.fa-sort-numeric-up-alt {
  --fa: "\f887";
}

.fa-spell-check {
  --fa: "\f891";
}

.fa-voicemail {
  --fa: "\f897";
}

.fa-hat-cowboy {
  --fa: "\f8c0";
}

.fa-hat-cowboy-side {
  --fa: "\f8c1";
}

.fa-computer-mouse {
  --fa: "\f8cc";
}

.fa-mouse {
  --fa: "\f8cc";
}

.fa-radio {
  --fa: "\f8d7";
}

.fa-record-vinyl {
  --fa: "\f8d9";
}

.fa-walkie-talkie {
  --fa: "\f8ef";
}

.fa-caravan {
  --fa: "\f8ff";
}
:root, :host {
  --fa-family-brands: "Font Awesome 7 Brands";
  --fa-font-brands: normal 400 1em/1 var(--fa-family-brands);
}

@font-face {
  font-family: "Font Awesome 7 Brands";
  font-style: normal;
  font-weight: 400;
  font-display: block;
  src: url("../webfonts/fa-brands-400.woff2");
}
.fab,
.fa-brands,
.fa-classic.fa-brands {
  --fa-family: var(--fa-family-brands);
  --fa-style: 400;
}

.fa-firefox-browser {
  --fa: "\e007";
}

.fa-ideal {
  --fa: "\e013";
}

.fa-microblog {
  --fa: "\e01a";
}

.fa-square-pied-piper {
  --fa: "\e01e";
}

.fa-pied-piper-square {
  --fa: "\e01e";
}

.fa-unity {
  --fa: "\e049";
}

.fa-dailymotion {
  --fa: "\e052";
}

.fa-square-instagram {
  --fa: "\e055";
}

.fa-instagram-square {
  --fa: "\e055";
}

.fa-mixer {
  --fa: "\e056";
}

.fa-shopify {
  --fa: "\e057";
}

.fa-deezer {
  --fa: "\e077";
}

.fa-edge-legacy {
  --fa: "\e078";
}

.fa-google-pay {
  --fa: "\e079";
}

.fa-rust {
  --fa: "\e07a";
}

.fa-tiktok {
  --fa: "\e07b";
}

.fa-unsplash {
  --fa: "\e07c";
}

.fa-cloudflare {
  --fa: "\e07d";
}

.fa-guilded {
  --fa: "\e07e";
}

.fa-hive {
  --fa: "\e07f";
}

.fa-42-group {
  --fa: "\e080";
}

.fa-innosoft {
  --fa: "\e080";
}

.fa-instalod {
  --fa: "\e081";
}

.fa-octopus-deploy {
  --fa: "\e082";
}

.fa-perbyte {
  --fa: "\e083";
}

.fa-uncharted {
  --fa: "\e084";
}

.fa-watchman-monitoring {
  --fa: "\e087";
}

.fa-wodu {
  --fa: "\e088";
}

.fa-wirsindhandwerk {
  --fa: "\e2d0";
}

.fa-wsh {
  --fa: "\e2d0";
}

.fa-bots {
  --fa: "\e340";
}

.fa-cmplid {
  --fa: "\e360";
}

.fa-bilibili {
  --fa: "\e3d9";
}

.fa-golang {
  --fa: "\e40f";
}

.fa-pix {
  --fa: "\e43a";
}

.fa-sitrox {
  --fa: "\e44a";
}

.fa-hashnode {
  --fa: "\e499";
}

.fa-meta {
  --fa: "\e49b";
}

.fa-padlet {
  --fa: "\e4a0";
}

.fa-nfc-directional {
  --fa: "\e530";
}

.fa-nfc-symbol {
  --fa: "\e531";
}

.fa-screenpal {
  --fa: "\e570";
}

.fa-space-awesome {
  --fa: "\e5ac";
}

.fa-square-font-awesome {
  --fa: "\e5ad";
}

.fa-square-gitlab {
  --fa: "\e5ae";
}

.fa-gitlab-square {
  --fa: "\e5ae";
}

.fa-odysee {
  --fa: "\e5c6";
}

.fa-stubber {
  --fa: "\e5c7";
}

.fa-debian {
  --fa: "\e60b";
}

.fa-shoelace {
  --fa: "\e60c";
}

.fa-threads {
  --fa: "\e618";
}

.fa-square-threads {
  --fa: "\e619";
}

.fa-square-x-twitter {
  --fa: "\e61a";
}

.fa-x-twitter {
  --fa: "\e61b";
}

.fa-opensuse {
  --fa: "\e62b";
}

.fa-letterboxd {
  --fa: "\e62d";
}

.fa-square-letterboxd {
  --fa: "\e62e";
}

.fa-mintbit {
  --fa: "\e62f";
}

.fa-google-scholar {
  --fa: "\e63b";
}

.fa-brave {
  --fa: "\e63c";
}

.fa-brave-reverse {
  --fa: "\e63d";
}

.fa-pixiv {
  --fa: "\e640";
}

.fa-upwork {
  --fa: "\e641";
}

.fa-webflow {
  --fa: "\e65c";
}

.fa-signal-messenger {
  --fa: "\e663";
}

.fa-bluesky {
  --fa: "\e671";
}

.fa-jxl {
  --fa: "\e67b";
}

.fa-square-upwork {
  --fa: "\e67c";
}

.fa-web-awesome {
  --fa: "\e682";
}

.fa-square-web-awesome {
  --fa: "\e683";
}

.fa-square-web-awesome-stroke {
  --fa: "\e684";
}

.fa-dart-lang {
  --fa: "\e693";
}

.fa-flutter {
  --fa: "\e694";
}

.fa-files-pinwheel {
  --fa: "\e69f";
}

.fa-css {
  --fa: "\e6a2";
}

.fa-square-bluesky {
  --fa: "\e6a3";
}

.fa-openai {
  --fa: "\e7cf";
}

.fa-square-linkedin {
  --fa: "\e7d0";
}

.fa-cash-app {
  --fa: "\e7d4";
}

.fa-disqus {
  --fa: "\e7d5";
}

.fa-eleventy {
  --fa: "\e7d6";
}

.fa-11ty {
  --fa: "\e7d6";
}

.fa-kakao-talk {
  --fa: "\e7d7";
}

.fa-linktree {
  --fa: "\e7d8";
}

.fa-notion {
  --fa: "\e7d9";
}

.fa-pandora {
  --fa: "\e7da";
}

.fa-pixelfed {
  --fa: "\e7db";
}

.fa-tidal {
  --fa: "\e7dc";
}

.fa-vsco {
  --fa: "\e7dd";
}

.fa-w3c {
  --fa: "\e7de";
}

.fa-lumon {
  --fa: "\e7e2";
}

.fa-lumon-drop {
  --fa: "\e7e3";
}

.fa-square-figma {
  --fa: "\e7e4";
}

.fa-tex {
  --fa: "\e7ff";
}

.fa-duolingo {
  --fa: "\e812";
}

.fa-square-twitter {
  --fa: "\f081";
}

.fa-twitter-square {
  --fa: "\f081";
}

.fa-square-facebook {
  --fa: "\f082";
}

.fa-facebook-square {
  --fa: "\f082";
}

.fa-linkedin {
  --fa: "\f08c";
}

.fa-square-github {
  --fa: "\f092";
}

.fa-github-square {
  --fa: "\f092";
}

.fa-twitter {
  --fa: "\f099";
}

.fa-facebook {
  --fa: "\f09a";
}

.fa-github {
  --fa: "\f09b";
}

.fa-pinterest {
  --fa: "\f0d2";
}

.fa-square-pinterest {
  --fa: "\f0d3";
}

.fa-pinterest-square {
  --fa: "\f0d3";
}

.fa-square-google-plus {
  --fa: "\f0d4";
}

.fa-google-plus-square {
  --fa: "\f0d4";
}

.fa-google-plus-g {
  --fa: "\f0d5";
}

.fa-linkedin-in {
  --fa: "\f0e1";
}

.fa-github-alt {
  --fa: "\f113";
}

.fa-maxcdn {
  --fa: "\f136";
}

.fa-html5 {
  --fa: "\f13b";
}

.fa-css3 {
  --fa: "\f13c";
}

.fa-btc {
  --fa: "\f15a";
}

.fa-youtube {
  --fa: "\f167";
}

.fa-xing {
  --fa: "\f168";
}

.fa-square-xing {
  --fa: "\f169";
}

.fa-xing-square {
  --fa: "\f169";
}

.fa-dropbox {
  --fa: "\f16b";
}

.fa-stack-overflow {
  --fa: "\f16c";
}

.fa-instagram {
  --fa: "\f16d";
}

.fa-flickr {
  --fa: "\f16e";
}

.fa-adn {
  --fa: "\f170";
}

.fa-bitbucket {
  --fa: "\f171";
}

.fa-tumblr {
  --fa: "\f173";
}

.fa-square-tumblr {
  --fa: "\f174";
}

.fa-tumblr-square {
  --fa: "\f174";
}

.fa-apple {
  --fa: "\f179";
}

.fa-windows {
  --fa: "\f17a";
}

.fa-android {
  --fa: "\f17b";
}

.fa-linux {
  --fa: "\f17c";
}

.fa-dribbble {
  --fa: "\f17d";
}

.fa-skype {
  --fa: "\f17e";
}

.fa-foursquare {
  --fa: "\f180";
}

.fa-trello {
  --fa: "\f181";
}

.fa-gratipay {
  --fa: "\f184";
}

.fa-vk {
  --fa: "\f189";
}

.fa-weibo {
  --fa: "\f18a";
}

.fa-renren {
  --fa: "\f18b";
}

.fa-pagelines {
  --fa: "\f18c";
}

.fa-stack-exchange {
  --fa: "\f18d";
}

.fa-square-vimeo {
  --fa: "\f194";
}

.fa-vimeo-square {
  --fa: "\f194";
}

.fa-slack {
  --fa: "\f198";
}

.fa-slack-hash {
  --fa: "\f198";
}

.fa-wordpress {
  --fa: "\f19a";
}

.fa-openid {
  --fa: "\f19b";
}

.fa-yahoo {
  --fa: "\f19e";
}

.fa-google {
  --fa: "\f1a0";
}

.fa-reddit {
  --fa: "\f1a1";
}

.fa-square-reddit {
  --fa: "\f1a2";
}

.fa-reddit-square {
  --fa: "\f1a2";
}

.fa-stumbleupon-circle {
  --fa: "\f1a3";
}

.fa-stumbleupon {
  --fa: "\f1a4";
}

.fa-delicious {
  --fa: "\f1a5";
}

.fa-digg {
  --fa: "\f1a6";
}

.fa-pied-piper-pp {
  --fa: "\f1a7";
}

.fa-pied-piper-alt {
  --fa: "\f1a8";
}

.fa-drupal {
  --fa: "\f1a9";
}

.fa-joomla {
  --fa: "\f1aa";
}

.fa-behance {
  --fa: "\f1b4";
}

.fa-square-behance {
  --fa: "\f1b5";
}

.fa-behance-square {
  --fa: "\f1b5";
}

.fa-steam {
  --fa: "\f1b6";
}

.fa-square-steam {
  --fa: "\f1b7";
}

.fa-steam-square {
  --fa: "\f1b7";
}

.fa-spotify {
  --fa: "\f1bc";
}

.fa-deviantart {
  --fa: "\f1bd";
}

.fa-soundcloud {
  --fa: "\f1be";
}

.fa-vine {
  --fa: "\f1ca";
}

.fa-codepen {
  --fa: "\f1cb";
}

.fa-jsfiddle {
  --fa: "\f1cc";
}

.fa-rebel {
  --fa: "\f1d0";
}

.fa-empire {
  --fa: "\f1d1";
}

.fa-square-git {
  --fa: "\f1d2";
}

.fa-git-square {
  --fa: "\f1d2";
}

.fa-git {
  --fa: "\f1d3";
}

.fa-hacker-news {
  --fa: "\f1d4";
}

.fa-tencent-weibo {
  --fa: "\f1d5";
}

.fa-qq {
  --fa: "\f1d6";
}

.fa-weixin {
  --fa: "\f1d7";
}

.fa-slideshare {
  --fa: "\f1e7";
}

.fa-twitch {
  --fa: "\f1e8";
}

.fa-yelp {
  --fa: "\f1e9";
}

.fa-paypal {
  --fa: "\f1ed";
}

.fa-google-wallet {
  --fa: "\f1ee";
}

.fa-cc-visa {
  --fa: "\f1f0";
}

.fa-cc-mastercard {
  --fa: "\f1f1";
}

.fa-cc-discover {
  --fa: "\f1f2";
}

.fa-cc-amex {
  --fa: "\f1f3";
}

.fa-cc-paypal {
  --fa: "\f1f4";
}

.fa-cc-stripe {
  --fa: "\f1f5";
}

.fa-lastfm {
  --fa: "\f202";
}

.fa-square-lastfm {
  --fa: "\f203";
}

.fa-lastfm-square {
  --fa: "\f203";
}

.fa-ioxhost {
  --fa: "\f208";
}

.fa-angellist {
  --fa: "\f209";
}

.fa-buysellads {
  --fa: "\f20d";
}

.fa-connectdevelop {
  --fa: "\f20e";
}

.fa-dashcube {
  --fa: "\f210";
}

.fa-forumbee {
  --fa: "\f211";
}

.fa-leanpub {
  --fa: "\f212";
}

.fa-sellsy {
  --fa: "\f213";
}

.fa-shirtsinbulk {
  --fa: "\f214";
}

.fa-simplybuilt {
  --fa: "\f215";
}

.fa-skyatlas {
  --fa: "\f216";
}

.fa-pinterest-p {
  --fa: "\f231";
}

.fa-whatsapp {
  --fa: "\f232";
}

.fa-viacoin {
  --fa: "\f237";
}

.fa-medium {
  --fa: "\f23a";
}

.fa-medium-m {
  --fa: "\f23a";
}

.fa-y-combinator {
  --fa: "\f23b";
}

.fa-optin-monster {
  --fa: "\f23c";
}

.fa-opencart {
  --fa: "\f23d";
}

.fa-expeditedssl {
  --fa: "\f23e";
}

.fa-cc-jcb {
  --fa: "\f24b";
}

.fa-cc-diners-club {
  --fa: "\f24c";
}

.fa-creative-commons {
  --fa: "\f25e";
}

.fa-gg {
  --fa: "\f260";
}

.fa-gg-circle {
  --fa: "\f261";
}

.fa-odnoklassniki {
  --fa: "\f263";
}

.fa-square-odnoklassniki {
  --fa: "\f264";
}

.fa-odnoklassniki-square {
  --fa: "\f264";
}

.fa-get-pocket {
  --fa: "\f265";
}

.fa-wikipedia-w {
  --fa: "\f266";
}

.fa-safari {
  --fa: "\f267";
}

.fa-chrome {
  --fa: "\f268";
}

.fa-firefox {
  --fa: "\f269";
}

.fa-opera {
  --fa: "\f26a";
}

.fa-internet-explorer {
  --fa: "\f26b";
}

.fa-contao {
  --fa: "\f26d";
}

.fa-500px {
  --fa: "\f26e";
}

.fa-amazon {
  --fa: "\f270";
}

.fa-houzz {
  --fa: "\f27c";
}

.fa-vimeo-v {
  --fa: "\f27d";
}

.fa-black-tie {
  --fa: "\f27e";
}

.fa-fonticons {
  --fa: "\f280";
}

.fa-reddit-alien {
  --fa: "\f281";
}

.fa-edge {
  --fa: "\f282";
}

.fa-codiepie {
  --fa: "\f284";
}

.fa-modx {
  --fa: "\f285";
}

.fa-fort-awesome {
  --fa: "\f286";
}

.fa-usb {
  --fa: "\f287";
}

.fa-product-hunt {
  --fa: "\f288";
}

.fa-mixcloud {
  --fa: "\f289";
}

.fa-scribd {
  --fa: "\f28a";
}

.fa-bluetooth {
  --fa: "\f293";
}

.fa-bluetooth-b {
  --fa: "\f294";
}

.fa-gitlab {
  --fa: "\f296";
}

.fa-wpbeginner {
  --fa: "\f297";
}

.fa-wpforms {
  --fa: "\f298";
}

.fa-envira {
  --fa: "\f299";
}

.fa-glide {
  --fa: "\f2a5";
}

.fa-glide-g {
  --fa: "\f2a6";
}

.fa-viadeo {
  --fa: "\f2a9";
}

.fa-square-viadeo {
  --fa: "\f2aa";
}

.fa-viadeo-square {
  --fa: "\f2aa";
}

.fa-snapchat {
  --fa: "\f2ab";
}

.fa-snapchat-ghost {
  --fa: "\f2ab";
}

.fa-square-snapchat {
  --fa: "\f2ad";
}

.fa-snapchat-square {
  --fa: "\f2ad";
}

.fa-pied-piper {
  --fa: "\f2ae";
}

.fa-first-order {
  --fa: "\f2b0";
}

.fa-yoast {
  --fa: "\f2b1";
}

.fa-themeisle {
  --fa: "\f2b2";
}

.fa-google-plus {
  --fa: "\f2b3";
}

.fa-font-awesome {
  --fa: "\f2b4";
}

.fa-font-awesome-flag {
  --fa: "\f2b4";
}

.fa-font-awesome-logo-full {
  --fa: "\f2b4";
}

.fa-linode {
  --fa: "\f2b8";
}

.fa-quora {
  --fa: "\f2c4";
}

.fa-free-code-camp {
  --fa: "\f2c5";
}

.fa-telegram {
  --fa: "\f2c6";
}

.fa-telegram-plane {
  --fa: "\f2c6";
}

.fa-bandcamp {
  --fa: "\f2d5";
}

.fa-grav {
  --fa: "\f2d6";
}

.fa-etsy {
  --fa: "\f2d7";
}

.fa-imdb {
  --fa: "\f2d8";
}

.fa-ravelry {
  --fa: "\f2d9";
}

.fa-sellcast {
  --fa: "\f2da";
}

.fa-superpowers {
  --fa: "\f2dd";
}

.fa-wpexplorer {
  --fa: "\f2de";
}

.fa-meetup {
  --fa: "\f2e0";
}

.fa-square-font-awesome-stroke {
  --fa: "\f35c";
}

.fa-font-awesome-alt {
  --fa: "\f35c";
}

.fa-accessible-icon {
  --fa: "\f368";
}

.fa-accusoft {
  --fa: "\f369";
}

.fa-adversal {
  --fa: "\f36a";
}

.fa-affiliatetheme {
  --fa: "\f36b";
}

.fa-algolia {
  --fa: "\f36c";
}

.fa-amilia {
  --fa: "\f36d";
}

.fa-angrycreative {
  --fa: "\f36e";
}

.fa-app-store {
  --fa: "\f36f";
}

.fa-app-store-ios {
  --fa: "\f370";
}

.fa-apper {
  --fa: "\f371";
}

.fa-asymmetrik {
  --fa: "\f372";
}

.fa-audible {
  --fa: "\f373";
}

.fa-avianex {
  --fa: "\f374";
}

.fa-aws {
  --fa: "\f375";
}

.fa-bimobject {
  --fa: "\f378";
}

.fa-bitcoin {
  --fa: "\f379";
}

.fa-bity {
  --fa: "\f37a";
}

.fa-blackberry {
  --fa: "\f37b";
}

.fa-blogger {
  --fa: "\f37c";
}

.fa-blogger-b {
  --fa: "\f37d";
}

.fa-buromobelexperte {
  --fa: "\f37f";
}

.fa-centercode {
  --fa: "\f380";
}

.fa-cloudscale {
  --fa: "\f383";
}

.fa-cloudsmith {
  --fa: "\f384";
}

.fa-cloudversify {
  --fa: "\f385";
}

.fa-cpanel {
  --fa: "\f388";
}

.fa-css3-alt {
  --fa: "\f38b";
}

.fa-cuttlefish {
  --fa: "\f38c";
}

.fa-d-and-d {
  --fa: "\f38d";
}

.fa-deploydog {
  --fa: "\f38e";
}

.fa-deskpro {
  --fa: "\f38f";
}

.fa-digital-ocean {
  --fa: "\f391";
}

.fa-discord {
  --fa: "\f392";
}

.fa-discourse {
  --fa: "\f393";
}

.fa-dochub {
  --fa: "\f394";
}

.fa-docker {
  --fa: "\f395";
}

.fa-draft2digital {
  --fa: "\f396";
}

.fa-square-dribbble {
  --fa: "\f397";
}

.fa-dribbble-square {
  --fa: "\f397";
}

.fa-dyalog {
  --fa: "\f399";
}

.fa-earlybirds {
  --fa: "\f39a";
}

.fa-erlang {
  --fa: "\f39d";
}

.fa-facebook-f {
  --fa: "\f39e";
}

.fa-facebook-messenger {
  --fa: "\f39f";
}

.fa-firstdraft {
  --fa: "\f3a1";
}

.fa-fonticons-fi {
  --fa: "\f3a2";
}

.fa-fort-awesome-alt {
  --fa: "\f3a3";
}

.fa-freebsd {
  --fa: "\f3a4";
}

.fa-gitkraken {
  --fa: "\f3a6";
}

.fa-gofore {
  --fa: "\f3a7";
}

.fa-goodreads {
  --fa: "\f3a8";
}

.fa-goodreads-g {
  --fa: "\f3a9";
}

.fa-google-drive {
  --fa: "\f3aa";
}

.fa-google-play {
  --fa: "\f3ab";
}

.fa-gripfire {
  --fa: "\f3ac";
}

.fa-grunt {
  --fa: "\f3ad";
}

.fa-gulp {
  --fa: "\f3ae";
}

.fa-square-hacker-news {
  --fa: "\f3af";
}

.fa-hacker-news-square {
  --fa: "\f3af";
}

.fa-hire-a-helper {
  --fa: "\f3b0";
}

.fa-hotjar {
  --fa: "\f3b1";
}

.fa-hubspot {
  --fa: "\f3b2";
}

.fa-itunes {
  --fa: "\f3b4";
}

.fa-itunes-note {
  --fa: "\f3b5";
}

.fa-jenkins {
  --fa: "\f3b6";
}

.fa-joget {
  --fa: "\f3b7";
}

.fa-js {
  --fa: "\f3b8";
}

.fa-square-js {
  --fa: "\f3b9";
}

.fa-js-square {
  --fa: "\f3b9";
}

.fa-keycdn {
  --fa: "\f3ba";
}

.fa-kickstarter {
  --fa: "\f3bb";
}

.fa-square-kickstarter {
  --fa: "\f3bb";
}

.fa-kickstarter-k {
  --fa: "\f3bc";
}

.fa-laravel {
  --fa: "\f3bd";
}

.fa-line {
  --fa: "\f3c0";
}

.fa-lyft {
  --fa: "\f3c3";
}

.fa-magento {
  --fa: "\f3c4";
}

.fa-medapps {
  --fa: "\f3c6";
}

.fa-medrt {
  --fa: "\f3c8";
}

.fa-microsoft {
  --fa: "\f3ca";
}

.fa-mix {
  --fa: "\f3cb";
}

.fa-mizuni {
  --fa: "\f3cc";
}

.fa-monero {
  --fa: "\f3d0";
}

.fa-napster {
  --fa: "\f3d2";
}

.fa-node-js {
  --fa: "\f3d3";
}

.fa-npm {
  --fa: "\f3d4";
}

.fa-ns8 {
  --fa: "\f3d5";
}

.fa-nutritionix {
  --fa: "\f3d6";
}

.fa-page4 {
  --fa: "\f3d7";
}

.fa-palfed {
  --fa: "\f3d8";
}

.fa-patreon {
  --fa: "\f3d9";
}

.fa-periscope {
  --fa: "\f3da";
}

.fa-phabricator {
  --fa: "\f3db";
}

.fa-phoenix-framework {
  --fa: "\f3dc";
}

.fa-playstation {
  --fa: "\f3df";
}

.fa-pushed {
  --fa: "\f3e1";
}

.fa-python {
  --fa: "\f3e2";
}

.fa-red-river {
  --fa: "\f3e3";
}

.fa-wpressr {
  --fa: "\f3e4";
}

.fa-rendact {
  --fa: "\f3e4";
}

.fa-replyd {
  --fa: "\f3e6";
}

.fa-resolving {
  --fa: "\f3e7";
}

.fa-rocketchat {
  --fa: "\f3e8";
}

.fa-rockrms {
  --fa: "\f3e9";
}

.fa-schlix {
  --fa: "\f3ea";
}

.fa-searchengin {
  --fa: "\f3eb";
}

.fa-servicestack {
  --fa: "\f3ec";
}

.fa-sistrix {
  --fa: "\f3ee";
}

.fa-speakap {
  --fa: "\f3f3";
}

.fa-staylinked {
  --fa: "\f3f5";
}

.fa-steam-symbol {
  --fa: "\f3f6";
}

.fa-sticker-mule {
  --fa: "\f3f7";
}

.fa-studiovinari {
  --fa: "\f3f8";
}

.fa-supple {
  --fa: "\f3f9";
}

.fa-uber {
  --fa: "\f402";
}

.fa-uikit {
  --fa: "\f403";
}

.fa-uniregistry {
  --fa: "\f404";
}

.fa-untappd {
  --fa: "\f405";
}

.fa-ussunnah {
  --fa: "\f407";
}

.fa-vaadin {
  --fa: "\f408";
}

.fa-viber {
  --fa: "\f409";
}

.fa-vimeo {
  --fa: "\f40a";
}

.fa-vnv {
  --fa: "\f40b";
}

.fa-square-whatsapp {
  --fa: "\f40c";
}

.fa-whatsapp-square {
  --fa: "\f40c";
}

.fa-whmcs {
  --fa: "\f40d";
}

.fa-wordpress-simple {
  --fa: "\f411";
}

.fa-xbox {
  --fa: "\f412";
}

.fa-yandex {
  --fa: "\f413";
}

.fa-yandex-international {
  --fa: "\f414";
}

.fa-apple-pay {
  --fa: "\f415";
}

.fa-cc-apple-pay {
  --fa: "\f416";
}

.fa-fly {
  --fa: "\f417";
}

.fa-node {
  --fa: "\f419";
}

.fa-osi {
  --fa: "\f41a";
}

.fa-react {
  --fa: "\f41b";
}

.fa-autoprefixer {
  --fa: "\f41c";
}

.fa-less {
  --fa: "\f41d";
}

.fa-sass {
  --fa: "\f41e";
}

.fa-vuejs {
  --fa: "\f41f";
}

.fa-angular {
  --fa: "\f420";
}

.fa-aviato {
  --fa: "\f421";
}

.fa-ember {
  --fa: "\f423";
}

.fa-gitter {
  --fa: "\f426";
}

.fa-hooli {
  --fa: "\f427";
}

.fa-strava {
  --fa: "\f428";
}

.fa-stripe {
  --fa: "\f429";
}

.fa-stripe-s {
  --fa: "\f42a";
}

.fa-typo3 {
  --fa: "\f42b";
}

.fa-amazon-pay {
  --fa: "\f42c";
}

.fa-cc-amazon-pay {
  --fa: "\f42d";
}

.fa-ethereum {
  --fa: "\f42e";
}

.fa-korvue {
  --fa: "\f42f";
}

.fa-elementor {
  --fa: "\f430";
}

.fa-square-youtube {
  --fa: "\f431";
}

.fa-youtube-square {
  --fa: "\f431";
}

.fa-flipboard {
  --fa: "\f44d";
}

.fa-hips {
  --fa: "\f452";
}

.fa-php {
  --fa: "\f457";
}

.fa-quinscape {
  --fa: "\f459";
}

.fa-readme {
  --fa: "\f4d5";
}

.fa-java {
  --fa: "\f4e4";
}

.fa-pied-piper-hat {
  --fa: "\f4e5";
}

.fa-creative-commons-by {
  --fa: "\f4e7";
}

.fa-creative-commons-nc {
  --fa: "\f4e8";
}

.fa-creative-commons-nc-eu {
  --fa: "\f4e9";
}

.fa-creative-commons-nc-jp {
  --fa: "\f4ea";
}

.fa-creative-commons-nd {
  --fa: "\f4eb";
}

.fa-creative-commons-pd {
  --fa: "\f4ec";
}

.fa-creative-commons-pd-alt {
  --fa: "\f4ed";
}

.fa-creative-commons-remix {
  --fa: "\f4ee";
}

.fa-creative-commons-sa {
  --fa: "\f4ef";
}

.fa-creative-commons-sampling {
  --fa: "\f4f0";
}

.fa-creative-commons-sampling-plus {
  --fa: "\f4f1";
}

.fa-creative-commons-share {
  --fa: "\f4f2";
}

.fa-creative-commons-zero {
  --fa: "\f4f3";
}

.fa-ebay {
  --fa: "\f4f4";
}

.fa-keybase {
  --fa: "\f4f5";
}

.fa-mastodon {
  --fa: "\f4f6";
}

.fa-r-project {
  --fa: "\f4f7";
}

.fa-researchgate {
  --fa: "\f4f8";
}

.fa-teamspeak {
  --fa: "\f4f9";
}

.fa-first-order-alt {
  --fa: "\f50a";
}

.fa-fulcrum {
  --fa: "\f50b";
}

.fa-galactic-republic {
  --fa: "\f50c";
}

.fa-galactic-senate {
  --fa: "\f50d";
}

.fa-jedi-order {
  --fa: "\f50e";
}

.fa-mandalorian {
  --fa: "\f50f";
}

.fa-old-republic {
  --fa: "\f510";
}

.fa-phoenix-squadron {
  --fa: "\f511";
}

.fa-sith {
  --fa: "\f512";
}

.fa-trade-federation {
  --fa: "\f513";
}

.fa-wolf-pack-battalion {
  --fa: "\f514";
}

.fa-hornbill {
  --fa: "\f592";
}

.fa-mailchimp {
  --fa: "\f59e";
}

.fa-megaport {
  --fa: "\f5a3";
}

.fa-nimblr {
  --fa: "\f5a8";
}

.fa-rev {
  --fa: "\f5b2";
}

.fa-shopware {
  --fa: "\f5b5";
}

.fa-squarespace {
  --fa: "\f5be";
}

.fa-themeco {
  --fa: "\f5c6";
}

.fa-weebly {
  --fa: "\f5cc";
}

.fa-wix {
  --fa: "\f5cf";
}

.fa-ello {
  --fa: "\f5f1";
}

.fa-hackerrank {
  --fa: "\f5f7";
}

.fa-kaggle {
  --fa: "\f5fa";
}

.fa-markdown {
  --fa: "\f60f";
}

.fa-neos {
  --fa: "\f612";
}

.fa-zhihu {
  --fa: "\f63f";
}

.fa-alipay {
  --fa: "\f642";
}

.fa-the-red-yeti {
  --fa: "\f69d";
}

.fa-critical-role {
  --fa: "\f6c9";
}

.fa-d-and-d-beyond {
  --fa: "\f6ca";
}

.fa-dev {
  --fa: "\f6cc";
}

.fa-fantasy-flight-games {
  --fa: "\f6dc";
}

.fa-wizards-of-the-coast {
  --fa: "\f730";
}

.fa-think-peaks {
  --fa: "\f731";
}

.fa-reacteurope {
  --fa: "\f75d";
}

.fa-artstation {
  --fa: "\f77a";
}

.fa-atlassian {
  --fa: "\f77b";
}

.fa-canadian-maple-leaf {
  --fa: "\f785";
}

.fa-centos {
  --fa: "\f789";
}

.fa-confluence {
  --fa: "\f78d";
}

.fa-dhl {
  --fa: "\f790";
}

.fa-diaspora {
  --fa: "\f791";
}

.fa-fedex {
  --fa: "\f797";
}

.fa-fedora {
  --fa: "\f798";
}

.fa-figma {
  --fa: "\f799";
}

.fa-intercom {
  --fa: "\f7af";
}

.fa-invision {
  --fa: "\f7b0";
}

.fa-jira {
  --fa: "\f7b1";
}

.fa-mendeley {
  --fa: "\f7b3";
}

.fa-raspberry-pi {
  --fa: "\f7bb";
}

.fa-redhat {
  --fa: "\f7bc";
}

.fa-sketch {
  --fa: "\f7c6";
}

.fa-sourcetree {
  --fa: "\f7d3";
}

.fa-suse {
  --fa: "\f7d6";
}

.fa-ubuntu {
  --fa: "\f7df";
}

.fa-ups {
  --fa: "\f7e0";
}

.fa-usps {
  --fa: "\f7e1";
}

.fa-yarn {
  --fa: "\f7e3";
}

.fa-airbnb {
  --fa: "\f834";
}

.fa-battle-net {
  --fa: "\f835";
}

.fa-bootstrap {
  --fa: "\f836";
}

.fa-buffer {
  --fa: "\f837";
}

.fa-chromecast {
  --fa: "\f838";
}

.fa-evernote {
  --fa: "\f839";
}

.fa-itch-io {
  --fa: "\f83a";
}

.fa-salesforce {
  --fa: "\f83b";
}

.fa-speaker-deck {
  --fa: "\f83c";
}

.fa-symfony {
  --fa: "\f83d";
}

.fa-waze {
  --fa: "\f83f";
}

.fa-yammer {
  --fa: "\f840";
}

.fa-git-alt {
  --fa: "\f841";
}

.fa-stackpath {
  --fa: "\f842";
}

.fa-cotton-bureau {
  --fa: "\f89e";
}

.fa-buy-n-large {
  --fa: "\f8a6";
}

.fa-mdb {
  --fa: "\f8ca";
}

.fa-orcid {
  --fa: "\f8d2";
}

.fa-swift {
  --fa: "\f8e1";
}

.fa-umbraco {
  --fa: "\f8e8";
}:root, :host {
  --fa-family-classic: "Font Awesome 7 Free";
  --fa-font-regular: normal 400 1em/1 var(--fa-family-classic);
  /* deprecated: this older custom property will be removed next major release */
  --fa-style-family-classic: var(--fa-family-classic);
}

@font-face {
  font-family: "Font Awesome 7 Free";
  font-style: normal;
  font-weight: 400;
  font-display: block;
  src: url("../webfonts/fa-regular-400.woff2");
}
.far {
  --fa-family: var(--fa-family-classic);
  --fa-style: 400;
}

.fa-classic {
  --fa-family: var(--fa-family-classic);
}

.fa-regular {
  --fa-style: 400;
}:root, :host {
  --fa-family-classic: "Font Awesome 7 Free";
  --fa-font-solid: normal 900 1em/1 var(--fa-family-classic);
  /* deprecated: this older custom property will be removed next major release */
  --fa-style-family-classic: var(--fa-family-classic);
}

@font-face {
  font-family: "Font Awesome 7 Free";
  font-style: normal;
  font-weight: 900;
  font-display: block;
  src: url("../webfonts/fa-solid-900.woff2");
}
.fas {
  --fa-family: var(--fa-family-classic);
  --fa-style: 900;
}

.fa-classic {
  --fa-family: var(--fa-family-classic);
}

.fa-solid {
  --fa-style: 900;
}@font-face {
  font-family: "Font Awesome 5 Brands";
  font-display: block;
  font-weight: 400;
  src: url("../webfonts/fa-brands-400.woff2") format("woff2");
}
@font-face {
  font-family: "Font Awesome 5 Free";
  font-display: block;
  font-weight: 900;
  src: url("../webfonts/fa-solid-900.woff2") format("woff2");
}
@font-face {
  font-family: "Font Awesome 5 Free";
  font-display: block;
  font-weight: 400;
  src: url("../webfonts/fa-regular-400.woff2") format("woff2");
}@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-solid-900.woff2") format("woff2");
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-brands-400.woff2") format("woff2");
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-regular-400.woff2") format("woff2");
  unicode-range: U+F003, U+F006, U+F014, U+F016-F017, U+F01A-F01B, U+F01D, U+F022, U+F03E, U+F044, U+F046, U+F05C-F05D, U+F06E, U+F070, U+F087-F088, U+F08A, U+F094, U+F096-F097, U+F09D, U+F0A0, U+F0A2, U+F0A4-F0A7, U+F0C5, U+F0C7, U+F0E5-F0E6, U+F0EB, U+F0F6-F0F8, U+F10C, U+F114-F115, U+F118-F11A, U+F11C-F11D, U+F133, U+F147, U+F14E, U+F150-F152, U+F185-F186, U+F18E, U+F190-F192, U+F196, U+F1C1-F1C9, U+F1D9, U+F1DB, U+F1E3, U+F1EA, U+F1F7, U+F1F9, U+F20A, U+F247-F248, U+F24A, U+F24D, U+F255-F25B, U+F25D, U+F271-F274, U+F278, U+F27B, U+F28C, U+F28E, U+F29C, U+F2B5, U+F2B7, U+F2BA, U+F2BC, U+F2BE, U+F2C0-F2C1, U+F2C3, U+F2D0, U+F2D2, U+F2D4, U+F2DC;
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-v4compatibility.woff2") format("woff2");
  unicode-range: U+F041, U+F047, U+F065-F066, U+F07D-F07E, U+F080, U+F08B, U+F08E, U+F090, U+F09A, U+F0AC, U+F0AE, U+F0B2, U+F0D0, U+F0D6, U+F0E4, U+F0EC, U+F10A-F10B, U+F123, U+F13E, U+F148-F149, U+F14C, U+F156, U+F15E, U+F160-F161, U+F163, U+F175-F178, U+F195, U+F1F8, U+F219, U+F27A;
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\all.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa,.fa-brands,.fa-classic,.fa-regular,.fa-solid,.fab,.far,.fas{--_fa-family:var(--fa-family,var(--fa-style-family,"Font Awesome 7 Free"));-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale;display:var(--fa-display,inline-block);font-family:var(--_fa-family);font-feature-settings:normal;font-style:normal;font-synthesis:none;font-variant:normal;font-weight:var(--fa-style,900);line-height:1;text-align:center;text-rendering:auto;width:var(--fa-width,1.25em)}:is(.fas,.far,.fab,.fa-solid,.fa-regular,.fa-brands,.fa-classic,.fa):before{content:var(--fa)/""}@supports not (content:""/""){:is(.fas,.far,.fab,.fa-solid,.fa-regular,.fa-brands,.fa-classic,.fa):before{content:var(--fa)}}.fa-1x{font-size:1em}.fa-2x{font-size:2em}.fa-3x{font-size:3em}.fa-4x{font-size:4em}.fa-5x{font-size:5em}.fa-6x{font-size:6em}.fa-7x{font-size:7em}.fa-8x{font-size:8em}.fa-9x{font-size:9em}.fa-10x{font-size:10em}.fa-2xs{font-size:.625em;line-height:.1em;vertical-align:.225em}.fa-xs{font-size:.75em;line-height:.08333em;vertical-align:.125em}.fa-sm{font-size:.875em;line-height:.07143em;vertical-align:.05357em}.fa-lg{font-size:1.25em;line-height:.05em;vertical-align:-.075em}.fa-xl{font-size:1.5em;line-height:.04167em;vertical-align:-.125em}.fa-2xl{font-size:2em;line-height:.03125em;vertical-align:-.1875em}.fa-width-auto{--fa-width:auto}.fa-fw,.fa-width-fixed{--fa-width:1.25em}.fa-ul{list-style-type:none;margin-inline-start:var(--fa-li-margin,2.5em);padding-inline-start:0}.fa-ul>li{position:relative}.fa-li{inset-inline-start:calc(var(--fa-li-width, 2em)*-1);position:absolute;text-align:center;width:var(--fa-li-width,2em);line-height:inherit}.fa-border{border-radius:var(--fa-border-radius,.1em);border:var(--fa-border-width,.0625em) var(--fa-border-style,solid) var(--fa-border-color,#eee);box-sizing:var(--fa-border-box-sizing,content-box);padding:var(--fa-border-padding,.1875em .25em)}.fa-pull-left,.fa-pull-start{float:inline-start;margin-inline-end:var(--fa-pull-margin,.3em)}.fa-pull-end,.fa-pull-right{float:inline-end;margin-inline-start:var(--fa-pull-margin,.3em)}.fa-beat{animation-name:fa-beat;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-bounce{animation-name:fa-bounce;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.28,.84,.42,1))}.fa-fade{animation-name:fa-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-beat-fade,.fa-fade{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s)}.fa-beat-fade{animation-name:fa-beat-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-flip{animation-name:fa-flip;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-shake{animation-name:fa-shake;animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-shake,.fa-spin{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal)}.fa-spin{animation-name:fa-spin;animation-duration:var(--fa-animation-duration,2s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-spin-reverse{--fa-animation-direction:reverse}.fa-pulse,.fa-spin-pulse{animation-name:fa-spin;animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,steps(8))}@media (prefers-reduced-motion:reduce){.fa-beat,.fa-beat-fade,.fa-bounce,.fa-fade,.fa-flip,.fa-pulse,.fa-shake,.fa-spin,.fa-spin-pulse{animation:none!important;transition:none!important}}@keyframes fa-beat{0%,90%{transform:scale(1)}45%{transform:scale(var(--fa-beat-scale,1.25))}}@keyframes fa-bounce{0%{transform:scale(1) translateY(0)}10%{transform:scale(var(--fa-bounce-start-scale-x,1.1),var(--fa-bounce-start-scale-y,.9)) translateY(0)}30%{transform:scale(var(--fa-bounce-jump-scale-x,.9),var(--fa-bounce-jump-scale-y,1.1)) translateY(var(--fa-bounce-height,-.5em))}50%{transform:scale(var(--fa-bounce-land-scale-x,1.05),var(--fa-bounce-land-scale-y,.95)) translateY(0)}57%{transform:scale(1) translateY(var(--fa-bounce-rebound,-.125em))}64%{transform:scale(1) translateY(0)}to{transform:scale(1) translateY(0)}}@keyframes fa-fade{50%{opacity:var(--fa-fade-opacity,.4)}}@keyframes fa-beat-fade{0%,to{opacity:var(--fa-beat-fade-opacity,.4);transform:scale(1)}50%{opacity:1;transform:scale(var(--fa-beat-fade-scale,1.125))}}@keyframes fa-flip{50%{transform:rotate3d(var(--fa-flip-x,0),var(--fa-flip-y,1),var(--fa-flip-z,0),var(--fa-flip-angle,-180deg))}}@keyframes fa-shake{0%{transform:rotate(-15deg)}4%{transform:rotate(15deg)}8%,24%{transform:rotate(-18deg)}12%,28%{transform:rotate(18deg)}16%{transform:rotate(-22deg)}20%{transform:rotate(22deg)}32%{transform:rotate(-12deg)}36%{transform:rotate(12deg)}40%,to{transform:rotate(0deg)}}@keyframes fa-spin{0%{transform:rotate(0deg)}to{transform:rotate(1turn)}}.fa-rotate-90{transform:rotate(90deg)}.fa-rotate-180{transform:rotate(180deg)}.fa-rotate-270{transform:rotate(270deg)}.fa-flip-horizontal{transform:scaleX(-1)}.fa-flip-vertical{transform:scaleY(-1)}.fa-flip-both,.fa-flip-horizontal.fa-flip-vertical{transform:scale(-1)}.fa-rotate-by{transform:rotate(var(--fa-rotate-angle,0))}.fa-stack{display:inline-block;height:2em;line-height:2em;position:relative;vertical-align:middle;width:2.5em}.fa-stack-1x,.fa-stack-2x{--fa-width:100%;inset:0;position:absolute;text-align:center;width:var(--fa-width);z-index:var(--fa-stack-z-index,auto)}.fa-stack-1x{line-height:inherit}.fa-stack-2x{font-size:2em}.fa-inverse{color:var(--fa-inverse,#fff)}

.fa-0{--fa:"\30 "}.fa-1{--fa:"\31 "}.fa-2{--fa:"\32 "}.fa-3{--fa:"\33 "}.fa-4{--fa:"\34 "}.fa-5{--fa:"\35 "}.fa-6{--fa:"\36 "}.fa-7{--fa:"\37 "}.fa-8{--fa:"\38 "}.fa-9{--fa:"\39 "}.fa-exclamation{--fa:"\!"}.fa-hashtag{--fa:"\#"}.fa-dollar,.fa-dollar-sign,.fa-usd{--fa:"\$"}.fa-percent,.fa-percentage{--fa:"\%"}.fa-asterisk{--fa:"\*"}.fa-add,.fa-plus{--fa:"\+"}.fa-less-than{--fa:"\<"}.fa-equals{--fa:"\="}.fa-greater-than{--fa:"\>"}.fa-question{--fa:"\?"}.fa-at{--fa:"\@"}.fa-a{--fa:"A"}.fa-b{--fa:"B"}.fa-c{--fa:"C"}.fa-d{--fa:"D"}.fa-e{--fa:"E"}.fa-f{--fa:"F"}.fa-g{--fa:"G"}.fa-h{--fa:"H"}.fa-i{--fa:"I"}.fa-j{--fa:"J"}.fa-k{--fa:"K"}.fa-l{--fa:"L"}.fa-m{--fa:"M"}.fa-n{--fa:"N"}.fa-o{--fa:"O"}.fa-p{--fa:"P"}.fa-q{--fa:"Q"}.fa-r{--fa:"R"}.fa-s{--fa:"S"}.fa-t{--fa:"T"}.fa-u{--fa:"U"}.fa-v{--fa:"V"}.fa-w{--fa:"W"}.fa-x{--fa:"X"}.fa-y{--fa:"Y"}.fa-z{--fa:"Z"}.fa-faucet{--fa:"\e005"}.fa-faucet-drip{--fa:"\e006"}.fa-house-chimney-window{--fa:"\e00d"}.fa-house-signal{--fa:"\e012"}.fa-temperature-arrow-down,.fa-temperature-down{--fa:"\e03f"}.fa-temperature-arrow-up,.fa-temperature-up{--fa:"\e040"}.fa-trailer{--fa:"\e041"}.fa-bacteria{--fa:"\e059"}.fa-bacterium{--fa:"\e05a"}.fa-box-tissue{--fa:"\e05b"}.fa-hand-holding-medical{--fa:"\e05c"}.fa-hand-sparkles{--fa:"\e05d"}.fa-hands-bubbles,.fa-hands-wash{--fa:"\e05e"}.fa-handshake-alt-slash,.fa-handshake-simple-slash,.fa-handshake-slash{--fa:"\e060"}.fa-head-side-cough{--fa:"\e061"}.fa-head-side-cough-slash{--fa:"\e062"}.fa-head-side-mask{--fa:"\e063"}.fa-head-side-virus{--fa:"\e064"}.fa-house-chimney-user{--fa:"\e065"}.fa-house-laptop,.fa-laptop-house{--fa:"\e066"}.fa-lungs-virus{--fa:"\e067"}.fa-people-arrows,.fa-people-arrows-left-right{--fa:"\e068"}.fa-plane-slash{--fa:"\e069"}.fa-pump-medical{--fa:"\e06a"}.fa-pump-soap{--fa:"\e06b"}.fa-shield-virus{--fa:"\e06c"}.fa-sink{--fa:"\e06d"}.fa-soap{--fa:"\e06e"}.fa-stopwatch-20{--fa:"\e06f"}.fa-shop-slash,.fa-store-alt-slash{--fa:"\e070"}.fa-store-slash{--fa:"\e071"}.fa-toilet-paper-slash{--fa:"\e072"}.fa-users-slash{--fa:"\e073"}.fa-virus{--fa:"\e074"}.fa-virus-slash{--fa:"\e075"}.fa-viruses{--fa:"\e076"}.fa-vest{--fa:"\e085"}.fa-vest-patches{--fa:"\e086"}.fa-arrow-trend-down{--fa:"\e097"}.fa-arrow-trend-up{--fa:"\e098"}.fa-arrow-up-from-bracket{--fa:"\e09a"}.fa-austral-sign{--fa:"\e0a9"}.fa-baht-sign{--fa:"\e0ac"}.fa-bitcoin-sign{--fa:"\e0b4"}.fa-bolt-lightning{--fa:"\e0b7"}.fa-book-bookmark{--fa:"\e0bb"}.fa-camera-rotate{--fa:"\e0d8"}.fa-cedi-sign{--fa:"\e0df"}.fa-chart-column{--fa:"\e0e3"}.fa-chart-gantt{--fa:"\e0e4"}.fa-clapperboard{--fa:"\e131"}.fa-clover{--fa:"\e139"}.fa-code-compare{--fa:"\e13a"}.fa-code-fork{--fa:"\e13b"}.fa-code-pull-request{--fa:"\e13c"}.fa-colon-sign{--fa:"\e140"}.fa-cruzeiro-sign{--fa:"\e152"}.fa-display{--fa:"\e163"}.fa-dong-sign{--fa:"\e169"}.fa-elevator{--fa:"\e16d"}.fa-filter-circle-xmark{--fa:"\e17b"}.fa-florin-sign{--fa:"\e184"}.fa-folder-closed{--fa:"\e185"}.fa-franc-sign{--fa:"\e18f"}.fa-guarani-sign{--fa:"\e19a"}.fa-gun{--fa:"\e19b"}.fa-hands-clapping{--fa:"\e1a8"}.fa-home-user,.fa-house-user{--fa:"\e1b0"}.fa-indian-rupee,.fa-indian-rupee-sign,.fa-inr{--fa:"\e1bc"}.fa-kip-sign{--fa:"\e1c4"}.fa-lari-sign{--fa:"\e1c8"}.fa-litecoin-sign{--fa:"\e1d3"}.fa-manat-sign{--fa:"\e1d5"}.fa-mask-face{--fa:"\e1d7"}.fa-mill-sign{--fa:"\e1ed"}.fa-money-bills{--fa:"\e1f3"}.fa-naira-sign{--fa:"\e1f6"}.fa-notdef{--fa:"\e1fe"}.fa-panorama{--fa:"\e209"}.fa-peseta-sign{--fa:"\e221"}.fa-peso-sign{--fa:"\e222"}.fa-plane-up{--fa:"\e22d"}.fa-rupiah-sign{--fa:"\e23d"}.fa-stairs{--fa:"\e289"}.fa-timeline{--fa:"\e29c"}.fa-truck-front{--fa:"\e2b7"}.fa-try,.fa-turkish-lira,.fa-turkish-lira-sign{--fa:"\e2bb"}.fa-vault{--fa:"\e2c5"}.fa-magic-wand-sparkles,.fa-wand-magic-sparkles{--fa:"\e2ca"}.fa-wheat-alt,.fa-wheat-awn{--fa:"\e2cd"}.fa-wheelchair-alt,.fa-wheelchair-move{--fa:"\e2ce"}.fa-bangladeshi-taka-sign{--fa:"\e2e6"}.fa-bowl-rice{--fa:"\e2eb"}.fa-person-pregnant{--fa:"\e31e"}.fa-home-lg,.fa-house-chimney{--fa:"\e3af"}.fa-house-crack{--fa:"\e3b1"}.fa-house-medical{--fa:"\e3b2"}.fa-cent-sign{--fa:"\e3f5"}.fa-plus-minus{--fa:"\e43c"}.fa-sailboat{--fa:"\e445"}.fa-section{--fa:"\e447"}.fa-shrimp{--fa:"\e448"}.fa-brazilian-real-sign{--fa:"\e46c"}.fa-chart-simple{--fa:"\e473"}.fa-diagram-next{--fa:"\e476"}.fa-diagram-predecessor{--fa:"\e477"}.fa-diagram-successor{--fa:"\e47a"}.fa-earth-oceania,.fa-globe-oceania{--fa:"\e47b"}.fa-bug-slash{--fa:"\e490"}.fa-file-circle-plus{--fa:"\e494"}.fa-shop-lock{--fa:"\e4a5"}.fa-virus-covid{--fa:"\e4a8"}.fa-virus-covid-slash{--fa:"\e4a9"}.fa-anchor-circle-check{--fa:"\e4aa"}.fa-anchor-circle-exclamation{--fa:"\e4ab"}.fa-anchor-circle-xmark{--fa:"\e4ac"}.fa-anchor-lock{--fa:"\e4ad"}.fa-arrow-down-up-across-line{--fa:"\e4af"}.fa-arrow-down-up-lock{--fa:"\e4b0"}.fa-arrow-right-to-city{--fa:"\e4b3"}.fa-arrow-up-from-ground-water{--fa:"\e4b5"}.fa-arrow-up-from-water-pump{--fa:"\e4b6"}.fa-arrow-up-right-dots{--fa:"\e4b7"}.fa-arrows-down-to-line{--fa:"\e4b8"}.fa-arrows-down-to-people{--fa:"\e4b9"}.fa-arrows-left-right-to-line{--fa:"\e4ba"}.fa-arrows-spin{--fa:"\e4bb"}.fa-arrows-split-up-and-left{--fa:"\e4bc"}.fa-arrows-to-circle{--fa:"\e4bd"}.fa-arrows-to-dot{--fa:"\e4be"}.fa-arrows-to-eye{--fa:"\e4bf"}.fa-arrows-turn-right{--fa:"\e4c0"}.fa-arrows-turn-to-dots{--fa:"\e4c1"}.fa-arrows-up-to-line{--fa:"\e4c2"}.fa-bore-hole{--fa:"\e4c3"}.fa-bottle-droplet{--fa:"\e4c4"}.fa-bottle-water{--fa:"\e4c5"}.fa-bowl-food{--fa:"\e4c6"}.fa-boxes-packing{--fa:"\e4c7"}.fa-bridge{--fa:"\e4c8"}.fa-bridge-circle-check{--fa:"\e4c9"}.fa-bridge-circle-exclamation{--fa:"\e4ca"}.fa-bridge-circle-xmark{--fa:"\e4cb"}.fa-bridge-lock{--fa:"\e4cc"}.fa-bridge-water{--fa:"\e4ce"}.fa-bucket{--fa:"\e4cf"}.fa-bugs{--fa:"\e4d0"}.fa-building-circle-arrow-right{--fa:"\e4d1"}.fa-building-circle-check{--fa:"\e4d2"}.fa-building-circle-exclamation{--fa:"\e4d3"}.fa-building-circle-xmark{--fa:"\e4d4"}.fa-building-flag{--fa:"\e4d5"}.fa-building-lock{--fa:"\e4d6"}.fa-building-ngo{--fa:"\e4d7"}.fa-building-shield{--fa:"\e4d8"}.fa-building-un{--fa:"\e4d9"}.fa-building-user{--fa:"\e4da"}.fa-building-wheat{--fa:"\e4db"}.fa-burst{--fa:"\e4dc"}.fa-car-on{--fa:"\e4dd"}.fa-car-tunnel{--fa:"\e4de"}.fa-child-combatant,.fa-child-rifle{--fa:"\e4e0"}.fa-children{--fa:"\e4e1"}.fa-circle-nodes{--fa:"\e4e2"}.fa-clipboard-question{--fa:"\e4e3"}.fa-cloud-showers-water{--fa:"\e4e4"}.fa-computer{--fa:"\e4e5"}.fa-cubes-stacked{--fa:"\e4e6"}.fa-envelope-circle-check{--fa:"\e4e8"}.fa-explosion{--fa:"\e4e9"}.fa-ferry{--fa:"\e4ea"}.fa-file-circle-exclamation{--fa:"\e4eb"}.fa-file-circle-minus{--fa:"\e4ed"}.fa-file-circle-question{--fa:"\e4ef"}.fa-file-shield{--fa:"\e4f0"}.fa-fire-burner{--fa:"\e4f1"}.fa-fish-fins{--fa:"\e4f2"}.fa-flask-vial{--fa:"\e4f3"}.fa-glass-water{--fa:"\e4f4"}.fa-glass-water-droplet{--fa:"\e4f5"}.fa-group-arrows-rotate{--fa:"\e4f6"}.fa-hand-holding-hand{--fa:"\e4f7"}.fa-handcuffs{--fa:"\e4f8"}.fa-hands-bound{--fa:"\e4f9"}.fa-hands-holding-child{--fa:"\e4fa"}.fa-hands-holding-circle{--fa:"\e4fb"}.fa-heart-circle-bolt{--fa:"\e4fc"}.fa-heart-circle-check{--fa:"\e4fd"}.fa-heart-circle-exclamation{--fa:"\e4fe"}.fa-heart-circle-minus{--fa:"\e4ff"}.fa-heart-circle-plus{--fa:"\e500"}.fa-heart-circle-xmark{--fa:"\e501"}.fa-helicopter-symbol{--fa:"\e502"}.fa-helmet-un{--fa:"\e503"}.fa-hill-avalanche{--fa:"\e507"}.fa-hill-rockslide{--fa:"\e508"}.fa-house-circle-check{--fa:"\e509"}.fa-house-circle-exclamation{--fa:"\e50a"}.fa-house-circle-xmark{--fa:"\e50b"}.fa-house-fire{--fa:"\e50c"}.fa-house-flag{--fa:"\e50d"}.fa-house-flood-water{--fa:"\e50e"}.fa-house-flood-water-circle-arrow-right{--fa:"\e50f"}.fa-house-lock{--fa:"\e510"}.fa-house-medical-circle-check{--fa:"\e511"}.fa-house-medical-circle-exclamation{--fa:"\e512"}.fa-house-medical-circle-xmark{--fa:"\e513"}.fa-house-medical-flag{--fa:"\e514"}.fa-house-tsunami{--fa:"\e515"}.fa-jar{--fa:"\e516"}.fa-jar-wheat{--fa:"\e517"}.fa-jet-fighter-up{--fa:"\e518"}.fa-jug-detergent{--fa:"\e519"}.fa-kitchen-set{--fa:"\e51a"}.fa-land-mine-on{--fa:"\e51b"}.fa-landmark-flag{--fa:"\e51c"}.fa-laptop-file{--fa:"\e51d"}.fa-lines-leaning{--fa:"\e51e"}.fa-location-pin-lock{--fa:"\e51f"}.fa-locust{--fa:"\e520"}.fa-magnifying-glass-arrow-right{--fa:"\e521"}.fa-magnifying-glass-chart{--fa:"\e522"}.fa-mars-and-venus-burst{--fa:"\e523"}.fa-mask-ventilator{--fa:"\e524"}.fa-mattress-pillow{--fa:"\e525"}.fa-mobile-retro{--fa:"\e527"}.fa-money-bill-transfer{--fa:"\e528"}.fa-money-bill-trend-up{--fa:"\e529"}.fa-money-bill-wheat{--fa:"\e52a"}.fa-mosquito{--fa:"\e52b"}.fa-mosquito-net{--fa:"\e52c"}.fa-mound{--fa:"\e52d"}.fa-mountain-city{--fa:"\e52e"}.fa-mountain-sun{--fa:"\e52f"}.fa-oil-well{--fa:"\e532"}.fa-people-group{--fa:"\e533"}.fa-people-line{--fa:"\e534"}.fa-people-pulling{--fa:"\e535"}.fa-people-robbery{--fa:"\e536"}.fa-people-roof{--fa:"\e537"}.fa-person-arrow-down-to-line{--fa:"\e538"}.fa-person-arrow-up-from-line{--fa:"\e539"}.fa-person-breastfeeding{--fa:"\e53a"}.fa-person-burst{--fa:"\e53b"}.fa-person-cane{--fa:"\e53c"}.fa-person-chalkboard{--fa:"\e53d"}.fa-person-circle-check{--fa:"\e53e"}.fa-person-circle-exclamation{--fa:"\e53f"}.fa-person-circle-minus{--fa:"\e540"}.fa-person-circle-plus{--fa:"\e541"}.fa-person-circle-question{--fa:"\e542"}.fa-person-circle-xmark{--fa:"\e543"}.fa-person-dress-burst{--fa:"\e544"}.fa-person-drowning{--fa:"\e545"}.fa-person-falling{--fa:"\e546"}.fa-person-falling-burst{--fa:"\e547"}.fa-person-half-dress{--fa:"\e548"}.fa-person-harassing{--fa:"\e549"}.fa-person-military-pointing{--fa:"\e54a"}.fa-person-military-rifle{--fa:"\e54b"}.fa-person-military-to-person{--fa:"\e54c"}.fa-person-rays{--fa:"\e54d"}.fa-person-rifle{--fa:"\e54e"}.fa-person-shelter{--fa:"\e54f"}.fa-person-walking-arrow-loop-left{--fa:"\e551"}.fa-person-walking-arrow-right{--fa:"\e552"}.fa-person-walking-dashed-line-arrow-right{--fa:"\e553"}.fa-person-walking-luggage{--fa:"\e554"}.fa-plane-circle-check{--fa:"\e555"}.fa-plane-circle-exclamation{--fa:"\e556"}.fa-plane-circle-xmark{--fa:"\e557"}.fa-plane-lock{--fa:"\e558"}.fa-plate-wheat{--fa:"\e55a"}.fa-plug-circle-bolt{--fa:"\e55b"}.fa-plug-circle-check{--fa:"\e55c"}.fa-plug-circle-exclamation{--fa:"\e55d"}.fa-plug-circle-minus{--fa:"\e55e"}.fa-plug-circle-plus{--fa:"\e55f"}.fa-plug-circle-xmark{--fa:"\e560"}.fa-ranking-star{--fa:"\e561"}.fa-road-barrier{--fa:"\e562"}.fa-road-bridge{--fa:"\e563"}.fa-road-circle-check{--fa:"\e564"}.fa-road-circle-exclamation{--fa:"\e565"}.fa-road-circle-xmark{--fa:"\e566"}.fa-road-lock{--fa:"\e567"}.fa-road-spikes{--fa:"\e568"}.fa-rug{--fa:"\e569"}.fa-sack-xmark{--fa:"\e56a"}.fa-school-circle-check{--fa:"\e56b"}.fa-school-circle-exclamation{--fa:"\e56c"}.fa-school-circle-xmark{--fa:"\e56d"}.fa-school-flag{--fa:"\e56e"}.fa-school-lock{--fa:"\e56f"}.fa-sheet-plastic{--fa:"\e571"}.fa-shield-cat{--fa:"\e572"}.fa-shield-dog{--fa:"\e573"}.fa-shield-heart{--fa:"\e574"}.fa-square-nfi{--fa:"\e576"}.fa-square-person-confined{--fa:"\e577"}.fa-square-virus{--fa:"\e578"}.fa-rod-asclepius,.fa-rod-snake,.fa-staff-aesculapius,.fa-staff-snake{--fa:"\e579"}.fa-sun-plant-wilt{--fa:"\e57a"}.fa-tarp{--fa:"\e57b"}.fa-tarp-droplet{--fa:"\e57c"}.fa-tent{--fa:"\e57d"}.fa-tent-arrow-down-to-line{--fa:"\e57e"}.fa-tent-arrow-left-right{--fa:"\e57f"}.fa-tent-arrow-turn-left{--fa:"\e580"}.fa-tent-arrows-down{--fa:"\e581"}.fa-tents{--fa:"\e582"}.fa-toilet-portable{--fa:"\e583"}.fa-toilets-portable{--fa:"\e584"}.fa-tower-cell{--fa:"\e585"}.fa-tower-observation{--fa:"\e586"}.fa-tree-city{--fa:"\e587"}.fa-trowel{--fa:"\e589"}.fa-trowel-bricks{--fa:"\e58a"}.fa-truck-arrow-right{--fa:"\e58b"}.fa-truck-droplet{--fa:"\e58c"}.fa-truck-field{--fa:"\e58d"}.fa-truck-field-un{--fa:"\e58e"}.fa-truck-plane{--fa:"\e58f"}.fa-users-between-lines{--fa:"\e591"}.fa-users-line{--fa:"\e592"}.fa-users-rays{--fa:"\e593"}.fa-users-rectangle{--fa:"\e594"}.fa-users-viewfinder{--fa:"\e595"}.fa-vial-circle-check{--fa:"\e596"}.fa-vial-virus{--fa:"\e597"}.fa-wheat-awn-circle-exclamation{--fa:"\e598"}.fa-worm{--fa:"\e599"}.fa-xmarks-lines{--fa:"\e59a"}.fa-child-dress{--fa:"\e59c"}.fa-child-reaching{--fa:"\e59d"}.fa-file-circle-check{--fa:"\e5a0"}.fa-file-circle-xmark{--fa:"\e5a1"}.fa-person-through-window{--fa:"\e5a9"}.fa-plant-wilt{--fa:"\e5aa"}.fa-stapler{--fa:"\e5af"}.fa-train-tram{--fa:"\e5b4"}.fa-table-cells-column-lock{--fa:"\e678"}.fa-table-cells-row-lock{--fa:"\e67a"}.fa-thumb-tack-slash,.fa-thumbtack-slash{--fa:"\e68f"}.fa-table-cells-row-unlock{--fa:"\e691"}.fa-chart-diagram{--fa:"\e695"}.fa-comment-nodes{--fa:"\e696"}.fa-file-fragment{--fa:"\e697"}.fa-file-half-dashed{--fa:"\e698"}.fa-hexagon-nodes{--fa:"\e699"}.fa-hexagon-nodes-bolt{--fa:"\e69a"}.fa-square-binary{--fa:"\e69b"}.fa-pentagon{--fa:"\e790"}.fa-non-binary{--fa:"\e807"}.fa-spiral{--fa:"\e80a"}.fa-mobile-vibrate{--fa:"\e816"}.fa-single-quote-left{--fa:"\e81b"}.fa-single-quote-right{--fa:"\e81c"}.fa-bus-side{--fa:"\e81d"}.fa-heptagon,.fa-septagon{--fa:"\e820"}.fa-glass-martini,.fa-martini-glass-empty{--fa:"\f000"}.fa-music{--fa:"\f001"}.fa-magnifying-glass,.fa-search{--fa:"\f002"}.fa-heart{--fa:"\f004"}.fa-star{--fa:"\f005"}.fa-user,.fa-user-alt,.fa-user-large{--fa:"\f007"}.fa-film,.fa-film-alt,.fa-film-simple{--fa:"\f008"}.fa-table-cells-large,.fa-th-large{--fa:"\f009"}.fa-table-cells,.fa-th{--fa:"\f00a"}.fa-table-list,.fa-th-list{--fa:"\f00b"}.fa-check{--fa:"\f00c"}.fa-close,.fa-multiply,.fa-remove,.fa-times,.fa-xmark{--fa:"\f00d"}.fa-magnifying-glass-plus,.fa-search-plus{--fa:"\f00e"}.fa-magnifying-glass-minus,.fa-search-minus{--fa:"\f010"}.fa-power-off{--fa:"\f011"}.fa-signal,.fa-signal-5,.fa-signal-perfect{--fa:"\f012"}.fa-cog,.fa-gear{--fa:"\f013"}.fa-home,.fa-home-alt,.fa-home-lg-alt,.fa-house{--fa:"\f015"}.fa-clock,.fa-clock-four{--fa:"\f017"}.fa-road{--fa:"\f018"}.fa-download{--fa:"\f019"}.fa-inbox{--fa:"\f01c"}.fa-arrow-right-rotate,.fa-arrow-rotate-forward,.fa-arrow-rotate-right,.fa-redo{--fa:"\f01e"}.fa-arrows-rotate,.fa-refresh,.fa-sync{--fa:"\f021"}.fa-list-alt,.fa-rectangle-list{--fa:"\f022"}.fa-lock{--fa:"\f023"}.fa-flag{--fa:"\f024"}.fa-headphones,.fa-headphones-alt,.fa-headphones-simple{--fa:"\f025"}.fa-volume-off{--fa:"\f026"}.fa-volume-down,.fa-volume-low{--fa:"\f027"}.fa-volume-high,.fa-volume-up{--fa:"\f028"}.fa-qrcode{--fa:"\f029"}.fa-barcode{--fa:"\f02a"}.fa-tag{--fa:"\f02b"}.fa-tags{--fa:"\f02c"}.fa-book{--fa:"\f02d"}.fa-bookmark{--fa:"\f02e"}.fa-print{--fa:"\f02f"}.fa-camera,.fa-camera-alt{--fa:"\f030"}.fa-font{--fa:"\f031"}.fa-bold{--fa:"\f032"}.fa-italic{--fa:"\f033"}.fa-text-height{--fa:"\f034"}.fa-text-width{--fa:"\f035"}.fa-align-left{--fa:"\f036"}.fa-align-center{--fa:"\f037"}.fa-align-right{--fa:"\f038"}.fa-align-justify{--fa:"\f039"}.fa-list,.fa-list-squares{--fa:"\f03a"}.fa-dedent,.fa-outdent{--fa:"\f03b"}.fa-indent{--fa:"\f03c"}.fa-video,.fa-video-camera{--fa:"\f03d"}.fa-image{--fa:"\f03e"}.fa-location-pin,.fa-map-marker{--fa:"\f041"}.fa-adjust,.fa-circle-half-stroke{--fa:"\f042"}.fa-droplet,.fa-tint{--fa:"\f043"}.fa-edit,.fa-pen-to-square{--fa:"\f044"}.fa-arrows,.fa-arrows-up-down-left-right{--fa:"\f047"}.fa-backward-step,.fa-step-backward{--fa:"\f048"}.fa-backward-fast,.fa-fast-backward{--fa:"\f049"}.fa-backward{--fa:"\f04a"}.fa-play{--fa:"\f04b"}.fa-pause{--fa:"\f04c"}.fa-stop{--fa:"\f04d"}.fa-forward{--fa:"\f04e"}.fa-fast-forward,.fa-forward-fast{--fa:"\f050"}.fa-forward-step,.fa-step-forward{--fa:"\f051"}.fa-eject{--fa:"\f052"}.fa-chevron-left{--fa:"\f053"}.fa-chevron-right{--fa:"\f054"}.fa-circle-plus,.fa-plus-circle{--fa:"\f055"}.fa-circle-minus,.fa-minus-circle{--fa:"\f056"}.fa-circle-xmark,.fa-times-circle,.fa-xmark-circle{--fa:"\f057"}.fa-check-circle,.fa-circle-check{--fa:"\f058"}.fa-circle-question,.fa-question-circle{--fa:"\f059"}.fa-circle-info,.fa-info-circle{--fa:"\f05a"}.fa-crosshairs{--fa:"\f05b"}.fa-ban,.fa-cancel{--fa:"\f05e"}.fa-arrow-left{--fa:"\f060"}.fa-arrow-right{--fa:"\f061"}.fa-arrow-up{--fa:"\f062"}.fa-arrow-down{--fa:"\f063"}.fa-mail-forward,.fa-share{--fa:"\f064"}.fa-expand{--fa:"\f065"}.fa-compress{--fa:"\f066"}.fa-minus,.fa-subtract{--fa:"\f068"}.fa-circle-exclamation,.fa-exclamation-circle{--fa:"\f06a"}.fa-gift{--fa:"\f06b"}.fa-leaf{--fa:"\f06c"}.fa-fire{--fa:"\f06d"}.fa-eye{--fa:"\f06e"}.fa-eye-slash{--fa:"\f070"}.fa-exclamation-triangle,.fa-triangle-exclamation,.fa-warning{--fa:"\f071"}.fa-plane{--fa:"\f072"}.fa-calendar-alt,.fa-calendar-days{--fa:"\f073"}.fa-random,.fa-shuffle{--fa:"\f074"}.fa-comment{--fa:"\f075"}.fa-magnet{--fa:"\f076"}.fa-chevron-up{--fa:"\f077"}.fa-chevron-down{--fa:"\f078"}.fa-retweet{--fa:"\f079"}.fa-cart-shopping,.fa-shopping-cart{--fa:"\f07a"}.fa-folder,.fa-folder-blank{--fa:"\f07b"}.fa-folder-open{--fa:"\f07c"}.fa-arrows-up-down,.fa-arrows-v{--fa:"\f07d"}.fa-arrows-h,.fa-arrows-left-right{--fa:"\f07e"}.fa-bar-chart,.fa-chart-bar{--fa:"\f080"}.fa-camera-retro{--fa:"\f083"}.fa-key{--fa:"\f084"}.fa-cogs,.fa-gears{--fa:"\f085"}.fa-comments{--fa:"\f086"}.fa-star-half{--fa:"\f089"}.fa-arrow-right-from-bracket,.fa-sign-out{--fa:"\f08b"}.fa-thumb-tack,.fa-thumbtack{--fa:"\f08d"}.fa-arrow-up-right-from-square,.fa-external-link{--fa:"\f08e"}.fa-arrow-right-to-bracket,.fa-sign-in{--fa:"\f090"}.fa-trophy{--fa:"\f091"}.fa-upload{--fa:"\f093"}.fa-lemon{--fa:"\f094"}.fa-phone{--fa:"\f095"}.fa-phone-square,.fa-square-phone{--fa:"\f098"}.fa-unlock{--fa:"\f09c"}.fa-credit-card,.fa-credit-card-alt{--fa:"\f09d"}.fa-feed,.fa-rss{--fa:"\f09e"}.fa-hard-drive,.fa-hdd{--fa:"\f0a0"}.fa-bullhorn{--fa:"\f0a1"}.fa-certificate{--fa:"\f0a3"}.fa-hand-point-right{--fa:"\f0a4"}.fa-hand-point-left{--fa:"\f0a5"}.fa-hand-point-up{--fa:"\f0a6"}.fa-hand-point-down{--fa:"\f0a7"}.fa-arrow-circle-left,.fa-circle-arrow-left{--fa:"\f0a8"}.fa-arrow-circle-right,.fa-circle-arrow-right{--fa:"\f0a9"}.fa-arrow-circle-up,.fa-circle-arrow-up{--fa:"\f0aa"}.fa-arrow-circle-down,.fa-circle-arrow-down{--fa:"\f0ab"}.fa-globe{--fa:"\f0ac"}.fa-wrench{--fa:"\f0ad"}.fa-list-check,.fa-tasks{--fa:"\f0ae"}.fa-filter{--fa:"\f0b0"}.fa-briefcase{--fa:"\f0b1"}.fa-arrows-alt,.fa-up-down-left-right{--fa:"\f0b2"}.fa-users{--fa:"\f0c0"}.fa-chain,.fa-link{--fa:"\f0c1"}.fa-cloud{--fa:"\f0c2"}.fa-flask{--fa:"\f0c3"}.fa-cut,.fa-scissors{--fa:"\f0c4"}.fa-copy{--fa:"\f0c5"}.fa-paperclip{--fa:"\f0c6"}.fa-floppy-disk,.fa-save{--fa:"\f0c7"}.fa-square{--fa:"\f0c8"}.fa-bars,.fa-navicon{--fa:"\f0c9"}.fa-list-dots,.fa-list-ul{--fa:"\f0ca"}.fa-list-1-2,.fa-list-numeric,.fa-list-ol{--fa:"\f0cb"}.fa-strikethrough{--fa:"\f0cc"}.fa-underline{--fa:"\f0cd"}.fa-table{--fa:"\f0ce"}.fa-magic,.fa-wand-magic{--fa:"\f0d0"}.fa-truck{--fa:"\f0d1"}.fa-money-bill{--fa:"\f0d6"}.fa-caret-down{--fa:"\f0d7"}.fa-caret-up{--fa:"\f0d8"}.fa-caret-left{--fa:"\f0d9"}.fa-caret-right{--fa:"\f0da"}.fa-columns,.fa-table-columns{--fa:"\f0db"}.fa-sort,.fa-unsorted{--fa:"\f0dc"}.fa-sort-desc,.fa-sort-down{--fa:"\f0dd"}.fa-sort-asc,.fa-sort-up{--fa:"\f0de"}.fa-envelope{--fa:"\f0e0"}.fa-arrow-left-rotate,.fa-arrow-rotate-back,.fa-arrow-rotate-backward,.fa-arrow-rotate-left,.fa-undo{--fa:"\f0e2"}.fa-gavel,.fa-legal{--fa:"\f0e3"}.fa-bolt,.fa-zap{--fa:"\f0e7"}.fa-sitemap{--fa:"\f0e8"}.fa-umbrella{--fa:"\f0e9"}.fa-file-clipboard,.fa-paste{--fa:"\f0ea"}.fa-lightbulb{--fa:"\f0eb"}.fa-arrow-right-arrow-left,.fa-exchange{--fa:"\f0ec"}.fa-cloud-arrow-down,.fa-cloud-download,.fa-cloud-download-alt{--fa:"\f0ed"}.fa-cloud-arrow-up,.fa-cloud-upload,.fa-cloud-upload-alt{--fa:"\f0ee"}.fa-user-doctor,.fa-user-md{--fa:"\f0f0"}.fa-stethoscope{--fa:"\f0f1"}.fa-suitcase{--fa:"\f0f2"}.fa-bell{--fa:"\f0f3"}.fa-coffee,.fa-mug-saucer{--fa:"\f0f4"}.fa-hospital,.fa-hospital-alt,.fa-hospital-wide{--fa:"\f0f8"}.fa-ambulance,.fa-truck-medical{--fa:"\f0f9"}.fa-medkit,.fa-suitcase-medical{--fa:"\f0fa"}.fa-fighter-jet,.fa-jet-fighter{--fa:"\f0fb"}.fa-beer,.fa-beer-mug-empty{--fa:"\f0fc"}.fa-h-square,.fa-square-h{--fa:"\f0fd"}.fa-plus-square,.fa-square-plus{--fa:"\f0fe"}.fa-angle-double-left,.fa-angles-left{--fa:"\f100"}.fa-angle-double-right,.fa-angles-right{--fa:"\f101"}.fa-angle-double-up,.fa-angles-up{--fa:"\f102"}.fa-angle-double-down,.fa-angles-down{--fa:"\f103"}.fa-angle-left{--fa:"\f104"}.fa-angle-right{--fa:"\f105"}.fa-angle-up{--fa:"\f106"}.fa-angle-down{--fa:"\f107"}.fa-laptop{--fa:"\f109"}.fa-tablet-button{--fa:"\f10a"}.fa-mobile-button{--fa:"\f10b"}.fa-quote-left,.fa-quote-left-alt{--fa:"\f10d"}.fa-quote-right,.fa-quote-right-alt{--fa:"\f10e"}.fa-spinner{--fa:"\f110"}.fa-circle{--fa:"\f111"}.fa-face-smile,.fa-smile{--fa:"\f118"}.fa-face-frown,.fa-frown{--fa:"\f119"}.fa-face-meh,.fa-meh{--fa:"\f11a"}.fa-gamepad{--fa:"\f11b"}.fa-keyboard{--fa:"\f11c"}.fa-flag-checkered{--fa:"\f11e"}.fa-terminal{--fa:"\f120"}.fa-code{--fa:"\f121"}.fa-mail-reply-all,.fa-reply-all{--fa:"\f122"}.fa-location-arrow{--fa:"\f124"}.fa-crop{--fa:"\f125"}.fa-code-branch{--fa:"\f126"}.fa-chain-broken,.fa-chain-slash,.fa-link-slash,.fa-unlink{--fa:"\f127"}.fa-info{--fa:"\f129"}.fa-superscript{--fa:"\f12b"}.fa-subscript{--fa:"\f12c"}.fa-eraser{--fa:"\f12d"}.fa-puzzle-piece{--fa:"\f12e"}.fa-microphone{--fa:"\f130"}.fa-microphone-slash{--fa:"\f131"}.fa-shield,.fa-shield-blank{--fa:"\f132"}.fa-calendar{--fa:"\f133"}.fa-fire-extinguisher{--fa:"\f134"}.fa-rocket{--fa:"\f135"}.fa-chevron-circle-left,.fa-circle-chevron-left{--fa:"\f137"}.fa-chevron-circle-right,.fa-circle-chevron-right{--fa:"\f138"}.fa-chevron-circle-up,.fa-circle-chevron-up{--fa:"\f139"}.fa-chevron-circle-down,.fa-circle-chevron-down{--fa:"\f13a"}.fa-anchor{--fa:"\f13d"}.fa-unlock-alt,.fa-unlock-keyhole{--fa:"\f13e"}.fa-bullseye{--fa:"\f140"}.fa-ellipsis,.fa-ellipsis-h{--fa:"\f141"}.fa-ellipsis-v,.fa-ellipsis-vertical{--fa:"\f142"}.fa-rss-square,.fa-square-rss{--fa:"\f143"}.fa-circle-play,.fa-play-circle{--fa:"\f144"}.fa-ticket{--fa:"\f145"}.fa-minus-square,.fa-square-minus{--fa:"\f146"}.fa-arrow-turn-up,.fa-level-up{--fa:"\f148"}.fa-arrow-turn-down,.fa-level-down{--fa:"\f149"}.fa-check-square,.fa-square-check{--fa:"\f14a"}.fa-pen-square,.fa-pencil-square,.fa-square-pen{--fa:"\f14b"}.fa-external-link-square,.fa-square-arrow-up-right{--fa:"\f14c"}.fa-share-from-square,.fa-share-square{--fa:"\f14d"}.fa-compass{--fa:"\f14e"}.fa-caret-square-down,.fa-square-caret-down{--fa:"\f150"}.fa-caret-square-up,.fa-square-caret-up{--fa:"\f151"}.fa-caret-square-right,.fa-square-caret-right{--fa:"\f152"}.fa-eur,.fa-euro,.fa-euro-sign{--fa:"\f153"}.fa-gbp,.fa-pound-sign,.fa-sterling-sign{--fa:"\f154"}.fa-rupee,.fa-rupee-sign{--fa:"\f156"}.fa-cny,.fa-jpy,.fa-rmb,.fa-yen,.fa-yen-sign{--fa:"\f157"}.fa-rouble,.fa-rub,.fa-ruble,.fa-ruble-sign{--fa:"\f158"}.fa-krw,.fa-won,.fa-won-sign{--fa:"\f159"}.fa-file{--fa:"\f15b"}.fa-file-alt,.fa-file-lines,.fa-file-text{--fa:"\f15c"}.fa-arrow-down-a-z,.fa-sort-alpha-asc,.fa-sort-alpha-down{--fa:"\f15d"}.fa-arrow-up-a-z,.fa-sort-alpha-up{--fa:"\f15e"}.fa-arrow-down-wide-short,.fa-sort-amount-asc,.fa-sort-amount-down{--fa:"\f160"}.fa-arrow-up-wide-short,.fa-sort-amount-up{--fa:"\f161"}.fa-arrow-down-1-9,.fa-sort-numeric-asc,.fa-sort-numeric-down{--fa:"\f162"}.fa-arrow-up-1-9,.fa-sort-numeric-up{--fa:"\f163"}.fa-thumbs-up{--fa:"\f164"}.fa-thumbs-down{--fa:"\f165"}.fa-arrow-down-long,.fa-long-arrow-down{--fa:"\f175"}.fa-arrow-up-long,.fa-long-arrow-up{--fa:"\f176"}.fa-arrow-left-long,.fa-long-arrow-left{--fa:"\f177"}.fa-arrow-right-long,.fa-long-arrow-right{--fa:"\f178"}.fa-female,.fa-person-dress{--fa:"\f182"}.fa-male,.fa-person{--fa:"\f183"}.fa-sun{--fa:"\f185"}.fa-moon{--fa:"\f186"}.fa-archive,.fa-box-archive{--fa:"\f187"}.fa-bug{--fa:"\f188"}.fa-caret-square-left,.fa-square-caret-left{--fa:"\f191"}.fa-circle-dot,.fa-dot-circle{--fa:"\f192"}.fa-wheelchair{--fa:"\f193"}.fa-lira-sign{--fa:"\f195"}.fa-shuttle-space,.fa-space-shuttle{--fa:"\f197"}.fa-envelope-square,.fa-square-envelope{--fa:"\f199"}.fa-bank,.fa-building-columns,.fa-institution,.fa-museum,.fa-university{--fa:"\f19c"}.fa-graduation-cap,.fa-mortar-board{--fa:"\f19d"}.fa-language{--fa:"\f1ab"}.fa-fax{--fa:"\f1ac"}.fa-building{--fa:"\f1ad"}.fa-child{--fa:"\f1ae"}.fa-paw{--fa:"\f1b0"}.fa-cube{--fa:"\f1b2"}.fa-cubes{--fa:"\f1b3"}.fa-recycle{--fa:"\f1b8"}.fa-automobile,.fa-car{--fa:"\f1b9"}.fa-cab,.fa-taxi{--fa:"\f1ba"}.fa-tree{--fa:"\f1bb"}.fa-database{--fa:"\f1c0"}.fa-file-pdf{--fa:"\f1c1"}.fa-file-word{--fa:"\f1c2"}.fa-file-excel{--fa:"\f1c3"}.fa-file-powerpoint{--fa:"\f1c4"}.fa-file-image{--fa:"\f1c5"}.fa-file-archive,.fa-file-zipper{--fa:"\f1c6"}.fa-file-audio{--fa:"\f1c7"}.fa-file-video{--fa:"\f1c8"}.fa-file-code{--fa:"\f1c9"}.fa-life-ring{--fa:"\f1cd"}.fa-circle-notch{--fa:"\f1ce"}.fa-paper-plane{--fa:"\f1d8"}.fa-clock-rotate-left,.fa-history{--fa:"\f1da"}.fa-header,.fa-heading{--fa:"\f1dc"}.fa-paragraph{--fa:"\f1dd"}.fa-sliders,.fa-sliders-h{--fa:"\f1de"}.fa-share-alt,.fa-share-nodes{--fa:"\f1e0"}.fa-share-alt-square,.fa-square-share-nodes{--fa:"\f1e1"}.fa-bomb{--fa:"\f1e2"}.fa-futbol,.fa-futbol-ball,.fa-soccer-ball{--fa:"\f1e3"}.fa-teletype,.fa-tty{--fa:"\f1e4"}.fa-binoculars{--fa:"\f1e5"}.fa-plug{--fa:"\f1e6"}.fa-newspaper{--fa:"\f1ea"}.fa-wifi,.fa-wifi-3,.fa-wifi-strong{--fa:"\f1eb"}.fa-calculator{--fa:"\f1ec"}.fa-bell-slash{--fa:"\f1f6"}.fa-trash{--fa:"\f1f8"}.fa-copyright{--fa:"\f1f9"}.fa-eye-dropper,.fa-eye-dropper-empty,.fa-eyedropper{--fa:"\f1fb"}.fa-paint-brush,.fa-paintbrush{--fa:"\f1fc"}.fa-birthday-cake,.fa-cake,.fa-cake-candles{--fa:"\f1fd"}.fa-area-chart,.fa-chart-area{--fa:"\f1fe"}.fa-chart-pie,.fa-pie-chart{--fa:"\f200"}.fa-chart-line,.fa-line-chart{--fa:"\f201"}.fa-toggle-off{--fa:"\f204"}.fa-toggle-on{--fa:"\f205"}.fa-bicycle{--fa:"\f206"}.fa-bus{--fa:"\f207"}.fa-closed-captioning{--fa:"\f20a"}.fa-ils,.fa-shekel,.fa-shekel-sign,.fa-sheqel,.fa-sheqel-sign{--fa:"\f20b"}.fa-cart-plus{--fa:"\f217"}.fa-cart-arrow-down{--fa:"\f218"}.fa-diamond{--fa:"\f219"}.fa-ship{--fa:"\f21a"}.fa-user-secret{--fa:"\f21b"}.fa-motorcycle{--fa:"\f21c"}.fa-street-view{--fa:"\f21d"}.fa-heart-pulse,.fa-heartbeat{--fa:"\f21e"}.fa-venus{--fa:"\f221"}.fa-mars{--fa:"\f222"}.fa-mercury{--fa:"\f223"}.fa-mars-and-venus{--fa:"\f224"}.fa-transgender,.fa-transgender-alt{--fa:"\f225"}.fa-venus-double{--fa:"\f226"}.fa-mars-double{--fa:"\f227"}.fa-venus-mars{--fa:"\f228"}.fa-mars-stroke{--fa:"\f229"}.fa-mars-stroke-up,.fa-mars-stroke-v{--fa:"\f22a"}.fa-mars-stroke-h,.fa-mars-stroke-right{--fa:"\f22b"}.fa-neuter{--fa:"\f22c"}.fa-genderless{--fa:"\f22d"}.fa-server{--fa:"\f233"}.fa-user-plus{--fa:"\f234"}.fa-user-times,.fa-user-xmark{--fa:"\f235"}.fa-bed{--fa:"\f236"}.fa-train{--fa:"\f238"}.fa-subway,.fa-train-subway{--fa:"\f239"}.fa-battery,.fa-battery-5,.fa-battery-full{--fa:"\f240"}.fa-battery-4,.fa-battery-three-quarters{--fa:"\f241"}.fa-battery-3,.fa-battery-half{--fa:"\f242"}.fa-battery-2,.fa-battery-quarter{--fa:"\f243"}.fa-battery-0,.fa-battery-empty{--fa:"\f244"}.fa-arrow-pointer,.fa-mouse-pointer{--fa:"\f245"}.fa-i-cursor{--fa:"\f246"}.fa-object-group{--fa:"\f247"}.fa-object-ungroup{--fa:"\f248"}.fa-note-sticky,.fa-sticky-note{--fa:"\f249"}.fa-clone{--fa:"\f24d"}.fa-balance-scale,.fa-scale-balanced{--fa:"\f24e"}.fa-hourglass-1,.fa-hourglass-start{--fa:"\f251"}.fa-hourglass-2,.fa-hourglass-half{--fa:"\f252"}.fa-hourglass-3,.fa-hourglass-end{--fa:"\f253"}.fa-hourglass,.fa-hourglass-empty{--fa:"\f254"}.fa-hand-back-fist,.fa-hand-rock{--fa:"\f255"}.fa-hand,.fa-hand-paper{--fa:"\f256"}.fa-hand-scissors{--fa:"\f257"}.fa-hand-lizard{--fa:"\f258"}.fa-hand-spock{--fa:"\f259"}.fa-hand-pointer{--fa:"\f25a"}.fa-hand-peace{--fa:"\f25b"}.fa-trademark{--fa:"\f25c"}.fa-registered{--fa:"\f25d"}.fa-television,.fa-tv,.fa-tv-alt{--fa:"\f26c"}.fa-calendar-plus{--fa:"\f271"}.fa-calendar-minus{--fa:"\f272"}.fa-calendar-times,.fa-calendar-xmark{--fa:"\f273"}.fa-calendar-check{--fa:"\f274"}.fa-industry{--fa:"\f275"}.fa-map-pin{--fa:"\f276"}.fa-map-signs,.fa-signs-post{--fa:"\f277"}.fa-map{--fa:"\f279"}.fa-comment-alt,.fa-message{--fa:"\f27a"}.fa-circle-pause,.fa-pause-circle{--fa:"\f28b"}.fa-circle-stop,.fa-stop-circle{--fa:"\f28d"}.fa-bag-shopping,.fa-shopping-bag{--fa:"\f290"}.fa-basket-shopping,.fa-shopping-basket{--fa:"\f291"}.fa-universal-access{--fa:"\f29a"}.fa-blind,.fa-person-walking-with-cane{--fa:"\f29d"}.fa-audio-description{--fa:"\f29e"}.fa-phone-volume,.fa-volume-control-phone{--fa:"\f2a0"}.fa-braille{--fa:"\f2a1"}.fa-assistive-listening-systems,.fa-ear-listen{--fa:"\f2a2"}.fa-american-sign-language-interpreting,.fa-asl-interpreting,.fa-hands-american-sign-language-interpreting,.fa-hands-asl-interpreting{--fa:"\f2a3"}.fa-deaf,.fa-deafness,.fa-ear-deaf,.fa-hard-of-hearing{--fa:"\f2a4"}.fa-hands,.fa-sign-language,.fa-signing{--fa:"\f2a7"}.fa-eye-low-vision,.fa-low-vision{--fa:"\f2a8"}.fa-handshake,.fa-handshake-alt,.fa-handshake-simple{--fa:"\f2b5"}.fa-envelope-open{--fa:"\f2b6"}.fa-address-book,.fa-contact-book{--fa:"\f2b9"}.fa-address-card,.fa-contact-card,.fa-vcard{--fa:"\f2bb"}.fa-circle-user,.fa-user-circle{--fa:"\f2bd"}.fa-id-badge{--fa:"\f2c1"}.fa-drivers-license,.fa-id-card{--fa:"\f2c2"}.fa-temperature-4,.fa-temperature-full,.fa-thermometer-4,.fa-thermometer-full{--fa:"\f2c7"}.fa-temperature-3,.fa-temperature-three-quarters,.fa-thermometer-3,.fa-thermometer-three-quarters{--fa:"\f2c8"}.fa-temperature-2,.fa-temperature-half,.fa-thermometer-2,.fa-thermometer-half{--fa:"\f2c9"}.fa-temperature-1,.fa-temperature-quarter,.fa-thermometer-1,.fa-thermometer-quarter{--fa:"\f2ca"}.fa-temperature-0,.fa-temperature-empty,.fa-thermometer-0,.fa-thermometer-empty{--fa:"\f2cb"}.fa-shower{--fa:"\f2cc"}.fa-bath,.fa-bathtub{--fa:"\f2cd"}.fa-podcast{--fa:"\f2ce"}.fa-window-maximize{--fa:"\f2d0"}.fa-window-minimize{--fa:"\f2d1"}.fa-window-restore{--fa:"\f2d2"}.fa-square-xmark,.fa-times-square,.fa-xmark-square{--fa:"\f2d3"}.fa-microchip{--fa:"\f2db"}.fa-snowflake{--fa:"\f2dc"}.fa-spoon,.fa-utensil-spoon{--fa:"\f2e5"}.fa-cutlery,.fa-utensils{--fa:"\f2e7"}.fa-rotate-back,.fa-rotate-backward,.fa-rotate-left,.fa-undo-alt{--fa:"\f2ea"}.fa-trash-alt,.fa-trash-can{--fa:"\f2ed"}.fa-rotate,.fa-sync-alt{--fa:"\f2f1"}.fa-stopwatch{--fa:"\f2f2"}.fa-right-from-bracket,.fa-sign-out-alt{--fa:"\f2f5"}.fa-right-to-bracket,.fa-sign-in-alt{--fa:"\f2f6"}.fa-redo-alt,.fa-rotate-forward,.fa-rotate-right{--fa:"\f2f9"}.fa-poo{--fa:"\f2fe"}.fa-images{--fa:"\f302"}.fa-pencil,.fa-pencil-alt{--fa:"\f303"}.fa-pen{--fa:"\f304"}.fa-pen-alt,.fa-pen-clip{--fa:"\f305"}.fa-octagon{--fa:"\f306"}.fa-down-long,.fa-long-arrow-alt-down{--fa:"\f309"}.fa-left-long,.fa-long-arrow-alt-left{--fa:"\f30a"}.fa-long-arrow-alt-right,.fa-right-long{--fa:"\f30b"}.fa-long-arrow-alt-up,.fa-up-long{--fa:"\f30c"}.fa-hexagon{--fa:"\f312"}.fa-file-edit,.fa-file-pen{--fa:"\f31c"}.fa-expand-arrows-alt,.fa-maximize{--fa:"\f31e"}.fa-clipboard{--fa:"\f328"}.fa-arrows-alt-h,.fa-left-right{--fa:"\f337"}.fa-arrows-alt-v,.fa-up-down{--fa:"\f338"}.fa-alarm-clock{--fa:"\f34e"}.fa-arrow-alt-circle-down,.fa-circle-down{--fa:"\f358"}.fa-arrow-alt-circle-left,.fa-circle-left{--fa:"\f359"}.fa-arrow-alt-circle-right,.fa-circle-right{--fa:"\f35a"}.fa-arrow-alt-circle-up,.fa-circle-up{--fa:"\f35b"}.fa-external-link-alt,.fa-up-right-from-square{--fa:"\f35d"}.fa-external-link-square-alt,.fa-square-up-right{--fa:"\f360"}.fa-exchange-alt,.fa-right-left{--fa:"\f362"}.fa-repeat{--fa:"\f363"}.fa-code-commit{--fa:"\f386"}.fa-code-merge{--fa:"\f387"}.fa-desktop,.fa-desktop-alt{--fa:"\f390"}.fa-gem{--fa:"\f3a5"}.fa-level-down-alt,.fa-turn-down{--fa:"\f3be"}.fa-level-up-alt,.fa-turn-up{--fa:"\f3bf"}.fa-lock-open{--fa:"\f3c1"}.fa-location-dot,.fa-map-marker-alt{--fa:"\f3c5"}.fa-microphone-alt,.fa-microphone-lines{--fa:"\f3c9"}.fa-mobile-alt,.fa-mobile-screen-button{--fa:"\f3cd"}.fa-mobile,.fa-mobile-android,.fa-mobile-phone{--fa:"\f3ce"}.fa-mobile-android-alt,.fa-mobile-screen{--fa:"\f3cf"}.fa-money-bill-1,.fa-money-bill-alt{--fa:"\f3d1"}.fa-phone-slash{--fa:"\f3dd"}.fa-image-portrait,.fa-portrait{--fa:"\f3e0"}.fa-mail-reply,.fa-reply{--fa:"\f3e5"}.fa-shield-alt,.fa-shield-halved{--fa:"\f3ed"}.fa-tablet-alt,.fa-tablet-screen-button{--fa:"\f3fa"}.fa-tablet,.fa-tablet-android{--fa:"\f3fb"}.fa-ticket-alt,.fa-ticket-simple{--fa:"\f3ff"}.fa-rectangle-times,.fa-rectangle-xmark,.fa-times-rectangle,.fa-window-close{--fa:"\f410"}.fa-compress-alt,.fa-down-left-and-up-right-to-center{--fa:"\f422"}.fa-expand-alt,.fa-up-right-and-down-left-from-center{--fa:"\f424"}.fa-baseball-bat-ball{--fa:"\f432"}.fa-baseball,.fa-baseball-ball{--fa:"\f433"}.fa-basketball,.fa-basketball-ball{--fa:"\f434"}.fa-bowling-ball{--fa:"\f436"}.fa-chess{--fa:"\f439"}.fa-chess-bishop{--fa:"\f43a"}.fa-chess-board{--fa:"\f43c"}.fa-chess-king{--fa:"\f43f"}.fa-chess-knight{--fa:"\f441"}.fa-chess-pawn{--fa:"\f443"}.fa-chess-queen{--fa:"\f445"}.fa-chess-rook{--fa:"\f447"}.fa-dumbbell{--fa:"\f44b"}.fa-football,.fa-football-ball{--fa:"\f44e"}.fa-golf-ball,.fa-golf-ball-tee{--fa:"\f450"}.fa-hockey-puck{--fa:"\f453"}.fa-broom-ball,.fa-quidditch,.fa-quidditch-broom-ball{--fa:"\f458"}.fa-square-full{--fa:"\f45c"}.fa-ping-pong-paddle-ball,.fa-table-tennis,.fa-table-tennis-paddle-ball{--fa:"\f45d"}.fa-volleyball,.fa-volleyball-ball{--fa:"\f45f"}.fa-allergies,.fa-hand-dots{--fa:"\f461"}.fa-band-aid,.fa-bandage{--fa:"\f462"}.fa-box{--fa:"\f466"}.fa-boxes,.fa-boxes-alt,.fa-boxes-stacked{--fa:"\f468"}.fa-briefcase-medical{--fa:"\f469"}.fa-burn,.fa-fire-flame-simple{--fa:"\f46a"}.fa-capsules{--fa:"\f46b"}.fa-clipboard-check{--fa:"\f46c"}.fa-clipboard-list{--fa:"\f46d"}.fa-diagnoses,.fa-person-dots-from-line{--fa:"\f470"}.fa-dna{--fa:"\f471"}.fa-dolly,.fa-dolly-box{--fa:"\f472"}.fa-cart-flatbed,.fa-dolly-flatbed{--fa:"\f474"}.fa-file-medical{--fa:"\f477"}.fa-file-medical-alt,.fa-file-waveform{--fa:"\f478"}.fa-first-aid,.fa-kit-medical{--fa:"\f479"}.fa-circle-h,.fa-hospital-symbol{--fa:"\f47e"}.fa-id-card-alt,.fa-id-card-clip{--fa:"\f47f"}.fa-notes-medical{--fa:"\f481"}.fa-pallet{--fa:"\f482"}.fa-pills{--fa:"\f484"}.fa-prescription-bottle{--fa:"\f485"}.fa-prescription-bottle-alt,.fa-prescription-bottle-medical{--fa:"\f486"}.fa-bed-pulse,.fa-procedures{--fa:"\f487"}.fa-shipping-fast,.fa-truck-fast{--fa:"\f48b"}.fa-smoking{--fa:"\f48d"}.fa-syringe{--fa:"\f48e"}.fa-tablets{--fa:"\f490"}.fa-thermometer{--fa:"\f491"}.fa-vial{--fa:"\f492"}.fa-vials{--fa:"\f493"}.fa-warehouse{--fa:"\f494"}.fa-weight,.fa-weight-scale{--fa:"\f496"}.fa-x-ray{--fa:"\f497"}.fa-box-open{--fa:"\f49e"}.fa-comment-dots,.fa-commenting{--fa:"\f4ad"}.fa-comment-slash{--fa:"\f4b3"}.fa-couch{--fa:"\f4b8"}.fa-circle-dollar-to-slot,.fa-donate{--fa:"\f4b9"}.fa-dove{--fa:"\f4ba"}.fa-hand-holding{--fa:"\f4bd"}.fa-hand-holding-heart{--fa:"\f4be"}.fa-hand-holding-dollar,.fa-hand-holding-usd{--fa:"\f4c0"}.fa-hand-holding-droplet,.fa-hand-holding-water{--fa:"\f4c1"}.fa-hands-holding{--fa:"\f4c2"}.fa-hands-helping,.fa-handshake-angle{--fa:"\f4c4"}.fa-parachute-box{--fa:"\f4cd"}.fa-people-carry,.fa-people-carry-box{--fa:"\f4ce"}.fa-piggy-bank{--fa:"\f4d3"}.fa-ribbon{--fa:"\f4d6"}.fa-route{--fa:"\f4d7"}.fa-seedling,.fa-sprout{--fa:"\f4d8"}.fa-sign,.fa-sign-hanging{--fa:"\f4d9"}.fa-face-smile-wink,.fa-smile-wink{--fa:"\f4da"}.fa-tape{--fa:"\f4db"}.fa-truck-loading,.fa-truck-ramp-box{--fa:"\f4de"}.fa-truck-moving{--fa:"\f4df"}.fa-video-slash{--fa:"\f4e2"}.fa-wine-glass{--fa:"\f4e3"}.fa-user-astronaut{--fa:"\f4fb"}.fa-user-check{--fa:"\f4fc"}.fa-user-clock{--fa:"\f4fd"}.fa-user-cog,.fa-user-gear{--fa:"\f4fe"}.fa-user-edit,.fa-user-pen{--fa:"\f4ff"}.fa-user-friends,.fa-user-group{--fa:"\f500"}.fa-user-graduate{--fa:"\f501"}.fa-user-lock{--fa:"\f502"}.fa-user-minus{--fa:"\f503"}.fa-user-ninja{--fa:"\f504"}.fa-user-shield{--fa:"\f505"}.fa-user-alt-slash,.fa-user-large-slash,.fa-user-slash{--fa:"\f506"}.fa-user-tag{--fa:"\f507"}.fa-user-tie{--fa:"\f508"}.fa-users-cog,.fa-users-gear{--fa:"\f509"}.fa-balance-scale-left,.fa-scale-unbalanced{--fa:"\f515"}.fa-balance-scale-right,.fa-scale-unbalanced-flip{--fa:"\f516"}.fa-blender{--fa:"\f517"}.fa-book-open{--fa:"\f518"}.fa-broadcast-tower,.fa-tower-broadcast{--fa:"\f519"}.fa-broom{--fa:"\f51a"}.fa-blackboard,.fa-chalkboard{--fa:"\f51b"}.fa-chalkboard-teacher,.fa-chalkboard-user{--fa:"\f51c"}.fa-church{--fa:"\f51d"}.fa-coins{--fa:"\f51e"}.fa-compact-disc{--fa:"\f51f"}.fa-crow{--fa:"\f520"}.fa-crown{--fa:"\f521"}.fa-dice{--fa:"\f522"}.fa-dice-five{--fa:"\f523"}.fa-dice-four{--fa:"\f524"}.fa-dice-one{--fa:"\f525"}.fa-dice-six{--fa:"\f526"}.fa-dice-three{--fa:"\f527"}.fa-dice-two{--fa:"\f528"}.fa-divide{--fa:"\f529"}.fa-door-closed{--fa:"\f52a"}.fa-door-open{--fa:"\f52b"}.fa-feather{--fa:"\f52d"}.fa-frog{--fa:"\f52e"}.fa-gas-pump{--fa:"\f52f"}.fa-glasses{--fa:"\f530"}.fa-greater-than-equal{--fa:"\f532"}.fa-helicopter{--fa:"\f533"}.fa-infinity{--fa:"\f534"}.fa-kiwi-bird{--fa:"\f535"}.fa-less-than-equal{--fa:"\f537"}.fa-memory{--fa:"\f538"}.fa-microphone-alt-slash,.fa-microphone-lines-slash{--fa:"\f539"}.fa-money-bill-wave{--fa:"\f53a"}.fa-money-bill-1-wave,.fa-money-bill-wave-alt{--fa:"\f53b"}.fa-money-check{--fa:"\f53c"}.fa-money-check-alt,.fa-money-check-dollar{--fa:"\f53d"}.fa-not-equal{--fa:"\f53e"}.fa-palette{--fa:"\f53f"}.fa-parking,.fa-square-parking{--fa:"\f540"}.fa-diagram-project,.fa-project-diagram{--fa:"\f542"}.fa-receipt{--fa:"\f543"}.fa-robot{--fa:"\f544"}.fa-ruler{--fa:"\f545"}.fa-ruler-combined{--fa:"\f546"}.fa-ruler-horizontal{--fa:"\f547"}.fa-ruler-vertical{--fa:"\f548"}.fa-school{--fa:"\f549"}.fa-screwdriver{--fa:"\f54a"}.fa-shoe-prints{--fa:"\f54b"}.fa-skull{--fa:"\f54c"}.fa-ban-smoking,.fa-smoking-ban{--fa:"\f54d"}.fa-store{--fa:"\f54e"}.fa-shop,.fa-store-alt{--fa:"\f54f"}.fa-bars-staggered,.fa-reorder,.fa-stream{--fa:"\f550"}.fa-stroopwafel{--fa:"\f551"}.fa-toolbox{--fa:"\f552"}.fa-shirt,.fa-t-shirt,.fa-tshirt{--fa:"\f553"}.fa-person-walking,.fa-walking{--fa:"\f554"}.fa-wallet{--fa:"\f555"}.fa-angry,.fa-face-angry{--fa:"\f556"}.fa-archway{--fa:"\f557"}.fa-atlas,.fa-book-atlas{--fa:"\f558"}.fa-award{--fa:"\f559"}.fa-backspace,.fa-delete-left{--fa:"\f55a"}.fa-bezier-curve{--fa:"\f55b"}.fa-bong{--fa:"\f55c"}.fa-brush{--fa:"\f55d"}.fa-bus-alt,.fa-bus-simple{--fa:"\f55e"}.fa-cannabis{--fa:"\f55f"}.fa-check-double{--fa:"\f560"}.fa-cocktail,.fa-martini-glass-citrus{--fa:"\f561"}.fa-bell-concierge,.fa-concierge-bell{--fa:"\f562"}.fa-cookie{--fa:"\f563"}.fa-cookie-bite{--fa:"\f564"}.fa-crop-alt,.fa-crop-simple{--fa:"\f565"}.fa-digital-tachograph,.fa-tachograph-digital{--fa:"\f566"}.fa-dizzy,.fa-face-dizzy{--fa:"\f567"}.fa-compass-drafting,.fa-drafting-compass{--fa:"\f568"}.fa-drum{--fa:"\f569"}.fa-drum-steelpan{--fa:"\f56a"}.fa-feather-alt,.fa-feather-pointed{--fa:"\f56b"}.fa-file-contract{--fa:"\f56c"}.fa-file-arrow-down,.fa-file-download{--fa:"\f56d"}.fa-arrow-right-from-file,.fa-file-export{--fa:"\f56e"}.fa-arrow-right-to-file,.fa-file-import{--fa:"\f56f"}.fa-file-invoice{--fa:"\f570"}.fa-file-invoice-dollar{--fa:"\f571"}.fa-file-prescription{--fa:"\f572"}.fa-file-signature{--fa:"\f573"}.fa-file-arrow-up,.fa-file-upload{--fa:"\f574"}.fa-fill{--fa:"\f575"}.fa-fill-drip{--fa:"\f576"}.fa-fingerprint{--fa:"\f577"}.fa-fish{--fa:"\f578"}.fa-face-flushed,.fa-flushed{--fa:"\f579"}.fa-face-frown-open,.fa-frown-open{--fa:"\f57a"}.fa-glass-martini-alt,.fa-martini-glass{--fa:"\f57b"}.fa-earth-africa,.fa-globe-africa{--fa:"\f57c"}.fa-earth,.fa-earth-america,.fa-earth-americas,.fa-globe-americas{--fa:"\f57d"}.fa-earth-asia,.fa-globe-asia{--fa:"\f57e"}.fa-face-grimace,.fa-grimace{--fa:"\f57f"}.fa-face-grin,.fa-grin{--fa:"\f580"}.fa-face-grin-wide,.fa-grin-alt{--fa:"\f581"}.fa-face-grin-beam,.fa-grin-beam{--fa:"\f582"}.fa-face-grin-beam-sweat,.fa-grin-beam-sweat{--fa:"\f583"}.fa-face-grin-hearts,.fa-grin-hearts{--fa:"\f584"}.fa-face-grin-squint,.fa-grin-squint{--fa:"\f585"}.fa-face-grin-squint-tears,.fa-grin-squint-tears{--fa:"\f586"}.fa-face-grin-stars,.fa-grin-stars{--fa:"\f587"}.fa-face-grin-tears,.fa-grin-tears{--fa:"\f588"}.fa-face-grin-tongue,.fa-grin-tongue{--fa:"\f589"}.fa-face-grin-tongue-squint,.fa-grin-tongue-squint{--fa:"\f58a"}.fa-face-grin-tongue-wink,.fa-grin-tongue-wink{--fa:"\f58b"}.fa-face-grin-wink,.fa-grin-wink{--fa:"\f58c"}.fa-grid-horizontal,.fa-grip,.fa-grip-horizontal{--fa:"\f58d"}.fa-grid-vertical,.fa-grip-vertical{--fa:"\f58e"}.fa-headset{--fa:"\f590"}.fa-highlighter{--fa:"\f591"}.fa-hot-tub,.fa-hot-tub-person{--fa:"\f593"}.fa-hotel{--fa:"\f594"}.fa-joint{--fa:"\f595"}.fa-face-kiss,.fa-kiss{--fa:"\f596"}.fa-face-kiss-beam,.fa-kiss-beam{--fa:"\f597"}.fa-face-kiss-wink-heart,.fa-kiss-wink-heart{--fa:"\f598"}.fa-face-laugh,.fa-laugh{--fa:"\f599"}.fa-face-laugh-beam,.fa-laugh-beam{--fa:"\f59a"}.fa-face-laugh-squint,.fa-laugh-squint{--fa:"\f59b"}.fa-face-laugh-wink,.fa-laugh-wink{--fa:"\f59c"}.fa-cart-flatbed-suitcase,.fa-luggage-cart{--fa:"\f59d"}.fa-map-location,.fa-map-marked{--fa:"\f59f"}.fa-map-location-dot,.fa-map-marked-alt{--fa:"\f5a0"}.fa-marker{--fa:"\f5a1"}.fa-medal{--fa:"\f5a2"}.fa-face-meh-blank,.fa-meh-blank{--fa:"\f5a4"}.fa-face-rolling-eyes,.fa-meh-rolling-eyes{--fa:"\f5a5"}.fa-monument{--fa:"\f5a6"}.fa-mortar-pestle{--fa:"\f5a7"}.fa-paint-roller{--fa:"\f5aa"}.fa-passport{--fa:"\f5ab"}.fa-pen-fancy{--fa:"\f5ac"}.fa-pen-nib{--fa:"\f5ad"}.fa-pen-ruler,.fa-pencil-ruler{--fa:"\f5ae"}.fa-plane-arrival{--fa:"\f5af"}.fa-plane-departure{--fa:"\f5b0"}.fa-prescription{--fa:"\f5b1"}.fa-face-sad-cry,.fa-sad-cry{--fa:"\f5b3"}.fa-face-sad-tear,.fa-sad-tear{--fa:"\f5b4"}.fa-shuttle-van,.fa-van-shuttle{--fa:"\f5b6"}.fa-signature{--fa:"\f5b7"}.fa-face-smile-beam,.fa-smile-beam{--fa:"\f5b8"}.fa-solar-panel{--fa:"\f5ba"}.fa-spa{--fa:"\f5bb"}.fa-splotch{--fa:"\f5bc"}.fa-spray-can{--fa:"\f5bd"}.fa-stamp{--fa:"\f5bf"}.fa-star-half-alt,.fa-star-half-stroke{--fa:"\f5c0"}.fa-suitcase-rolling{--fa:"\f5c1"}.fa-face-surprise,.fa-surprise{--fa:"\f5c2"}.fa-swatchbook{--fa:"\f5c3"}.fa-person-swimming,.fa-swimmer{--fa:"\f5c4"}.fa-ladder-water,.fa-swimming-pool,.fa-water-ladder{--fa:"\f5c5"}.fa-droplet-slash,.fa-tint-slash{--fa:"\f5c7"}.fa-face-tired,.fa-tired{--fa:"\f5c8"}.fa-tooth{--fa:"\f5c9"}.fa-umbrella-beach{--fa:"\f5ca"}.fa-weight-hanging{--fa:"\f5cd"}.fa-wine-glass-alt,.fa-wine-glass-empty{--fa:"\f5ce"}.fa-air-freshener,.fa-spray-can-sparkles{--fa:"\f5d0"}.fa-apple-alt,.fa-apple-whole{--fa:"\f5d1"}.fa-atom{--fa:"\f5d2"}.fa-bone{--fa:"\f5d7"}.fa-book-open-reader,.fa-book-reader{--fa:"\f5da"}.fa-brain{--fa:"\f5dc"}.fa-car-alt,.fa-car-rear{--fa:"\f5de"}.fa-battery-car,.fa-car-battery{--fa:"\f5df"}.fa-car-burst,.fa-car-crash{--fa:"\f5e1"}.fa-car-side{--fa:"\f5e4"}.fa-charging-station{--fa:"\f5e7"}.fa-diamond-turn-right,.fa-directions{--fa:"\f5eb"}.fa-draw-polygon,.fa-vector-polygon{--fa:"\f5ee"}.fa-laptop-code{--fa:"\f5fc"}.fa-layer-group{--fa:"\f5fd"}.fa-location,.fa-location-crosshairs{--fa:"\f601"}.fa-lungs{--fa:"\f604"}.fa-microscope{--fa:"\f610"}.fa-oil-can{--fa:"\f613"}.fa-poop{--fa:"\f619"}.fa-shapes,.fa-triangle-circle-square{--fa:"\f61f"}.fa-star-of-life{--fa:"\f621"}.fa-dashboard,.fa-gauge,.fa-gauge-med,.fa-tachometer-alt-average{--fa:"\f624"}.fa-gauge-high,.fa-tachometer-alt,.fa-tachometer-alt-fast{--fa:"\f625"}.fa-gauge-simple,.fa-gauge-simple-med,.fa-tachometer-average{--fa:"\f629"}.fa-gauge-simple-high,.fa-tachometer,.fa-tachometer-fast{--fa:"\f62a"}.fa-teeth{--fa:"\f62e"}.fa-teeth-open{--fa:"\f62f"}.fa-masks-theater,.fa-theater-masks{--fa:"\f630"}.fa-traffic-light{--fa:"\f637"}.fa-truck-monster{--fa:"\f63b"}.fa-truck-pickup{--fa:"\f63c"}.fa-ad,.fa-rectangle-ad{--fa:"\f641"}.fa-ankh{--fa:"\f644"}.fa-bible,.fa-book-bible{--fa:"\f647"}.fa-briefcase-clock,.fa-business-time{--fa:"\f64a"}.fa-city{--fa:"\f64f"}.fa-comment-dollar{--fa:"\f651"}.fa-comments-dollar{--fa:"\f653"}.fa-cross{--fa:"\f654"}.fa-dharmachakra{--fa:"\f655"}.fa-envelope-open-text{--fa:"\f658"}.fa-folder-minus{--fa:"\f65d"}.fa-folder-plus{--fa:"\f65e"}.fa-filter-circle-dollar,.fa-funnel-dollar{--fa:"\f662"}.fa-gopuram{--fa:"\f664"}.fa-hamsa{--fa:"\f665"}.fa-bahai,.fa-haykal{--fa:"\f666"}.fa-jedi{--fa:"\f669"}.fa-book-journal-whills,.fa-journal-whills{--fa:"\f66a"}.fa-kaaba{--fa:"\f66b"}.fa-khanda{--fa:"\f66d"}.fa-landmark{--fa:"\f66f"}.fa-envelopes-bulk,.fa-mail-bulk{--fa:"\f674"}.fa-menorah{--fa:"\f676"}.fa-mosque{--fa:"\f678"}.fa-om{--fa:"\f679"}.fa-pastafarianism,.fa-spaghetti-monster-flying{--fa:"\f67b"}.fa-peace{--fa:"\f67c"}.fa-place-of-worship{--fa:"\f67f"}.fa-poll,.fa-square-poll-vertical{--fa:"\f681"}.fa-poll-h,.fa-square-poll-horizontal{--fa:"\f682"}.fa-person-praying,.fa-pray{--fa:"\f683"}.fa-hands-praying,.fa-praying-hands{--fa:"\f684"}.fa-book-quran,.fa-quran{--fa:"\f687"}.fa-magnifying-glass-dollar,.fa-search-dollar{--fa:"\f688"}.fa-magnifying-glass-location,.fa-search-location{--fa:"\f689"}.fa-socks{--fa:"\f696"}.fa-square-root-alt,.fa-square-root-variable{--fa:"\f698"}.fa-star-and-crescent{--fa:"\f699"}.fa-star-of-david{--fa:"\f69a"}.fa-synagogue{--fa:"\f69b"}.fa-scroll-torah,.fa-torah{--fa:"\f6a0"}.fa-torii-gate{--fa:"\f6a1"}.fa-vihara{--fa:"\f6a7"}.fa-volume-mute,.fa-volume-times,.fa-volume-xmark{--fa:"\f6a9"}.fa-yin-yang{--fa:"\f6ad"}.fa-blender-phone{--fa:"\f6b6"}.fa-book-dead,.fa-book-skull{--fa:"\f6b7"}.fa-campground{--fa:"\f6bb"}.fa-cat{--fa:"\f6be"}.fa-chair{--fa:"\f6c0"}.fa-cloud-moon{--fa:"\f6c3"}.fa-cloud-sun{--fa:"\f6c4"}.fa-cow{--fa:"\f6c8"}.fa-dice-d20{--fa:"\f6cf"}.fa-dice-d6{--fa:"\f6d1"}.fa-dog{--fa:"\f6d3"}.fa-dragon{--fa:"\f6d5"}.fa-drumstick-bite{--fa:"\f6d7"}.fa-dungeon{--fa:"\f6d9"}.fa-file-csv{--fa:"\f6dd"}.fa-fist-raised,.fa-hand-fist{--fa:"\f6de"}.fa-ghost{--fa:"\f6e2"}.fa-hammer{--fa:"\f6e3"}.fa-hanukiah{--fa:"\f6e6"}.fa-hat-wizard{--fa:"\f6e8"}.fa-hiking,.fa-person-hiking{--fa:"\f6ec"}.fa-hippo{--fa:"\f6ed"}.fa-horse{--fa:"\f6f0"}.fa-house-chimney-crack,.fa-house-damage{--fa:"\f6f1"}.fa-hryvnia,.fa-hryvnia-sign{--fa:"\f6f2"}.fa-mask{--fa:"\f6fa"}.fa-mountain{--fa:"\f6fc"}.fa-network-wired{--fa:"\f6ff"}.fa-otter{--fa:"\f700"}.fa-ring{--fa:"\f70b"}.fa-person-running,.fa-running{--fa:"\f70c"}.fa-scroll{--fa:"\f70e"}.fa-skull-crossbones{--fa:"\f714"}.fa-slash{--fa:"\f715"}.fa-spider{--fa:"\f717"}.fa-toilet-paper,.fa-toilet-paper-alt,.fa-toilet-paper-blank{--fa:"\f71e"}.fa-tractor{--fa:"\f722"}.fa-user-injured{--fa:"\f728"}.fa-vr-cardboard{--fa:"\f729"}.fa-wand-sparkles{--fa:"\f72b"}.fa-wind{--fa:"\f72e"}.fa-wine-bottle{--fa:"\f72f"}.fa-cloud-meatball{--fa:"\f73b"}.fa-cloud-moon-rain{--fa:"\f73c"}.fa-cloud-rain{--fa:"\f73d"}.fa-cloud-showers-heavy{--fa:"\f740"}.fa-cloud-sun-rain{--fa:"\f743"}.fa-democrat{--fa:"\f747"}.fa-flag-usa{--fa:"\f74d"}.fa-hurricane{--fa:"\f751"}.fa-landmark-alt,.fa-landmark-dome{--fa:"\f752"}.fa-meteor{--fa:"\f753"}.fa-person-booth{--fa:"\f756"}.fa-poo-bolt,.fa-poo-storm{--fa:"\f75a"}.fa-rainbow{--fa:"\f75b"}.fa-republican{--fa:"\f75e"}.fa-smog{--fa:"\f75f"}.fa-temperature-high{--fa:"\f769"}.fa-temperature-low{--fa:"\f76b"}.fa-cloud-bolt,.fa-thunderstorm{--fa:"\f76c"}.fa-tornado{--fa:"\f76f"}.fa-volcano{--fa:"\f770"}.fa-check-to-slot,.fa-vote-yea{--fa:"\f772"}.fa-water{--fa:"\f773"}.fa-baby{--fa:"\f77c"}.fa-baby-carriage,.fa-carriage-baby{--fa:"\f77d"}.fa-biohazard{--fa:"\f780"}.fa-blog{--fa:"\f781"}.fa-calendar-day{--fa:"\f783"}.fa-calendar-week{--fa:"\f784"}.fa-candy-cane{--fa:"\f786"}.fa-carrot{--fa:"\f787"}.fa-cash-register{--fa:"\f788"}.fa-compress-arrows-alt,.fa-minimize{--fa:"\f78c"}.fa-dumpster{--fa:"\f793"}.fa-dumpster-fire{--fa:"\f794"}.fa-ethernet{--fa:"\f796"}.fa-gifts{--fa:"\f79c"}.fa-champagne-glasses,.fa-glass-cheers{--fa:"\f79f"}.fa-glass-whiskey,.fa-whiskey-glass{--fa:"\f7a0"}.fa-earth-europe,.fa-globe-europe{--fa:"\f7a2"}.fa-grip-lines{--fa:"\f7a4"}.fa-grip-lines-vertical{--fa:"\f7a5"}.fa-guitar{--fa:"\f7a6"}.fa-heart-broken,.fa-heart-crack{--fa:"\f7a9"}.fa-holly-berry{--fa:"\f7aa"}.fa-horse-head{--fa:"\f7ab"}.fa-icicles{--fa:"\f7ad"}.fa-igloo{--fa:"\f7ae"}.fa-mitten{--fa:"\f7b5"}.fa-mug-hot{--fa:"\f7b6"}.fa-radiation{--fa:"\f7b9"}.fa-circle-radiation,.fa-radiation-alt{--fa:"\f7ba"}.fa-restroom{--fa:"\f7bd"}.fa-satellite{--fa:"\f7bf"}.fa-satellite-dish{--fa:"\f7c0"}.fa-sd-card{--fa:"\f7c2"}.fa-sim-card{--fa:"\f7c4"}.fa-person-skating,.fa-skating{--fa:"\f7c5"}.fa-person-skiing,.fa-skiing{--fa:"\f7c9"}.fa-person-skiing-nordic,.fa-skiing-nordic{--fa:"\f7ca"}.fa-sleigh{--fa:"\f7cc"}.fa-comment-sms,.fa-sms{--fa:"\f7cd"}.fa-person-snowboarding,.fa-snowboarding{--fa:"\f7ce"}.fa-snowman{--fa:"\f7d0"}.fa-snowplow{--fa:"\f7d2"}.fa-tenge,.fa-tenge-sign{--fa:"\f7d7"}.fa-toilet{--fa:"\f7d8"}.fa-screwdriver-wrench,.fa-tools{--fa:"\f7d9"}.fa-cable-car,.fa-tram{--fa:"\f7da"}.fa-fire-alt,.fa-fire-flame-curved{--fa:"\f7e4"}.fa-bacon{--fa:"\f7e5"}.fa-book-medical{--fa:"\f7e6"}.fa-bread-slice{--fa:"\f7ec"}.fa-cheese{--fa:"\f7ef"}.fa-clinic-medical,.fa-house-chimney-medical{--fa:"\f7f2"}.fa-clipboard-user{--fa:"\f7f3"}.fa-comment-medical{--fa:"\f7f5"}.fa-crutch{--fa:"\f7f7"}.fa-disease{--fa:"\f7fa"}.fa-egg{--fa:"\f7fb"}.fa-folder-tree{--fa:"\f802"}.fa-burger,.fa-hamburger{--fa:"\f805"}.fa-hand-middle-finger{--fa:"\f806"}.fa-hard-hat,.fa-hat-hard,.fa-helmet-safety{--fa:"\f807"}.fa-hospital-user{--fa:"\f80d"}.fa-hotdog{--fa:"\f80f"}.fa-ice-cream{--fa:"\f810"}.fa-laptop-medical{--fa:"\f812"}.fa-pager{--fa:"\f815"}.fa-pepper-hot{--fa:"\f816"}.fa-pizza-slice{--fa:"\f818"}.fa-sack-dollar{--fa:"\f81d"}.fa-book-tanakh,.fa-tanakh{--fa:"\f827"}.fa-bars-progress,.fa-tasks-alt{--fa:"\f828"}.fa-trash-arrow-up,.fa-trash-restore{--fa:"\f829"}.fa-trash-can-arrow-up,.fa-trash-restore-alt{--fa:"\f82a"}.fa-user-nurse{--fa:"\f82f"}.fa-wave-square{--fa:"\f83e"}.fa-biking,.fa-person-biking{--fa:"\f84a"}.fa-border-all{--fa:"\f84c"}.fa-border-none{--fa:"\f850"}.fa-border-style,.fa-border-top-left{--fa:"\f853"}.fa-digging,.fa-person-digging{--fa:"\f85e"}.fa-fan{--fa:"\f863"}.fa-heart-music-camera-bolt,.fa-icons{--fa:"\f86d"}.fa-phone-alt,.fa-phone-flip{--fa:"\f879"}.fa-phone-square-alt,.fa-square-phone-flip{--fa:"\f87b"}.fa-photo-film,.fa-photo-video{--fa:"\f87c"}.fa-remove-format,.fa-text-slash{--fa:"\f87d"}.fa-arrow-down-z-a,.fa-sort-alpha-desc,.fa-sort-alpha-down-alt{--fa:"\f881"}.fa-arrow-up-z-a,.fa-sort-alpha-up-alt{--fa:"\f882"}.fa-arrow-down-short-wide,.fa-sort-amount-desc,.fa-sort-amount-down-alt{--fa:"\f884"}.fa-arrow-up-short-wide,.fa-sort-amount-up-alt{--fa:"\f885"}.fa-arrow-down-9-1,.fa-sort-numeric-desc,.fa-sort-numeric-down-alt{--fa:"\f886"}.fa-arrow-up-9-1,.fa-sort-numeric-up-alt{--fa:"\f887"}.fa-spell-check{--fa:"\f891"}.fa-voicemail{--fa:"\f897"}.fa-hat-cowboy{--fa:"\f8c0"}.fa-hat-cowboy-side{--fa:"\f8c1"}.fa-computer-mouse,.fa-mouse{--fa:"\f8cc"}.fa-radio{--fa:"\f8d7"}.fa-record-vinyl{--fa:"\f8d9"}.fa-walkie-talkie{--fa:"\f8ef"}.fa-caravan{--fa:"\f8ff"}
:host,:root{--fa-family-brands:"Font Awesome 7 Brands";--fa-font-brands:normal 400 1em/1 var(--fa-family-brands)}@font-face{font-family:"Font Awesome 7 Brands";font-style:normal;font-weight:400;font-display:block;src:url(../webfonts/fa-brands-400.woff2)}.fa-brands,.fa-classic.fa-brands,.fab{--fa-family:var(--fa-family-brands);--fa-style:400}.fa-firefox-browser{--fa:"\e007"}.fa-ideal{--fa:"\e013"}.fa-microblog{--fa:"\e01a"}.fa-pied-piper-square,.fa-square-pied-piper{--fa:"\e01e"}.fa-unity{--fa:"\e049"}.fa-dailymotion{--fa:"\e052"}.fa-instagram-square,.fa-square-instagram{--fa:"\e055"}.fa-mixer{--fa:"\e056"}.fa-shopify{--fa:"\e057"}.fa-deezer{--fa:"\e077"}.fa-edge-legacy{--fa:"\e078"}.fa-google-pay{--fa:"\e079"}.fa-rust{--fa:"\e07a"}.fa-tiktok{--fa:"\e07b"}.fa-unsplash{--fa:"\e07c"}.fa-cloudflare{--fa:"\e07d"}.fa-guilded{--fa:"\e07e"}.fa-hive{--fa:"\e07f"}.fa-42-group,.fa-innosoft{--fa:"\e080"}.fa-instalod{--fa:"\e081"}.fa-octopus-deploy{--fa:"\e082"}.fa-perbyte{--fa:"\e083"}.fa-uncharted{--fa:"\e084"}.fa-watchman-monitoring{--fa:"\e087"}.fa-wodu{--fa:"\e088"}.fa-wirsindhandwerk,.fa-wsh{--fa:"\e2d0"}.fa-bots{--fa:"\e340"}.fa-cmplid{--fa:"\e360"}.fa-bilibili{--fa:"\e3d9"}.fa-golang{--fa:"\e40f"}.fa-pix{--fa:"\e43a"}.fa-sitrox{--fa:"\e44a"}.fa-hashnode{--fa:"\e499"}.fa-meta{--fa:"\e49b"}.fa-padlet{--fa:"\e4a0"}.fa-nfc-directional{--fa:"\e530"}.fa-nfc-symbol{--fa:"\e531"}.fa-screenpal{--fa:"\e570"}.fa-space-awesome{--fa:"\e5ac"}.fa-square-font-awesome{--fa:"\e5ad"}.fa-gitlab-square,.fa-square-gitlab{--fa:"\e5ae"}.fa-odysee{--fa:"\e5c6"}.fa-stubber{--fa:"\e5c7"}.fa-debian{--fa:"\e60b"}.fa-shoelace{--fa:"\e60c"}.fa-threads{--fa:"\e618"}.fa-square-threads{--fa:"\e619"}.fa-square-x-twitter{--fa:"\e61a"}.fa-x-twitter{--fa:"\e61b"}.fa-opensuse{--fa:"\e62b"}.fa-letterboxd{--fa:"\e62d"}.fa-square-letterboxd{--fa:"\e62e"}.fa-mintbit{--fa:"\e62f"}.fa-google-scholar{--fa:"\e63b"}.fa-brave{--fa:"\e63c"}.fa-brave-reverse{--fa:"\e63d"}.fa-pixiv{--fa:"\e640"}.fa-upwork{--fa:"\e641"}.fa-webflow{--fa:"\e65c"}.fa-signal-messenger{--fa:"\e663"}.fa-bluesky{--fa:"\e671"}.fa-jxl{--fa:"\e67b"}.fa-square-upwork{--fa:"\e67c"}.fa-web-awesome{--fa:"\e682"}.fa-square-web-awesome{--fa:"\e683"}.fa-square-web-awesome-stroke{--fa:"\e684"}.fa-dart-lang{--fa:"\e693"}.fa-flutter{--fa:"\e694"}.fa-files-pinwheel{--fa:"\e69f"}.fa-css{--fa:"\e6a2"}.fa-square-bluesky{--fa:"\e6a3"}.fa-openai{--fa:"\e7cf"}.fa-square-linkedin{--fa:"\e7d0"}.fa-cash-app{--fa:"\e7d4"}.fa-disqus{--fa:"\e7d5"}.fa-11ty,.fa-eleventy{--fa:"\e7d6"}.fa-kakao-talk{--fa:"\e7d7"}.fa-linktree{--fa:"\e7d8"}.fa-notion{--fa:"\e7d9"}.fa-pandora{--fa:"\e7da"}.fa-pixelfed{--fa:"\e7db"}.fa-tidal{--fa:"\e7dc"}.fa-vsco{--fa:"\e7dd"}.fa-w3c{--fa:"\e7de"}.fa-lumon{--fa:"\e7e2"}.fa-lumon-drop{--fa:"\e7e3"}.fa-square-figma{--fa:"\e7e4"}.fa-tex{--fa:"\e7ff"}.fa-duolingo{--fa:"\e812"}.fa-square-twitter,.fa-twitter-square{--fa:"\f081"}.fa-facebook-square,.fa-square-facebook{--fa:"\f082"}.fa-linkedin{--fa:"\f08c"}.fa-github-square,.fa-square-github{--fa:"\f092"}.fa-twitter{--fa:"\f099"}.fa-facebook{--fa:"\f09a"}.fa-github{--fa:"\f09b"}.fa-pinterest{--fa:"\f0d2"}.fa-pinterest-square,.fa-square-pinterest{--fa:"\f0d3"}.fa-google-plus-square,.fa-square-google-plus{--fa:"\f0d4"}.fa-google-plus-g{--fa:"\f0d5"}.fa-linkedin-in{--fa:"\f0e1"}.fa-github-alt{--fa:"\f113"}.fa-maxcdn{--fa:"\f136"}.fa-html5{--fa:"\f13b"}.fa-css3{--fa:"\f13c"}.fa-btc{--fa:"\f15a"}.fa-youtube{--fa:"\f167"}.fa-xing{--fa:"\f168"}.fa-square-xing,.fa-xing-square{--fa:"\f169"}.fa-dropbox{--fa:"\f16b"}.fa-stack-overflow{--fa:"\f16c"}.fa-instagram{--fa:"\f16d"}.fa-flickr{--fa:"\f16e"}.fa-adn{--fa:"\f170"}.fa-bitbucket{--fa:"\f171"}.fa-tumblr{--fa:"\f173"}.fa-square-tumblr,.fa-tumblr-square{--fa:"\f174"}.fa-apple{--fa:"\f179"}.fa-windows{--fa:"\f17a"}.fa-android{--fa:"\f17b"}.fa-linux{--fa:"\f17c"}.fa-dribbble{--fa:"\f17d"}.fa-skype{--fa:"\f17e"}.fa-foursquare{--fa:"\f180"}.fa-trello{--fa:"\f181"}.fa-gratipay{--fa:"\f184"}.fa-vk{--fa:"\f189"}.fa-weibo{--fa:"\f18a"}.fa-renren{--fa:"\f18b"}.fa-pagelines{--fa:"\f18c"}.fa-stack-exchange{--fa:"\f18d"}.fa-square-vimeo,.fa-vimeo-square{--fa:"\f194"}.fa-slack,.fa-slack-hash{--fa:"\f198"}.fa-wordpress{--fa:"\f19a"}.fa-openid{--fa:"\f19b"}.fa-yahoo{--fa:"\f19e"}.fa-google{--fa:"\f1a0"}.fa-reddit{--fa:"\f1a1"}.fa-reddit-square,.fa-square-reddit{--fa:"\f1a2"}.fa-stumbleupon-circle{--fa:"\f1a3"}.fa-stumbleupon{--fa:"\f1a4"}.fa-delicious{--fa:"\f1a5"}.fa-digg{--fa:"\f1a6"}.fa-pied-piper-pp{--fa:"\f1a7"}.fa-pied-piper-alt{--fa:"\f1a8"}.fa-drupal{--fa:"\f1a9"}.fa-joomla{--fa:"\f1aa"}.fa-behance{--fa:"\f1b4"}.fa-behance-square,.fa-square-behance{--fa:"\f1b5"}.fa-steam{--fa:"\f1b6"}.fa-square-steam,.fa-steam-square{--fa:"\f1b7"}.fa-spotify{--fa:"\f1bc"}.fa-deviantart{--fa:"\f1bd"}.fa-soundcloud{--fa:"\f1be"}.fa-vine{--fa:"\f1ca"}.fa-codepen{--fa:"\f1cb"}.fa-jsfiddle{--fa:"\f1cc"}.fa-rebel{--fa:"\f1d0"}.fa-empire{--fa:"\f1d1"}.fa-git-square,.fa-square-git{--fa:"\f1d2"}.fa-git{--fa:"\f1d3"}.fa-hacker-news{--fa:"\f1d4"}.fa-tencent-weibo{--fa:"\f1d5"}.fa-qq{--fa:"\f1d6"}.fa-weixin{--fa:"\f1d7"}.fa-slideshare{--fa:"\f1e7"}.fa-twitch{--fa:"\f1e8"}.fa-yelp{--fa:"\f1e9"}.fa-paypal{--fa:"\f1ed"}.fa-google-wallet{--fa:"\f1ee"}.fa-cc-visa{--fa:"\f1f0"}.fa-cc-mastercard{--fa:"\f1f1"}.fa-cc-discover{--fa:"\f1f2"}.fa-cc-amex{--fa:"\f1f3"}.fa-cc-paypal{--fa:"\f1f4"}.fa-cc-stripe{--fa:"\f1f5"}.fa-lastfm{--fa:"\f202"}.fa-lastfm-square,.fa-square-lastfm{--fa:"\f203"}.fa-ioxhost{--fa:"\f208"}.fa-angellist{--fa:"\f209"}.fa-buysellads{--fa:"\f20d"}.fa-connectdevelop{--fa:"\f20e"}.fa-dashcube{--fa:"\f210"}.fa-forumbee{--fa:"\f211"}.fa-leanpub{--fa:"\f212"}.fa-sellsy{--fa:"\f213"}.fa-shirtsinbulk{--fa:"\f214"}.fa-simplybuilt{--fa:"\f215"}.fa-skyatlas{--fa:"\f216"}.fa-pinterest-p{--fa:"\f231"}.fa-whatsapp{--fa:"\f232"}.fa-viacoin{--fa:"\f237"}.fa-medium,.fa-medium-m{--fa:"\f23a"}.fa-y-combinator{--fa:"\f23b"}.fa-optin-monster{--fa:"\f23c"}.fa-opencart{--fa:"\f23d"}.fa-expeditedssl{--fa:"\f23e"}.fa-cc-jcb{--fa:"\f24b"}.fa-cc-diners-club{--fa:"\f24c"}.fa-creative-commons{--fa:"\f25e"}.fa-gg{--fa:"\f260"}.fa-gg-circle{--fa:"\f261"}.fa-odnoklassniki{--fa:"\f263"}.fa-odnoklassniki-square,.fa-square-odnoklassniki{--fa:"\f264"}.fa-get-pocket{--fa:"\f265"}.fa-wikipedia-w{--fa:"\f266"}.fa-safari{--fa:"\f267"}.fa-chrome{--fa:"\f268"}.fa-firefox{--fa:"\f269"}.fa-opera{--fa:"\f26a"}.fa-internet-explorer{--fa:"\f26b"}.fa-contao{--fa:"\f26d"}.fa-500px{--fa:"\f26e"}.fa-amazon{--fa:"\f270"}.fa-houzz{--fa:"\f27c"}.fa-vimeo-v{--fa:"\f27d"}.fa-black-tie{--fa:"\f27e"}.fa-fonticons{--fa:"\f280"}.fa-reddit-alien{--fa:"\f281"}.fa-edge{--fa:"\f282"}.fa-codiepie{--fa:"\f284"}.fa-modx{--fa:"\f285"}.fa-fort-awesome{--fa:"\f286"}.fa-usb{--fa:"\f287"}.fa-product-hunt{--fa:"\f288"}.fa-mixcloud{--fa:"\f289"}.fa-scribd{--fa:"\f28a"}.fa-bluetooth{--fa:"\f293"}.fa-bluetooth-b{--fa:"\f294"}.fa-gitlab{--fa:"\f296"}.fa-wpbeginner{--fa:"\f297"}.fa-wpforms{--fa:"\f298"}.fa-envira{--fa:"\f299"}.fa-glide{--fa:"\f2a5"}.fa-glide-g{--fa:"\f2a6"}.fa-viadeo{--fa:"\f2a9"}.fa-square-viadeo,.fa-viadeo-square{--fa:"\f2aa"}.fa-snapchat,.fa-snapchat-ghost{--fa:"\f2ab"}.fa-snapchat-square,.fa-square-snapchat{--fa:"\f2ad"}.fa-pied-piper{--fa:"\f2ae"}.fa-first-order{--fa:"\f2b0"}.fa-yoast{--fa:"\f2b1"}.fa-themeisle{--fa:"\f2b2"}.fa-google-plus{--fa:"\f2b3"}.fa-font-awesome,.fa-font-awesome-flag,.fa-font-awesome-logo-full{--fa:"\f2b4"}.fa-linode{--fa:"\f2b8"}.fa-quora{--fa:"\f2c4"}.fa-free-code-camp{--fa:"\f2c5"}.fa-telegram,.fa-telegram-plane{--fa:"\f2c6"}.fa-bandcamp{--fa:"\f2d5"}.fa-grav{--fa:"\f2d6"}.fa-etsy{--fa:"\f2d7"}.fa-imdb{--fa:"\f2d8"}.fa-ravelry{--fa:"\f2d9"}.fa-sellcast{--fa:"\f2da"}.fa-superpowers{--fa:"\f2dd"}.fa-wpexplorer{--fa:"\f2de"}.fa-meetup{--fa:"\f2e0"}.fa-font-awesome-alt,.fa-square-font-awesome-stroke{--fa:"\f35c"}.fa-accessible-icon{--fa:"\f368"}.fa-accusoft{--fa:"\f369"}.fa-adversal{--fa:"\f36a"}.fa-affiliatetheme{--fa:"\f36b"}.fa-algolia{--fa:"\f36c"}.fa-amilia{--fa:"\f36d"}.fa-angrycreative{--fa:"\f36e"}.fa-app-store{--fa:"\f36f"}.fa-app-store-ios{--fa:"\f370"}.fa-apper{--fa:"\f371"}.fa-asymmetrik{--fa:"\f372"}.fa-audible{--fa:"\f373"}.fa-avianex{--fa:"\f374"}.fa-aws{--fa:"\f375"}.fa-bimobject{--fa:"\f378"}.fa-bitcoin{--fa:"\f379"}.fa-bity{--fa:"\f37a"}.fa-blackberry{--fa:"\f37b"}.fa-blogger{--fa:"\f37c"}.fa-blogger-b{--fa:"\f37d"}.fa-buromobelexperte{--fa:"\f37f"}.fa-centercode{--fa:"\f380"}.fa-cloudscale{--fa:"\f383"}.fa-cloudsmith{--fa:"\f384"}.fa-cloudversify{--fa:"\f385"}.fa-cpanel{--fa:"\f388"}.fa-css3-alt{--fa:"\f38b"}.fa-cuttlefish{--fa:"\f38c"}.fa-d-and-d{--fa:"\f38d"}.fa-deploydog{--fa:"\f38e"}.fa-deskpro{--fa:"\f38f"}.fa-digital-ocean{--fa:"\f391"}.fa-discord{--fa:"\f392"}.fa-discourse{--fa:"\f393"}.fa-dochub{--fa:"\f394"}.fa-docker{--fa:"\f395"}.fa-draft2digital{--fa:"\f396"}.fa-dribbble-square,.fa-square-dribbble{--fa:"\f397"}.fa-dyalog{--fa:"\f399"}.fa-earlybirds{--fa:"\f39a"}.fa-erlang{--fa:"\f39d"}.fa-facebook-f{--fa:"\f39e"}.fa-facebook-messenger{--fa:"\f39f"}.fa-firstdraft{--fa:"\f3a1"}.fa-fonticons-fi{--fa:"\f3a2"}.fa-fort-awesome-alt{--fa:"\f3a3"}.fa-freebsd{--fa:"\f3a4"}.fa-gitkraken{--fa:"\f3a6"}.fa-gofore{--fa:"\f3a7"}.fa-goodreads{--fa:"\f3a8"}.fa-goodreads-g{--fa:"\f3a9"}.fa-google-drive{--fa:"\f3aa"}.fa-google-play{--fa:"\f3ab"}.fa-gripfire{--fa:"\f3ac"}.fa-grunt{--fa:"\f3ad"}.fa-gulp{--fa:"\f3ae"}.fa-hacker-news-square,.fa-square-hacker-news{--fa:"\f3af"}.fa-hire-a-helper{--fa:"\f3b0"}.fa-hotjar{--fa:"\f3b1"}.fa-hubspot{--fa:"\f3b2"}.fa-itunes{--fa:"\f3b4"}.fa-itunes-note{--fa:"\f3b5"}.fa-jenkins{--fa:"\f3b6"}.fa-joget{--fa:"\f3b7"}.fa-js{--fa:"\f3b8"}.fa-js-square,.fa-square-js{--fa:"\f3b9"}.fa-keycdn{--fa:"\f3ba"}.fa-kickstarter,.fa-square-kickstarter{--fa:"\f3bb"}.fa-kickstarter-k{--fa:"\f3bc"}.fa-laravel{--fa:"\f3bd"}.fa-line{--fa:"\f3c0"}.fa-lyft{--fa:"\f3c3"}.fa-magento{--fa:"\f3c4"}.fa-medapps{--fa:"\f3c6"}.fa-medrt{--fa:"\f3c8"}.fa-microsoft{--fa:"\f3ca"}.fa-mix{--fa:"\f3cb"}.fa-mizuni{--fa:"\f3cc"}.fa-monero{--fa:"\f3d0"}.fa-napster{--fa:"\f3d2"}.fa-node-js{--fa:"\f3d3"}.fa-npm{--fa:"\f3d4"}.fa-ns8{--fa:"\f3d5"}.fa-nutritionix{--fa:"\f3d6"}.fa-page4{--fa:"\f3d7"}.fa-palfed{--fa:"\f3d8"}.fa-patreon{--fa:"\f3d9"}.fa-periscope{--fa:"\f3da"}.fa-phabricator{--fa:"\f3db"}.fa-phoenix-framework{--fa:"\f3dc"}.fa-playstation{--fa:"\f3df"}.fa-pushed{--fa:"\f3e1"}.fa-python{--fa:"\f3e2"}.fa-red-river{--fa:"\f3e3"}.fa-rendact,.fa-wpressr{--fa:"\f3e4"}.fa-replyd{--fa:"\f3e6"}.fa-resolving{--fa:"\f3e7"}.fa-rocketchat{--fa:"\f3e8"}.fa-rockrms{--fa:"\f3e9"}.fa-schlix{--fa:"\f3ea"}.fa-searchengin{--fa:"\f3eb"}.fa-servicestack{--fa:"\f3ec"}.fa-sistrix{--fa:"\f3ee"}.fa-speakap{--fa:"\f3f3"}.fa-staylinked{--fa:"\f3f5"}.fa-steam-symbol{--fa:"\f3f6"}.fa-sticker-mule{--fa:"\f3f7"}.fa-studiovinari{--fa:"\f3f8"}.fa-supple{--fa:"\f3f9"}.fa-uber{--fa:"\f402"}.fa-uikit{--fa:"\f403"}.fa-uniregistry{--fa:"\f404"}.fa-untappd{--fa:"\f405"}.fa-ussunnah{--fa:"\f407"}.fa-vaadin{--fa:"\f408"}.fa-viber{--fa:"\f409"}.fa-vimeo{--fa:"\f40a"}.fa-vnv{--fa:"\f40b"}.fa-square-whatsapp,.fa-whatsapp-square{--fa:"\f40c"}.fa-whmcs{--fa:"\f40d"}.fa-wordpress-simple{--fa:"\f411"}.fa-xbox{--fa:"\f412"}.fa-yandex{--fa:"\f413"}.fa-yandex-international{--fa:"\f414"}.fa-apple-pay{--fa:"\f415"}.fa-cc-apple-pay{--fa:"\f416"}.fa-fly{--fa:"\f417"}.fa-node{--fa:"\f419"}.fa-osi{--fa:"\f41a"}.fa-react{--fa:"\f41b"}.fa-autoprefixer{--fa:"\f41c"}.fa-less{--fa:"\f41d"}.fa-sass{--fa:"\f41e"}.fa-vuejs{--fa:"\f41f"}.fa-angular{--fa:"\f420"}.fa-aviato{--fa:"\f421"}.fa-ember{--fa:"\f423"}.fa-gitter{--fa:"\f426"}.fa-hooli{--fa:"\f427"}.fa-strava{--fa:"\f428"}.fa-stripe{--fa:"\f429"}.fa-stripe-s{--fa:"\f42a"}.fa-typo3{--fa:"\f42b"}.fa-amazon-pay{--fa:"\f42c"}.fa-cc-amazon-pay{--fa:"\f42d"}.fa-ethereum{--fa:"\f42e"}.fa-korvue{--fa:"\f42f"}.fa-elementor{--fa:"\f430"}.fa-square-youtube,.fa-youtube-square{--fa:"\f431"}.fa-flipboard{--fa:"\f44d"}.fa-hips{--fa:"\f452"}.fa-php{--fa:"\f457"}.fa-quinscape{--fa:"\f459"}.fa-readme{--fa:"\f4d5"}.fa-java{--fa:"\f4e4"}.fa-pied-piper-hat{--fa:"\f4e5"}.fa-creative-commons-by{--fa:"\f4e7"}.fa-creative-commons-nc{--fa:"\f4e8"}.fa-creative-commons-nc-eu{--fa:"\f4e9"}.fa-creative-commons-nc-jp{--fa:"\f4ea"}.fa-creative-commons-nd{--fa:"\f4eb"}.fa-creative-commons-pd{--fa:"\f4ec"}.fa-creative-commons-pd-alt{--fa:"\f4ed"}.fa-creative-commons-remix{--fa:"\f4ee"}.fa-creative-commons-sa{--fa:"\f4ef"}.fa-creative-commons-sampling{--fa:"\f4f0"}.fa-creative-commons-sampling-plus{--fa:"\f4f1"}.fa-creative-commons-share{--fa:"\f4f2"}.fa-creative-commons-zero{--fa:"\f4f3"}.fa-ebay{--fa:"\f4f4"}.fa-keybase{--fa:"\f4f5"}.fa-mastodon{--fa:"\f4f6"}.fa-r-project{--fa:"\f4f7"}.fa-researchgate{--fa:"\f4f8"}.fa-teamspeak{--fa:"\f4f9"}.fa-first-order-alt{--fa:"\f50a"}.fa-fulcrum{--fa:"\f50b"}.fa-galactic-republic{--fa:"\f50c"}.fa-galactic-senate{--fa:"\f50d"}.fa-jedi-order{--fa:"\f50e"}.fa-mandalorian{--fa:"\f50f"}.fa-old-republic{--fa:"\f510"}.fa-phoenix-squadron{--fa:"\f511"}.fa-sith{--fa:"\f512"}.fa-trade-federation{--fa:"\f513"}.fa-wolf-pack-battalion{--fa:"\f514"}.fa-hornbill{--fa:"\f592"}.fa-mailchimp{--fa:"\f59e"}.fa-megaport{--fa:"\f5a3"}.fa-nimblr{--fa:"\f5a8"}.fa-rev{--fa:"\f5b2"}.fa-shopware{--fa:"\f5b5"}.fa-squarespace{--fa:"\f5be"}.fa-themeco{--fa:"\f5c6"}.fa-weebly{--fa:"\f5cc"}.fa-wix{--fa:"\f5cf"}.fa-ello{--fa:"\f5f1"}.fa-hackerrank{--fa:"\f5f7"}.fa-kaggle{--fa:"\f5fa"}.fa-markdown{--fa:"\f60f"}.fa-neos{--fa:"\f612"}.fa-zhihu{--fa:"\f63f"}.fa-alipay{--fa:"\f642"}.fa-the-red-yeti{--fa:"\f69d"}.fa-critical-role{--fa:"\f6c9"}.fa-d-and-d-beyond{--fa:"\f6ca"}.fa-dev{--fa:"\f6cc"}.fa-fantasy-flight-games{--fa:"\f6dc"}.fa-wizards-of-the-coast{--fa:"\f730"}.fa-think-peaks{--fa:"\f731"}.fa-reacteurope{--fa:"\f75d"}.fa-artstation{--fa:"\f77a"}.fa-atlassian{--fa:"\f77b"}.fa-canadian-maple-leaf{--fa:"\f785"}.fa-centos{--fa:"\f789"}.fa-confluence{--fa:"\f78d"}.fa-dhl{--fa:"\f790"}.fa-diaspora{--fa:"\f791"}.fa-fedex{--fa:"\f797"}.fa-fedora{--fa:"\f798"}.fa-figma{--fa:"\f799"}.fa-intercom{--fa:"\f7af"}.fa-invision{--fa:"\f7b0"}.fa-jira{--fa:"\f7b1"}.fa-mendeley{--fa:"\f7b3"}.fa-raspberry-pi{--fa:"\f7bb"}.fa-redhat{--fa:"\f7bc"}.fa-sketch{--fa:"\f7c6"}.fa-sourcetree{--fa:"\f7d3"}.fa-suse{--fa:"\f7d6"}.fa-ubuntu{--fa:"\f7df"}.fa-ups{--fa:"\f7e0"}.fa-usps{--fa:"\f7e1"}.fa-yarn{--fa:"\f7e3"}.fa-airbnb{--fa:"\f834"}.fa-battle-net{--fa:"\f835"}.fa-bootstrap{--fa:"\f836"}.fa-buffer{--fa:"\f837"}.fa-chromecast{--fa:"\f838"}.fa-evernote{--fa:"\f839"}.fa-itch-io{--fa:"\f83a"}.fa-salesforce{--fa:"\f83b"}.fa-speaker-deck{--fa:"\f83c"}.fa-symfony{--fa:"\f83d"}.fa-waze{--fa:"\f83f"}.fa-yammer{--fa:"\f840"}.fa-git-alt{--fa:"\f841"}.fa-stackpath{--fa:"\f842"}.fa-cotton-bureau{--fa:"\f89e"}.fa-buy-n-large{--fa:"\f8a6"}.fa-mdb{--fa:"\f8ca"}.fa-orcid{--fa:"\f8d2"}.fa-swift{--fa:"\f8e1"}.fa-umbraco{--fa:"\f8e8"}:host,:root{--fa-font-regular:normal 400 1em/1 var(--fa-family-classic)}@font-face{font-family:"Font Awesome 7 Free";font-style:normal;font-weight:400;font-display:block;src:url(../webfonts/fa-regular-400.woff2)}.far{--fa-family:var(--fa-family-classic)}.fa-regular,.far{--fa-style:400}:host,:root{--fa-family-classic:"Font Awesome 7 Free";--fa-font-solid:normal 900 1em/1 var(--fa-family-classic);--fa-style-family-classic:var(--fa-family-classic)}@font-face{font-family:"Font Awesome 7 Free";font-style:normal;font-weight:900;font-display:block;src:url(../webfonts/fa-solid-900.woff2)}.fas{--fa-style:900}.fa-classic,.fas{--fa-family:var(--fa-family-classic)}.fa-solid{--fa-style:900}@font-face{font-family:"Font Awesome 5 Brands";font-display:block;font-weight:400;src:url(../webfonts/fa-brands-400.woff2) format("woff2")}@font-face{font-family:"Font Awesome 5 Free";font-display:block;font-weight:900;src:url(../webfonts/fa-solid-900.woff2) format("woff2")}@font-face{font-family:"Font Awesome 5 Free";font-display:block;font-weight:400;src:url(../webfonts/fa-regular-400.woff2) format("woff2")}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-solid-900.woff2) format("woff2")}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-brands-400.woff2) format("woff2")}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-regular-400.woff2) format("woff2");unicode-range:u+f003,u+f006,u+f014,u+f016-f017,u+f01a-f01b,u+f01d,u+f022,u+f03e,u+f044,u+f046,u+f05c-f05d,u+f06e,u+f070,u+f087-f088,u+f08a,u+f094,u+f096-f097,u+f09d,u+f0a0,u+f0a2,u+f0a4-f0a7,u+f0c5,u+f0c7,u+f0e5-f0e6,u+f0eb,u+f0f6-f0f8,u+f10c,u+f114-f115,u+f118-f11a,u+f11c-f11d,u+f133,u+f147,u+f14e,u+f150-f152,u+f185-f186,u+f18e,u+f190-f192,u+f196,u+f1c1-f1c9,u+f1d9,u+f1db,u+f1e3,u+f1ea,u+f1f7,u+f1f9,u+f20a,u+f247-f248,u+f24a,u+f24d,u+f255-f25b,u+f25d,u+f271-f274,u+f278,u+f27b,u+f28c,u+f28e,u+f29c,u+f2b5,u+f2b7,u+f2ba,u+f2bc,u+f2be,u+f2c0-f2c1,u+f2c3,u+f2d0,u+f2d2,u+f2d4,u+f2dc}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-v4compatibility.woff2) format("woff2");unicode-range:u+f041,u+f047,u+f065-f066,u+f07d-f07e,u+f080,u+f08b,u+f08e,u+f090,u+f09a,u+f0ac,u+f0ae,u+f0b2,u+f0d0,u+f0d6,u+f0e4,u+f0ec,u+f10a-f10b,u+f123,u+f13e,u+f148-f149,u+f14c,u+f156,u+f15e,u+f160-f161,u+f163,u+f175-f178,u+f195,u+f1f8,u+f219,u+f27a}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\brands.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:root, :host {
  --fa-family-brands: "Font Awesome 7 Brands";
  --fa-font-brands: normal 400 1em/1 var(--fa-family-brands);
}

@font-face {
  font-family: "Font Awesome 7 Brands";
  font-style: normal;
  font-weight: 400;
  font-display: block;
  src: url("../webfonts/fa-brands-400.woff2");
}
.fab,
.fa-brands,
.fa-classic.fa-brands {
  --fa-family: var(--fa-family-brands);
  --fa-style: 400;
}

.fa-firefox-browser {
  --fa: "\e007";
}

.fa-ideal {
  --fa: "\e013";
}

.fa-microblog {
  --fa: "\e01a";
}

.fa-square-pied-piper {
  --fa: "\e01e";
}

.fa-pied-piper-square {
  --fa: "\e01e";
}

.fa-unity {
  --fa: "\e049";
}

.fa-dailymotion {
  --fa: "\e052";
}

.fa-square-instagram {
  --fa: "\e055";
}

.fa-instagram-square {
  --fa: "\e055";
}

.fa-mixer {
  --fa: "\e056";
}

.fa-shopify {
  --fa: "\e057";
}

.fa-deezer {
  --fa: "\e077";
}

.fa-edge-legacy {
  --fa: "\e078";
}

.fa-google-pay {
  --fa: "\e079";
}

.fa-rust {
  --fa: "\e07a";
}

.fa-tiktok {
  --fa: "\e07b";
}

.fa-unsplash {
  --fa: "\e07c";
}

.fa-cloudflare {
  --fa: "\e07d";
}

.fa-guilded {
  --fa: "\e07e";
}

.fa-hive {
  --fa: "\e07f";
}

.fa-42-group {
  --fa: "\e080";
}

.fa-innosoft {
  --fa: "\e080";
}

.fa-instalod {
  --fa: "\e081";
}

.fa-octopus-deploy {
  --fa: "\e082";
}

.fa-perbyte {
  --fa: "\e083";
}

.fa-uncharted {
  --fa: "\e084";
}

.fa-watchman-monitoring {
  --fa: "\e087";
}

.fa-wodu {
  --fa: "\e088";
}

.fa-wirsindhandwerk {
  --fa: "\e2d0";
}

.fa-wsh {
  --fa: "\e2d0";
}

.fa-bots {
  --fa: "\e340";
}

.fa-cmplid {
  --fa: "\e360";
}

.fa-bilibili {
  --fa: "\e3d9";
}

.fa-golang {
  --fa: "\e40f";
}

.fa-pix {
  --fa: "\e43a";
}

.fa-sitrox {
  --fa: "\e44a";
}

.fa-hashnode {
  --fa: "\e499";
}

.fa-meta {
  --fa: "\e49b";
}

.fa-padlet {
  --fa: "\e4a0";
}

.fa-nfc-directional {
  --fa: "\e530";
}

.fa-nfc-symbol {
  --fa: "\e531";
}

.fa-screenpal {
  --fa: "\e570";
}

.fa-space-awesome {
  --fa: "\e5ac";
}

.fa-square-font-awesome {
  --fa: "\e5ad";
}

.fa-square-gitlab {
  --fa: "\e5ae";
}

.fa-gitlab-square {
  --fa: "\e5ae";
}

.fa-odysee {
  --fa: "\e5c6";
}

.fa-stubber {
  --fa: "\e5c7";
}

.fa-debian {
  --fa: "\e60b";
}

.fa-shoelace {
  --fa: "\e60c";
}

.fa-threads {
  --fa: "\e618";
}

.fa-square-threads {
  --fa: "\e619";
}

.fa-square-x-twitter {
  --fa: "\e61a";
}

.fa-x-twitter {
  --fa: "\e61b";
}

.fa-opensuse {
  --fa: "\e62b";
}

.fa-letterboxd {
  --fa: "\e62d";
}

.fa-square-letterboxd {
  --fa: "\e62e";
}

.fa-mintbit {
  --fa: "\e62f";
}

.fa-google-scholar {
  --fa: "\e63b";
}

.fa-brave {
  --fa: "\e63c";
}

.fa-brave-reverse {
  --fa: "\e63d";
}

.fa-pixiv {
  --fa: "\e640";
}

.fa-upwork {
  --fa: "\e641";
}

.fa-webflow {
  --fa: "\e65c";
}

.fa-signal-messenger {
  --fa: "\e663";
}

.fa-bluesky {
  --fa: "\e671";
}

.fa-jxl {
  --fa: "\e67b";
}

.fa-square-upwork {
  --fa: "\e67c";
}

.fa-web-awesome {
  --fa: "\e682";
}

.fa-square-web-awesome {
  --fa: "\e683";
}

.fa-square-web-awesome-stroke {
  --fa: "\e684";
}

.fa-dart-lang {
  --fa: "\e693";
}

.fa-flutter {
  --fa: "\e694";
}

.fa-files-pinwheel {
  --fa: "\e69f";
}

.fa-css {
  --fa: "\e6a2";
}

.fa-square-bluesky {
  --fa: "\e6a3";
}

.fa-openai {
  --fa: "\e7cf";
}

.fa-square-linkedin {
  --fa: "\e7d0";
}

.fa-cash-app {
  --fa: "\e7d4";
}

.fa-disqus {
  --fa: "\e7d5";
}

.fa-eleventy {
  --fa: "\e7d6";
}

.fa-11ty {
  --fa: "\e7d6";
}

.fa-kakao-talk {
  --fa: "\e7d7";
}

.fa-linktree {
  --fa: "\e7d8";
}

.fa-notion {
  --fa: "\e7d9";
}

.fa-pandora {
  --fa: "\e7da";
}

.fa-pixelfed {
  --fa: "\e7db";
}

.fa-tidal {
  --fa: "\e7dc";
}

.fa-vsco {
  --fa: "\e7dd";
}

.fa-w3c {
  --fa: "\e7de";
}

.fa-lumon {
  --fa: "\e7e2";
}

.fa-lumon-drop {
  --fa: "\e7e3";
}

.fa-square-figma {
  --fa: "\e7e4";
}

.fa-tex {
  --fa: "\e7ff";
}

.fa-duolingo {
  --fa: "\e812";
}

.fa-square-twitter {
  --fa: "\f081";
}

.fa-twitter-square {
  --fa: "\f081";
}

.fa-square-facebook {
  --fa: "\f082";
}

.fa-facebook-square {
  --fa: "\f082";
}

.fa-linkedin {
  --fa: "\f08c";
}

.fa-square-github {
  --fa: "\f092";
}

.fa-github-square {
  --fa: "\f092";
}

.fa-twitter {
  --fa: "\f099";
}

.fa-facebook {
  --fa: "\f09a";
}

.fa-github {
  --fa: "\f09b";
}

.fa-pinterest {
  --fa: "\f0d2";
}

.fa-square-pinterest {
  --fa: "\f0d3";
}

.fa-pinterest-square {
  --fa: "\f0d3";
}

.fa-square-google-plus {
  --fa: "\f0d4";
}

.fa-google-plus-square {
  --fa: "\f0d4";
}

.fa-google-plus-g {
  --fa: "\f0d5";
}

.fa-linkedin-in {
  --fa: "\f0e1";
}

.fa-github-alt {
  --fa: "\f113";
}

.fa-maxcdn {
  --fa: "\f136";
}

.fa-html5 {
  --fa: "\f13b";
}

.fa-css3 {
  --fa: "\f13c";
}

.fa-btc {
  --fa: "\f15a";
}

.fa-youtube {
  --fa: "\f167";
}

.fa-xing {
  --fa: "\f168";
}

.fa-square-xing {
  --fa: "\f169";
}

.fa-xing-square {
  --fa: "\f169";
}

.fa-dropbox {
  --fa: "\f16b";
}

.fa-stack-overflow {
  --fa: "\f16c";
}

.fa-instagram {
  --fa: "\f16d";
}

.fa-flickr {
  --fa: "\f16e";
}

.fa-adn {
  --fa: "\f170";
}

.fa-bitbucket {
  --fa: "\f171";
}

.fa-tumblr {
  --fa: "\f173";
}

.fa-square-tumblr {
  --fa: "\f174";
}

.fa-tumblr-square {
  --fa: "\f174";
}

.fa-apple {
  --fa: "\f179";
}

.fa-windows {
  --fa: "\f17a";
}

.fa-android {
  --fa: "\f17b";
}

.fa-linux {
  --fa: "\f17c";
}

.fa-dribbble {
  --fa: "\f17d";
}

.fa-skype {
  --fa: "\f17e";
}

.fa-foursquare {
  --fa: "\f180";
}

.fa-trello {
  --fa: "\f181";
}

.fa-gratipay {
  --fa: "\f184";
}

.fa-vk {
  --fa: "\f189";
}

.fa-weibo {
  --fa: "\f18a";
}

.fa-renren {
  --fa: "\f18b";
}

.fa-pagelines {
  --fa: "\f18c";
}

.fa-stack-exchange {
  --fa: "\f18d";
}

.fa-square-vimeo {
  --fa: "\f194";
}

.fa-vimeo-square {
  --fa: "\f194";
}

.fa-slack {
  --fa: "\f198";
}

.fa-slack-hash {
  --fa: "\f198";
}

.fa-wordpress {
  --fa: "\f19a";
}

.fa-openid {
  --fa: "\f19b";
}

.fa-yahoo {
  --fa: "\f19e";
}

.fa-google {
  --fa: "\f1a0";
}

.fa-reddit {
  --fa: "\f1a1";
}

.fa-square-reddit {
  --fa: "\f1a2";
}

.fa-reddit-square {
  --fa: "\f1a2";
}

.fa-stumbleupon-circle {
  --fa: "\f1a3";
}

.fa-stumbleupon {
  --fa: "\f1a4";
}

.fa-delicious {
  --fa: "\f1a5";
}

.fa-digg {
  --fa: "\f1a6";
}

.fa-pied-piper-pp {
  --fa: "\f1a7";
}

.fa-pied-piper-alt {
  --fa: "\f1a8";
}

.fa-drupal {
  --fa: "\f1a9";
}

.fa-joomla {
  --fa: "\f1aa";
}

.fa-behance {
  --fa: "\f1b4";
}

.fa-square-behance {
  --fa: "\f1b5";
}

.fa-behance-square {
  --fa: "\f1b5";
}

.fa-steam {
  --fa: "\f1b6";
}

.fa-square-steam {
  --fa: "\f1b7";
}

.fa-steam-square {
  --fa: "\f1b7";
}

.fa-spotify {
  --fa: "\f1bc";
}

.fa-deviantart {
  --fa: "\f1bd";
}

.fa-soundcloud {
  --fa: "\f1be";
}

.fa-vine {
  --fa: "\f1ca";
}

.fa-codepen {
  --fa: "\f1cb";
}

.fa-jsfiddle {
  --fa: "\f1cc";
}

.fa-rebel {
  --fa: "\f1d0";
}

.fa-empire {
  --fa: "\f1d1";
}

.fa-square-git {
  --fa: "\f1d2";
}

.fa-git-square {
  --fa: "\f1d2";
}

.fa-git {
  --fa: "\f1d3";
}

.fa-hacker-news {
  --fa: "\f1d4";
}

.fa-tencent-weibo {
  --fa: "\f1d5";
}

.fa-qq {
  --fa: "\f1d6";
}

.fa-weixin {
  --fa: "\f1d7";
}

.fa-slideshare {
  --fa: "\f1e7";
}

.fa-twitch {
  --fa: "\f1e8";
}

.fa-yelp {
  --fa: "\f1e9";
}

.fa-paypal {
  --fa: "\f1ed";
}

.fa-google-wallet {
  --fa: "\f1ee";
}

.fa-cc-visa {
  --fa: "\f1f0";
}

.fa-cc-mastercard {
  --fa: "\f1f1";
}

.fa-cc-discover {
  --fa: "\f1f2";
}

.fa-cc-amex {
  --fa: "\f1f3";
}

.fa-cc-paypal {
  --fa: "\f1f4";
}

.fa-cc-stripe {
  --fa: "\f1f5";
}

.fa-lastfm {
  --fa: "\f202";
}

.fa-square-lastfm {
  --fa: "\f203";
}

.fa-lastfm-square {
  --fa: "\f203";
}

.fa-ioxhost {
  --fa: "\f208";
}

.fa-angellist {
  --fa: "\f209";
}

.fa-buysellads {
  --fa: "\f20d";
}

.fa-connectdevelop {
  --fa: "\f20e";
}

.fa-dashcube {
  --fa: "\f210";
}

.fa-forumbee {
  --fa: "\f211";
}

.fa-leanpub {
  --fa: "\f212";
}

.fa-sellsy {
  --fa: "\f213";
}

.fa-shirtsinbulk {
  --fa: "\f214";
}

.fa-simplybuilt {
  --fa: "\f215";
}

.fa-skyatlas {
  --fa: "\f216";
}

.fa-pinterest-p {
  --fa: "\f231";
}

.fa-whatsapp {
  --fa: "\f232";
}

.fa-viacoin {
  --fa: "\f237";
}

.fa-medium {
  --fa: "\f23a";
}

.fa-medium-m {
  --fa: "\f23a";
}

.fa-y-combinator {
  --fa: "\f23b";
}

.fa-optin-monster {
  --fa: "\f23c";
}

.fa-opencart {
  --fa: "\f23d";
}

.fa-expeditedssl {
  --fa: "\f23e";
}

.fa-cc-jcb {
  --fa: "\f24b";
}

.fa-cc-diners-club {
  --fa: "\f24c";
}

.fa-creative-commons {
  --fa: "\f25e";
}

.fa-gg {
  --fa: "\f260";
}

.fa-gg-circle {
  --fa: "\f261";
}

.fa-odnoklassniki {
  --fa: "\f263";
}

.fa-square-odnoklassniki {
  --fa: "\f264";
}

.fa-odnoklassniki-square {
  --fa: "\f264";
}

.fa-get-pocket {
  --fa: "\f265";
}

.fa-wikipedia-w {
  --fa: "\f266";
}

.fa-safari {
  --fa: "\f267";
}

.fa-chrome {
  --fa: "\f268";
}

.fa-firefox {
  --fa: "\f269";
}

.fa-opera {
  --fa: "\f26a";
}

.fa-internet-explorer {
  --fa: "\f26b";
}

.fa-contao {
  --fa: "\f26d";
}

.fa-500px {
  --fa: "\f26e";
}

.fa-amazon {
  --fa: "\f270";
}

.fa-houzz {
  --fa: "\f27c";
}

.fa-vimeo-v {
  --fa: "\f27d";
}

.fa-black-tie {
  --fa: "\f27e";
}

.fa-fonticons {
  --fa: "\f280";
}

.fa-reddit-alien {
  --fa: "\f281";
}

.fa-edge {
  --fa: "\f282";
}

.fa-codiepie {
  --fa: "\f284";
}

.fa-modx {
  --fa: "\f285";
}

.fa-fort-awesome {
  --fa: "\f286";
}

.fa-usb {
  --fa: "\f287";
}

.fa-product-hunt {
  --fa: "\f288";
}

.fa-mixcloud {
  --fa: "\f289";
}

.fa-scribd {
  --fa: "\f28a";
}

.fa-bluetooth {
  --fa: "\f293";
}

.fa-bluetooth-b {
  --fa: "\f294";
}

.fa-gitlab {
  --fa: "\f296";
}

.fa-wpbeginner {
  --fa: "\f297";
}

.fa-wpforms {
  --fa: "\f298";
}

.fa-envira {
  --fa: "\f299";
}

.fa-glide {
  --fa: "\f2a5";
}

.fa-glide-g {
  --fa: "\f2a6";
}

.fa-viadeo {
  --fa: "\f2a9";
}

.fa-square-viadeo {
  --fa: "\f2aa";
}

.fa-viadeo-square {
  --fa: "\f2aa";
}

.fa-snapchat {
  --fa: "\f2ab";
}

.fa-snapchat-ghost {
  --fa: "\f2ab";
}

.fa-square-snapchat {
  --fa: "\f2ad";
}

.fa-snapchat-square {
  --fa: "\f2ad";
}

.fa-pied-piper {
  --fa: "\f2ae";
}

.fa-first-order {
  --fa: "\f2b0";
}

.fa-yoast {
  --fa: "\f2b1";
}

.fa-themeisle {
  --fa: "\f2b2";
}

.fa-google-plus {
  --fa: "\f2b3";
}

.fa-font-awesome {
  --fa: "\f2b4";
}

.fa-font-awesome-flag {
  --fa: "\f2b4";
}

.fa-font-awesome-logo-full {
  --fa: "\f2b4";
}

.fa-linode {
  --fa: "\f2b8";
}

.fa-quora {
  --fa: "\f2c4";
}

.fa-free-code-camp {
  --fa: "\f2c5";
}

.fa-telegram {
  --fa: "\f2c6";
}

.fa-telegram-plane {
  --fa: "\f2c6";
}

.fa-bandcamp {
  --fa: "\f2d5";
}

.fa-grav {
  --fa: "\f2d6";
}

.fa-etsy {
  --fa: "\f2d7";
}

.fa-imdb {
  --fa: "\f2d8";
}

.fa-ravelry {
  --fa: "\f2d9";
}

.fa-sellcast {
  --fa: "\f2da";
}

.fa-superpowers {
  --fa: "\f2dd";
}

.fa-wpexplorer {
  --fa: "\f2de";
}

.fa-meetup {
  --fa: "\f2e0";
}

.fa-square-font-awesome-stroke {
  --fa: "\f35c";
}

.fa-font-awesome-alt {
  --fa: "\f35c";
}

.fa-accessible-icon {
  --fa: "\f368";
}

.fa-accusoft {
  --fa: "\f369";
}

.fa-adversal {
  --fa: "\f36a";
}

.fa-affiliatetheme {
  --fa: "\f36b";
}

.fa-algolia {
  --fa: "\f36c";
}

.fa-amilia {
  --fa: "\f36d";
}

.fa-angrycreative {
  --fa: "\f36e";
}

.fa-app-store {
  --fa: "\f36f";
}

.fa-app-store-ios {
  --fa: "\f370";
}

.fa-apper {
  --fa: "\f371";
}

.fa-asymmetrik {
  --fa: "\f372";
}

.fa-audible {
  --fa: "\f373";
}

.fa-avianex {
  --fa: "\f374";
}

.fa-aws {
  --fa: "\f375";
}

.fa-bimobject {
  --fa: "\f378";
}

.fa-bitcoin {
  --fa: "\f379";
}

.fa-bity {
  --fa: "\f37a";
}

.fa-blackberry {
  --fa: "\f37b";
}

.fa-blogger {
  --fa: "\f37c";
}

.fa-blogger-b {
  --fa: "\f37d";
}

.fa-buromobelexperte {
  --fa: "\f37f";
}

.fa-centercode {
  --fa: "\f380";
}

.fa-cloudscale {
  --fa: "\f383";
}

.fa-cloudsmith {
  --fa: "\f384";
}

.fa-cloudversify {
  --fa: "\f385";
}

.fa-cpanel {
  --fa: "\f388";
}

.fa-css3-alt {
  --fa: "\f38b";
}

.fa-cuttlefish {
  --fa: "\f38c";
}

.fa-d-and-d {
  --fa: "\f38d";
}

.fa-deploydog {
  --fa: "\f38e";
}

.fa-deskpro {
  --fa: "\f38f";
}

.fa-digital-ocean {
  --fa: "\f391";
}

.fa-discord {
  --fa: "\f392";
}

.fa-discourse {
  --fa: "\f393";
}

.fa-dochub {
  --fa: "\f394";
}

.fa-docker {
  --fa: "\f395";
}

.fa-draft2digital {
  --fa: "\f396";
}

.fa-square-dribbble {
  --fa: "\f397";
}

.fa-dribbble-square {
  --fa: "\f397";
}

.fa-dyalog {
  --fa: "\f399";
}

.fa-earlybirds {
  --fa: "\f39a";
}

.fa-erlang {
  --fa: "\f39d";
}

.fa-facebook-f {
  --fa: "\f39e";
}

.fa-facebook-messenger {
  --fa: "\f39f";
}

.fa-firstdraft {
  --fa: "\f3a1";
}

.fa-fonticons-fi {
  --fa: "\f3a2";
}

.fa-fort-awesome-alt {
  --fa: "\f3a3";
}

.fa-freebsd {
  --fa: "\f3a4";
}

.fa-gitkraken {
  --fa: "\f3a6";
}

.fa-gofore {
  --fa: "\f3a7";
}

.fa-goodreads {
  --fa: "\f3a8";
}

.fa-goodreads-g {
  --fa: "\f3a9";
}

.fa-google-drive {
  --fa: "\f3aa";
}

.fa-google-play {
  --fa: "\f3ab";
}

.fa-gripfire {
  --fa: "\f3ac";
}

.fa-grunt {
  --fa: "\f3ad";
}

.fa-gulp {
  --fa: "\f3ae";
}

.fa-square-hacker-news {
  --fa: "\f3af";
}

.fa-hacker-news-square {
  --fa: "\f3af";
}

.fa-hire-a-helper {
  --fa: "\f3b0";
}

.fa-hotjar {
  --fa: "\f3b1";
}

.fa-hubspot {
  --fa: "\f3b2";
}

.fa-itunes {
  --fa: "\f3b4";
}

.fa-itunes-note {
  --fa: "\f3b5";
}

.fa-jenkins {
  --fa: "\f3b6";
}

.fa-joget {
  --fa: "\f3b7";
}

.fa-js {
  --fa: "\f3b8";
}

.fa-square-js {
  --fa: "\f3b9";
}

.fa-js-square {
  --fa: "\f3b9";
}

.fa-keycdn {
  --fa: "\f3ba";
}

.fa-kickstarter {
  --fa: "\f3bb";
}

.fa-square-kickstarter {
  --fa: "\f3bb";
}

.fa-kickstarter-k {
  --fa: "\f3bc";
}

.fa-laravel {
  --fa: "\f3bd";
}

.fa-line {
  --fa: "\f3c0";
}

.fa-lyft {
  --fa: "\f3c3";
}

.fa-magento {
  --fa: "\f3c4";
}

.fa-medapps {
  --fa: "\f3c6";
}

.fa-medrt {
  --fa: "\f3c8";
}

.fa-microsoft {
  --fa: "\f3ca";
}

.fa-mix {
  --fa: "\f3cb";
}

.fa-mizuni {
  --fa: "\f3cc";
}

.fa-monero {
  --fa: "\f3d0";
}

.fa-napster {
  --fa: "\f3d2";
}

.fa-node-js {
  --fa: "\f3d3";
}

.fa-npm {
  --fa: "\f3d4";
}

.fa-ns8 {
  --fa: "\f3d5";
}

.fa-nutritionix {
  --fa: "\f3d6";
}

.fa-page4 {
  --fa: "\f3d7";
}

.fa-palfed {
  --fa: "\f3d8";
}

.fa-patreon {
  --fa: "\f3d9";
}

.fa-periscope {
  --fa: "\f3da";
}

.fa-phabricator {
  --fa: "\f3db";
}

.fa-phoenix-framework {
  --fa: "\f3dc";
}

.fa-playstation {
  --fa: "\f3df";
}

.fa-pushed {
  --fa: "\f3e1";
}

.fa-python {
  --fa: "\f3e2";
}

.fa-red-river {
  --fa: "\f3e3";
}

.fa-wpressr {
  --fa: "\f3e4";
}

.fa-rendact {
  --fa: "\f3e4";
}

.fa-replyd {
  --fa: "\f3e6";
}

.fa-resolving {
  --fa: "\f3e7";
}

.fa-rocketchat {
  --fa: "\f3e8";
}

.fa-rockrms {
  --fa: "\f3e9";
}

.fa-schlix {
  --fa: "\f3ea";
}

.fa-searchengin {
  --fa: "\f3eb";
}

.fa-servicestack {
  --fa: "\f3ec";
}

.fa-sistrix {
  --fa: "\f3ee";
}

.fa-speakap {
  --fa: "\f3f3";
}

.fa-staylinked {
  --fa: "\f3f5";
}

.fa-steam-symbol {
  --fa: "\f3f6";
}

.fa-sticker-mule {
  --fa: "\f3f7";
}

.fa-studiovinari {
  --fa: "\f3f8";
}

.fa-supple {
  --fa: "\f3f9";
}

.fa-uber {
  --fa: "\f402";
}

.fa-uikit {
  --fa: "\f403";
}

.fa-uniregistry {
  --fa: "\f404";
}

.fa-untappd {
  --fa: "\f405";
}

.fa-ussunnah {
  --fa: "\f407";
}

.fa-vaadin {
  --fa: "\f408";
}

.fa-viber {
  --fa: "\f409";
}

.fa-vimeo {
  --fa: "\f40a";
}

.fa-vnv {
  --fa: "\f40b";
}

.fa-square-whatsapp {
  --fa: "\f40c";
}

.fa-whatsapp-square {
  --fa: "\f40c";
}

.fa-whmcs {
  --fa: "\f40d";
}

.fa-wordpress-simple {
  --fa: "\f411";
}

.fa-xbox {
  --fa: "\f412";
}

.fa-yandex {
  --fa: "\f413";
}

.fa-yandex-international {
  --fa: "\f414";
}

.fa-apple-pay {
  --fa: "\f415";
}

.fa-cc-apple-pay {
  --fa: "\f416";
}

.fa-fly {
  --fa: "\f417";
}

.fa-node {
  --fa: "\f419";
}

.fa-osi {
  --fa: "\f41a";
}

.fa-react {
  --fa: "\f41b";
}

.fa-autoprefixer {
  --fa: "\f41c";
}

.fa-less {
  --fa: "\f41d";
}

.fa-sass {
  --fa: "\f41e";
}

.fa-vuejs {
  --fa: "\f41f";
}

.fa-angular {
  --fa: "\f420";
}

.fa-aviato {
  --fa: "\f421";
}

.fa-ember {
  --fa: "\f423";
}

.fa-gitter {
  --fa: "\f426";
}

.fa-hooli {
  --fa: "\f427";
}

.fa-strava {
  --fa: "\f428";
}

.fa-stripe {
  --fa: "\f429";
}

.fa-stripe-s {
  --fa: "\f42a";
}

.fa-typo3 {
  --fa: "\f42b";
}

.fa-amazon-pay {
  --fa: "\f42c";
}

.fa-cc-amazon-pay {
  --fa: "\f42d";
}

.fa-ethereum {
  --fa: "\f42e";
}

.fa-korvue {
  --fa: "\f42f";
}

.fa-elementor {
  --fa: "\f430";
}

.fa-square-youtube {
  --fa: "\f431";
}

.fa-youtube-square {
  --fa: "\f431";
}

.fa-flipboard {
  --fa: "\f44d";
}

.fa-hips {
  --fa: "\f452";
}

.fa-php {
  --fa: "\f457";
}

.fa-quinscape {
  --fa: "\f459";
}

.fa-readme {
  --fa: "\f4d5";
}

.fa-java {
  --fa: "\f4e4";
}

.fa-pied-piper-hat {
  --fa: "\f4e5";
}

.fa-creative-commons-by {
  --fa: "\f4e7";
}

.fa-creative-commons-nc {
  --fa: "\f4e8";
}

.fa-creative-commons-nc-eu {
  --fa: "\f4e9";
}

.fa-creative-commons-nc-jp {
  --fa: "\f4ea";
}

.fa-creative-commons-nd {
  --fa: "\f4eb";
}

.fa-creative-commons-pd {
  --fa: "\f4ec";
}

.fa-creative-commons-pd-alt {
  --fa: "\f4ed";
}

.fa-creative-commons-remix {
  --fa: "\f4ee";
}

.fa-creative-commons-sa {
  --fa: "\f4ef";
}

.fa-creative-commons-sampling {
  --fa: "\f4f0";
}

.fa-creative-commons-sampling-plus {
  --fa: "\f4f1";
}

.fa-creative-commons-share {
  --fa: "\f4f2";
}

.fa-creative-commons-zero {
  --fa: "\f4f3";
}

.fa-ebay {
  --fa: "\f4f4";
}

.fa-keybase {
  --fa: "\f4f5";
}

.fa-mastodon {
  --fa: "\f4f6";
}

.fa-r-project {
  --fa: "\f4f7";
}

.fa-researchgate {
  --fa: "\f4f8";
}

.fa-teamspeak {
  --fa: "\f4f9";
}

.fa-first-order-alt {
  --fa: "\f50a";
}

.fa-fulcrum {
  --fa: "\f50b";
}

.fa-galactic-republic {
  --fa: "\f50c";
}

.fa-galactic-senate {
  --fa: "\f50d";
}

.fa-jedi-order {
  --fa: "\f50e";
}

.fa-mandalorian {
  --fa: "\f50f";
}

.fa-old-republic {
  --fa: "\f510";
}

.fa-phoenix-squadron {
  --fa: "\f511";
}

.fa-sith {
  --fa: "\f512";
}

.fa-trade-federation {
  --fa: "\f513";
}

.fa-wolf-pack-battalion {
  --fa: "\f514";
}

.fa-hornbill {
  --fa: "\f592";
}

.fa-mailchimp {
  --fa: "\f59e";
}

.fa-megaport {
  --fa: "\f5a3";
}

.fa-nimblr {
  --fa: "\f5a8";
}

.fa-rev {
  --fa: "\f5b2";
}

.fa-shopware {
  --fa: "\f5b5";
}

.fa-squarespace {
  --fa: "\f5be";
}

.fa-themeco {
  --fa: "\f5c6";
}

.fa-weebly {
  --fa: "\f5cc";
}

.fa-wix {
  --fa: "\f5cf";
}

.fa-ello {
  --fa: "\f5f1";
}

.fa-hackerrank {
  --fa: "\f5f7";
}

.fa-kaggle {
  --fa: "\f5fa";
}

.fa-markdown {
  --fa: "\f60f";
}

.fa-neos {
  --fa: "\f612";
}

.fa-zhihu {
  --fa: "\f63f";
}

.fa-alipay {
  --fa: "\f642";
}

.fa-the-red-yeti {
  --fa: "\f69d";
}

.fa-critical-role {
  --fa: "\f6c9";
}

.fa-d-and-d-beyond {
  --fa: "\f6ca";
}

.fa-dev {
  --fa: "\f6cc";
}

.fa-fantasy-flight-games {
  --fa: "\f6dc";
}

.fa-wizards-of-the-coast {
  --fa: "\f730";
}

.fa-think-peaks {
  --fa: "\f731";
}

.fa-reacteurope {
  --fa: "\f75d";
}

.fa-artstation {
  --fa: "\f77a";
}

.fa-atlassian {
  --fa: "\f77b";
}

.fa-canadian-maple-leaf {
  --fa: "\f785";
}

.fa-centos {
  --fa: "\f789";
}

.fa-confluence {
  --fa: "\f78d";
}

.fa-dhl {
  --fa: "\f790";
}

.fa-diaspora {
  --fa: "\f791";
}

.fa-fedex {
  --fa: "\f797";
}

.fa-fedora {
  --fa: "\f798";
}

.fa-figma {
  --fa: "\f799";
}

.fa-intercom {
  --fa: "\f7af";
}

.fa-invision {
  --fa: "\f7b0";
}

.fa-jira {
  --fa: "\f7b1";
}

.fa-mendeley {
  --fa: "\f7b3";
}

.fa-raspberry-pi {
  --fa: "\f7bb";
}

.fa-redhat {
  --fa: "\f7bc";
}

.fa-sketch {
  --fa: "\f7c6";
}

.fa-sourcetree {
  --fa: "\f7d3";
}

.fa-suse {
  --fa: "\f7d6";
}

.fa-ubuntu {
  --fa: "\f7df";
}

.fa-ups {
  --fa: "\f7e0";
}

.fa-usps {
  --fa: "\f7e1";
}

.fa-yarn {
  --fa: "\f7e3";
}

.fa-airbnb {
  --fa: "\f834";
}

.fa-battle-net {
  --fa: "\f835";
}

.fa-bootstrap {
  --fa: "\f836";
}

.fa-buffer {
  --fa: "\f837";
}

.fa-chromecast {
  --fa: "\f838";
}

.fa-evernote {
  --fa: "\f839";
}

.fa-itch-io {
  --fa: "\f83a";
}

.fa-salesforce {
  --fa: "\f83b";
}

.fa-speaker-deck {
  --fa: "\f83c";
}

.fa-symfony {
  --fa: "\f83d";
}

.fa-waze {
  --fa: "\f83f";
}

.fa-yammer {
  --fa: "\f840";
}

.fa-git-alt {
  --fa: "\f841";
}

.fa-stackpath {
  --fa: "\f842";
}

.fa-cotton-bureau {
  --fa: "\f89e";
}

.fa-buy-n-large {
  --fa: "\f8a6";
}

.fa-mdb {
  --fa: "\f8ca";
}

.fa-orcid {
  --fa: "\f8d2";
}

.fa-swift {
  --fa: "\f8e1";
}

.fa-umbraco {
  --fa: "\f8e8";
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\brands.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:host,:root{--fa-family-brands:"Font Awesome 7 Brands";--fa-font-brands:normal 400 1em/1 var(--fa-family-brands)}@font-face{font-family:"Font Awesome 7 Brands";font-style:normal;font-weight:400;font-display:block;src:url(../webfonts/fa-brands-400.woff2)}.fa-brands,.fa-classic.fa-brands,.fab{--fa-family:var(--fa-family-brands);--fa-style:400}.fa-firefox-browser{--fa:"\e007"}.fa-ideal{--fa:"\e013"}.fa-microblog{--fa:"\e01a"}.fa-pied-piper-square,.fa-square-pied-piper{--fa:"\e01e"}.fa-unity{--fa:"\e049"}.fa-dailymotion{--fa:"\e052"}.fa-instagram-square,.fa-square-instagram{--fa:"\e055"}.fa-mixer{--fa:"\e056"}.fa-shopify{--fa:"\e057"}.fa-deezer{--fa:"\e077"}.fa-edge-legacy{--fa:"\e078"}.fa-google-pay{--fa:"\e079"}.fa-rust{--fa:"\e07a"}.fa-tiktok{--fa:"\e07b"}.fa-unsplash{--fa:"\e07c"}.fa-cloudflare{--fa:"\e07d"}.fa-guilded{--fa:"\e07e"}.fa-hive{--fa:"\e07f"}.fa-42-group,.fa-innosoft{--fa:"\e080"}.fa-instalod{--fa:"\e081"}.fa-octopus-deploy{--fa:"\e082"}.fa-perbyte{--fa:"\e083"}.fa-uncharted{--fa:"\e084"}.fa-watchman-monitoring{--fa:"\e087"}.fa-wodu{--fa:"\e088"}.fa-wirsindhandwerk,.fa-wsh{--fa:"\e2d0"}.fa-bots{--fa:"\e340"}.fa-cmplid{--fa:"\e360"}.fa-bilibili{--fa:"\e3d9"}.fa-golang{--fa:"\e40f"}.fa-pix{--fa:"\e43a"}.fa-sitrox{--fa:"\e44a"}.fa-hashnode{--fa:"\e499"}.fa-meta{--fa:"\e49b"}.fa-padlet{--fa:"\e4a0"}.fa-nfc-directional{--fa:"\e530"}.fa-nfc-symbol{--fa:"\e531"}.fa-screenpal{--fa:"\e570"}.fa-space-awesome{--fa:"\e5ac"}.fa-square-font-awesome{--fa:"\e5ad"}.fa-gitlab-square,.fa-square-gitlab{--fa:"\e5ae"}.fa-odysee{--fa:"\e5c6"}.fa-stubber{--fa:"\e5c7"}.fa-debian{--fa:"\e60b"}.fa-shoelace{--fa:"\e60c"}.fa-threads{--fa:"\e618"}.fa-square-threads{--fa:"\e619"}.fa-square-x-twitter{--fa:"\e61a"}.fa-x-twitter{--fa:"\e61b"}.fa-opensuse{--fa:"\e62b"}.fa-letterboxd{--fa:"\e62d"}.fa-square-letterboxd{--fa:"\e62e"}.fa-mintbit{--fa:"\e62f"}.fa-google-scholar{--fa:"\e63b"}.fa-brave{--fa:"\e63c"}.fa-brave-reverse{--fa:"\e63d"}.fa-pixiv{--fa:"\e640"}.fa-upwork{--fa:"\e641"}.fa-webflow{--fa:"\e65c"}.fa-signal-messenger{--fa:"\e663"}.fa-bluesky{--fa:"\e671"}.fa-jxl{--fa:"\e67b"}.fa-square-upwork{--fa:"\e67c"}.fa-web-awesome{--fa:"\e682"}.fa-square-web-awesome{--fa:"\e683"}.fa-square-web-awesome-stroke{--fa:"\e684"}.fa-dart-lang{--fa:"\e693"}.fa-flutter{--fa:"\e694"}.fa-files-pinwheel{--fa:"\e69f"}.fa-css{--fa:"\e6a2"}.fa-square-bluesky{--fa:"\e6a3"}.fa-openai{--fa:"\e7cf"}.fa-square-linkedin{--fa:"\e7d0"}.fa-cash-app{--fa:"\e7d4"}.fa-disqus{--fa:"\e7d5"}.fa-11ty,.fa-eleventy{--fa:"\e7d6"}.fa-kakao-talk{--fa:"\e7d7"}.fa-linktree{--fa:"\e7d8"}.fa-notion{--fa:"\e7d9"}.fa-pandora{--fa:"\e7da"}.fa-pixelfed{--fa:"\e7db"}.fa-tidal{--fa:"\e7dc"}.fa-vsco{--fa:"\e7dd"}.fa-w3c{--fa:"\e7de"}.fa-lumon{--fa:"\e7e2"}.fa-lumon-drop{--fa:"\e7e3"}.fa-square-figma{--fa:"\e7e4"}.fa-tex{--fa:"\e7ff"}.fa-duolingo{--fa:"\e812"}.fa-square-twitter,.fa-twitter-square{--fa:"\f081"}.fa-facebook-square,.fa-square-facebook{--fa:"\f082"}.fa-linkedin{--fa:"\f08c"}.fa-github-square,.fa-square-github{--fa:"\f092"}.fa-twitter{--fa:"\f099"}.fa-facebook{--fa:"\f09a"}.fa-github{--fa:"\f09b"}.fa-pinterest{--fa:"\f0d2"}.fa-pinterest-square,.fa-square-pinterest{--fa:"\f0d3"}.fa-google-plus-square,.fa-square-google-plus{--fa:"\f0d4"}.fa-google-plus-g{--fa:"\f0d5"}.fa-linkedin-in{--fa:"\f0e1"}.fa-github-alt{--fa:"\f113"}.fa-maxcdn{--fa:"\f136"}.fa-html5{--fa:"\f13b"}.fa-css3{--fa:"\f13c"}.fa-btc{--fa:"\f15a"}.fa-youtube{--fa:"\f167"}.fa-xing{--fa:"\f168"}.fa-square-xing,.fa-xing-square{--fa:"\f169"}.fa-dropbox{--fa:"\f16b"}.fa-stack-overflow{--fa:"\f16c"}.fa-instagram{--fa:"\f16d"}.fa-flickr{--fa:"\f16e"}.fa-adn{--fa:"\f170"}.fa-bitbucket{--fa:"\f171"}.fa-tumblr{--fa:"\f173"}.fa-square-tumblr,.fa-tumblr-square{--fa:"\f174"}.fa-apple{--fa:"\f179"}.fa-windows{--fa:"\f17a"}.fa-android{--fa:"\f17b"}.fa-linux{--fa:"\f17c"}.fa-dribbble{--fa:"\f17d"}.fa-skype{--fa:"\f17e"}.fa-foursquare{--fa:"\f180"}.fa-trello{--fa:"\f181"}.fa-gratipay{--fa:"\f184"}.fa-vk{--fa:"\f189"}.fa-weibo{--fa:"\f18a"}.fa-renren{--fa:"\f18b"}.fa-pagelines{--fa:"\f18c"}.fa-stack-exchange{--fa:"\f18d"}.fa-square-vimeo,.fa-vimeo-square{--fa:"\f194"}.fa-slack,.fa-slack-hash{--fa:"\f198"}.fa-wordpress{--fa:"\f19a"}.fa-openid{--fa:"\f19b"}.fa-yahoo{--fa:"\f19e"}.fa-google{--fa:"\f1a0"}.fa-reddit{--fa:"\f1a1"}.fa-reddit-square,.fa-square-reddit{--fa:"\f1a2"}.fa-stumbleupon-circle{--fa:"\f1a3"}.fa-stumbleupon{--fa:"\f1a4"}.fa-delicious{--fa:"\f1a5"}.fa-digg{--fa:"\f1a6"}.fa-pied-piper-pp{--fa:"\f1a7"}.fa-pied-piper-alt{--fa:"\f1a8"}.fa-drupal{--fa:"\f1a9"}.fa-joomla{--fa:"\f1aa"}.fa-behance{--fa:"\f1b4"}.fa-behance-square,.fa-square-behance{--fa:"\f1b5"}.fa-steam{--fa:"\f1b6"}.fa-square-steam,.fa-steam-square{--fa:"\f1b7"}.fa-spotify{--fa:"\f1bc"}.fa-deviantart{--fa:"\f1bd"}.fa-soundcloud{--fa:"\f1be"}.fa-vine{--fa:"\f1ca"}.fa-codepen{--fa:"\f1cb"}.fa-jsfiddle{--fa:"\f1cc"}.fa-rebel{--fa:"\f1d0"}.fa-empire{--fa:"\f1d1"}.fa-git-square,.fa-square-git{--fa:"\f1d2"}.fa-git{--fa:"\f1d3"}.fa-hacker-news{--fa:"\f1d4"}.fa-tencent-weibo{--fa:"\f1d5"}.fa-qq{--fa:"\f1d6"}.fa-weixin{--fa:"\f1d7"}.fa-slideshare{--fa:"\f1e7"}.fa-twitch{--fa:"\f1e8"}.fa-yelp{--fa:"\f1e9"}.fa-paypal{--fa:"\f1ed"}.fa-google-wallet{--fa:"\f1ee"}.fa-cc-visa{--fa:"\f1f0"}.fa-cc-mastercard{--fa:"\f1f1"}.fa-cc-discover{--fa:"\f1f2"}.fa-cc-amex{--fa:"\f1f3"}.fa-cc-paypal{--fa:"\f1f4"}.fa-cc-stripe{--fa:"\f1f5"}.fa-lastfm{--fa:"\f202"}.fa-lastfm-square,.fa-square-lastfm{--fa:"\f203"}.fa-ioxhost{--fa:"\f208"}.fa-angellist{--fa:"\f209"}.fa-buysellads{--fa:"\f20d"}.fa-connectdevelop{--fa:"\f20e"}.fa-dashcube{--fa:"\f210"}.fa-forumbee{--fa:"\f211"}.fa-leanpub{--fa:"\f212"}.fa-sellsy{--fa:"\f213"}.fa-shirtsinbulk{--fa:"\f214"}.fa-simplybuilt{--fa:"\f215"}.fa-skyatlas{--fa:"\f216"}.fa-pinterest-p{--fa:"\f231"}.fa-whatsapp{--fa:"\f232"}.fa-viacoin{--fa:"\f237"}.fa-medium,.fa-medium-m{--fa:"\f23a"}.fa-y-combinator{--fa:"\f23b"}.fa-optin-monster{--fa:"\f23c"}.fa-opencart{--fa:"\f23d"}.fa-expeditedssl{--fa:"\f23e"}.fa-cc-jcb{--fa:"\f24b"}.fa-cc-diners-club{--fa:"\f24c"}.fa-creative-commons{--fa:"\f25e"}.fa-gg{--fa:"\f260"}.fa-gg-circle{--fa:"\f261"}.fa-odnoklassniki{--fa:"\f263"}.fa-odnoklassniki-square,.fa-square-odnoklassniki{--fa:"\f264"}.fa-get-pocket{--fa:"\f265"}.fa-wikipedia-w{--fa:"\f266"}.fa-safari{--fa:"\f267"}.fa-chrome{--fa:"\f268"}.fa-firefox{--fa:"\f269"}.fa-opera{--fa:"\f26a"}.fa-internet-explorer{--fa:"\f26b"}.fa-contao{--fa:"\f26d"}.fa-500px{--fa:"\f26e"}.fa-amazon{--fa:"\f270"}.fa-houzz{--fa:"\f27c"}.fa-vimeo-v{--fa:"\f27d"}.fa-black-tie{--fa:"\f27e"}.fa-fonticons{--fa:"\f280"}.fa-reddit-alien{--fa:"\f281"}.fa-edge{--fa:"\f282"}.fa-codiepie{--fa:"\f284"}.fa-modx{--fa:"\f285"}.fa-fort-awesome{--fa:"\f286"}.fa-usb{--fa:"\f287"}.fa-product-hunt{--fa:"\f288"}.fa-mixcloud{--fa:"\f289"}.fa-scribd{--fa:"\f28a"}.fa-bluetooth{--fa:"\f293"}.fa-bluetooth-b{--fa:"\f294"}.fa-gitlab{--fa:"\f296"}.fa-wpbeginner{--fa:"\f297"}.fa-wpforms{--fa:"\f298"}.fa-envira{--fa:"\f299"}.fa-glide{--fa:"\f2a5"}.fa-glide-g{--fa:"\f2a6"}.fa-viadeo{--fa:"\f2a9"}.fa-square-viadeo,.fa-viadeo-square{--fa:"\f2aa"}.fa-snapchat,.fa-snapchat-ghost{--fa:"\f2ab"}.fa-snapchat-square,.fa-square-snapchat{--fa:"\f2ad"}.fa-pied-piper{--fa:"\f2ae"}.fa-first-order{--fa:"\f2b0"}.fa-yoast{--fa:"\f2b1"}.fa-themeisle{--fa:"\f2b2"}.fa-google-plus{--fa:"\f2b3"}.fa-font-awesome,.fa-font-awesome-flag,.fa-font-awesome-logo-full{--fa:"\f2b4"}.fa-linode{--fa:"\f2b8"}.fa-quora{--fa:"\f2c4"}.fa-free-code-camp{--fa:"\f2c5"}.fa-telegram,.fa-telegram-plane{--fa:"\f2c6"}.fa-bandcamp{--fa:"\f2d5"}.fa-grav{--fa:"\f2d6"}.fa-etsy{--fa:"\f2d7"}.fa-imdb{--fa:"\f2d8"}.fa-ravelry{--fa:"\f2d9"}.fa-sellcast{--fa:"\f2da"}.fa-superpowers{--fa:"\f2dd"}.fa-wpexplorer{--fa:"\f2de"}.fa-meetup{--fa:"\f2e0"}.fa-font-awesome-alt,.fa-square-font-awesome-stroke{--fa:"\f35c"}.fa-accessible-icon{--fa:"\f368"}.fa-accusoft{--fa:"\f369"}.fa-adversal{--fa:"\f36a"}.fa-affiliatetheme{--fa:"\f36b"}.fa-algolia{--fa:"\f36c"}.fa-amilia{--fa:"\f36d"}.fa-angrycreative{--fa:"\f36e"}.fa-app-store{--fa:"\f36f"}.fa-app-store-ios{--fa:"\f370"}.fa-apper{--fa:"\f371"}.fa-asymmetrik{--fa:"\f372"}.fa-audible{--fa:"\f373"}.fa-avianex{--fa:"\f374"}.fa-aws{--fa:"\f375"}.fa-bimobject{--fa:"\f378"}.fa-bitcoin{--fa:"\f379"}.fa-bity{--fa:"\f37a"}.fa-blackberry{--fa:"\f37b"}.fa-blogger{--fa:"\f37c"}.fa-blogger-b{--fa:"\f37d"}.fa-buromobelexperte{--fa:"\f37f"}.fa-centercode{--fa:"\f380"}.fa-cloudscale{--fa:"\f383"}.fa-cloudsmith{--fa:"\f384"}.fa-cloudversify{--fa:"\f385"}.fa-cpanel{--fa:"\f388"}.fa-css3-alt{--fa:"\f38b"}.fa-cuttlefish{--fa:"\f38c"}.fa-d-and-d{--fa:"\f38d"}.fa-deploydog{--fa:"\f38e"}.fa-deskpro{--fa:"\f38f"}.fa-digital-ocean{--fa:"\f391"}.fa-discord{--fa:"\f392"}.fa-discourse{--fa:"\f393"}.fa-dochub{--fa:"\f394"}.fa-docker{--fa:"\f395"}.fa-draft2digital{--fa:"\f396"}.fa-dribbble-square,.fa-square-dribbble{--fa:"\f397"}.fa-dyalog{--fa:"\f399"}.fa-earlybirds{--fa:"\f39a"}.fa-erlang{--fa:"\f39d"}.fa-facebook-f{--fa:"\f39e"}.fa-facebook-messenger{--fa:"\f39f"}.fa-firstdraft{--fa:"\f3a1"}.fa-fonticons-fi{--fa:"\f3a2"}.fa-fort-awesome-alt{--fa:"\f3a3"}.fa-freebsd{--fa:"\f3a4"}.fa-gitkraken{--fa:"\f3a6"}.fa-gofore{--fa:"\f3a7"}.fa-goodreads{--fa:"\f3a8"}.fa-goodreads-g{--fa:"\f3a9"}.fa-google-drive{--fa:"\f3aa"}.fa-google-play{--fa:"\f3ab"}.fa-gripfire{--fa:"\f3ac"}.fa-grunt{--fa:"\f3ad"}.fa-gulp{--fa:"\f3ae"}.fa-hacker-news-square,.fa-square-hacker-news{--fa:"\f3af"}.fa-hire-a-helper{--fa:"\f3b0"}.fa-hotjar{--fa:"\f3b1"}.fa-hubspot{--fa:"\f3b2"}.fa-itunes{--fa:"\f3b4"}.fa-itunes-note{--fa:"\f3b5"}.fa-jenkins{--fa:"\f3b6"}.fa-joget{--fa:"\f3b7"}.fa-js{--fa:"\f3b8"}.fa-js-square,.fa-square-js{--fa:"\f3b9"}.fa-keycdn{--fa:"\f3ba"}.fa-kickstarter,.fa-square-kickstarter{--fa:"\f3bb"}.fa-kickstarter-k{--fa:"\f3bc"}.fa-laravel{--fa:"\f3bd"}.fa-line{--fa:"\f3c0"}.fa-lyft{--fa:"\f3c3"}.fa-magento{--fa:"\f3c4"}.fa-medapps{--fa:"\f3c6"}.fa-medrt{--fa:"\f3c8"}.fa-microsoft{--fa:"\f3ca"}.fa-mix{--fa:"\f3cb"}.fa-mizuni{--fa:"\f3cc"}.fa-monero{--fa:"\f3d0"}.fa-napster{--fa:"\f3d2"}.fa-node-js{--fa:"\f3d3"}.fa-npm{--fa:"\f3d4"}.fa-ns8{--fa:"\f3d5"}.fa-nutritionix{--fa:"\f3d6"}.fa-page4{--fa:"\f3d7"}.fa-palfed{--fa:"\f3d8"}.fa-patreon{--fa:"\f3d9"}.fa-periscope{--fa:"\f3da"}.fa-phabricator{--fa:"\f3db"}.fa-phoenix-framework{--fa:"\f3dc"}.fa-playstation{--fa:"\f3df"}.fa-pushed{--fa:"\f3e1"}.fa-python{--fa:"\f3e2"}.fa-red-river{--fa:"\f3e3"}.fa-rendact,.fa-wpressr{--fa:"\f3e4"}.fa-replyd{--fa:"\f3e6"}.fa-resolving{--fa:"\f3e7"}.fa-rocketchat{--fa:"\f3e8"}.fa-rockrms{--fa:"\f3e9"}.fa-schlix{--fa:"\f3ea"}.fa-searchengin{--fa:"\f3eb"}.fa-servicestack{--fa:"\f3ec"}.fa-sistrix{--fa:"\f3ee"}.fa-speakap{--fa:"\f3f3"}.fa-staylinked{--fa:"\f3f5"}.fa-steam-symbol{--fa:"\f3f6"}.fa-sticker-mule{--fa:"\f3f7"}.fa-studiovinari{--fa:"\f3f8"}.fa-supple{--fa:"\f3f9"}.fa-uber{--fa:"\f402"}.fa-uikit{--fa:"\f403"}.fa-uniregistry{--fa:"\f404"}.fa-untappd{--fa:"\f405"}.fa-ussunnah{--fa:"\f407"}.fa-vaadin{--fa:"\f408"}.fa-viber{--fa:"\f409"}.fa-vimeo{--fa:"\f40a"}.fa-vnv{--fa:"\f40b"}.fa-square-whatsapp,.fa-whatsapp-square{--fa:"\f40c"}.fa-whmcs{--fa:"\f40d"}.fa-wordpress-simple{--fa:"\f411"}.fa-xbox{--fa:"\f412"}.fa-yandex{--fa:"\f413"}.fa-yandex-international{--fa:"\f414"}.fa-apple-pay{--fa:"\f415"}.fa-cc-apple-pay{--fa:"\f416"}.fa-fly{--fa:"\f417"}.fa-node{--fa:"\f419"}.fa-osi{--fa:"\f41a"}.fa-react{--fa:"\f41b"}.fa-autoprefixer{--fa:"\f41c"}.fa-less{--fa:"\f41d"}.fa-sass{--fa:"\f41e"}.fa-vuejs{--fa:"\f41f"}.fa-angular{--fa:"\f420"}.fa-aviato{--fa:"\f421"}.fa-ember{--fa:"\f423"}.fa-gitter{--fa:"\f426"}.fa-hooli{--fa:"\f427"}.fa-strava{--fa:"\f428"}.fa-stripe{--fa:"\f429"}.fa-stripe-s{--fa:"\f42a"}.fa-typo3{--fa:"\f42b"}.fa-amazon-pay{--fa:"\f42c"}.fa-cc-amazon-pay{--fa:"\f42d"}.fa-ethereum{--fa:"\f42e"}.fa-korvue{--fa:"\f42f"}.fa-elementor{--fa:"\f430"}.fa-square-youtube,.fa-youtube-square{--fa:"\f431"}.fa-flipboard{--fa:"\f44d"}.fa-hips{--fa:"\f452"}.fa-php{--fa:"\f457"}.fa-quinscape{--fa:"\f459"}.fa-readme{--fa:"\f4d5"}.fa-java{--fa:"\f4e4"}.fa-pied-piper-hat{--fa:"\f4e5"}.fa-creative-commons-by{--fa:"\f4e7"}.fa-creative-commons-nc{--fa:"\f4e8"}.fa-creative-commons-nc-eu{--fa:"\f4e9"}.fa-creative-commons-nc-jp{--fa:"\f4ea"}.fa-creative-commons-nd{--fa:"\f4eb"}.fa-creative-commons-pd{--fa:"\f4ec"}.fa-creative-commons-pd-alt{--fa:"\f4ed"}.fa-creative-commons-remix{--fa:"\f4ee"}.fa-creative-commons-sa{--fa:"\f4ef"}.fa-creative-commons-sampling{--fa:"\f4f0"}.fa-creative-commons-sampling-plus{--fa:"\f4f1"}.fa-creative-commons-share{--fa:"\f4f2"}.fa-creative-commons-zero{--fa:"\f4f3"}.fa-ebay{--fa:"\f4f4"}.fa-keybase{--fa:"\f4f5"}.fa-mastodon{--fa:"\f4f6"}.fa-r-project{--fa:"\f4f7"}.fa-researchgate{--fa:"\f4f8"}.fa-teamspeak{--fa:"\f4f9"}.fa-first-order-alt{--fa:"\f50a"}.fa-fulcrum{--fa:"\f50b"}.fa-galactic-republic{--fa:"\f50c"}.fa-galactic-senate{--fa:"\f50d"}.fa-jedi-order{--fa:"\f50e"}.fa-mandalorian{--fa:"\f50f"}.fa-old-republic{--fa:"\f510"}.fa-phoenix-squadron{--fa:"\f511"}.fa-sith{--fa:"\f512"}.fa-trade-federation{--fa:"\f513"}.fa-wolf-pack-battalion{--fa:"\f514"}.fa-hornbill{--fa:"\f592"}.fa-mailchimp{--fa:"\f59e"}.fa-megaport{--fa:"\f5a3"}.fa-nimblr{--fa:"\f5a8"}.fa-rev{--fa:"\f5b2"}.fa-shopware{--fa:"\f5b5"}.fa-squarespace{--fa:"\f5be"}.fa-themeco{--fa:"\f5c6"}.fa-weebly{--fa:"\f5cc"}.fa-wix{--fa:"\f5cf"}.fa-ello{--fa:"\f5f1"}.fa-hackerrank{--fa:"\f5f7"}.fa-kaggle{--fa:"\f5fa"}.fa-markdown{--fa:"\f60f"}.fa-neos{--fa:"\f612"}.fa-zhihu{--fa:"\f63f"}.fa-alipay{--fa:"\f642"}.fa-the-red-yeti{--fa:"\f69d"}.fa-critical-role{--fa:"\f6c9"}.fa-d-and-d-beyond{--fa:"\f6ca"}.fa-dev{--fa:"\f6cc"}.fa-fantasy-flight-games{--fa:"\f6dc"}.fa-wizards-of-the-coast{--fa:"\f730"}.fa-think-peaks{--fa:"\f731"}.fa-reacteurope{--fa:"\f75d"}.fa-artstation{--fa:"\f77a"}.fa-atlassian{--fa:"\f77b"}.fa-canadian-maple-leaf{--fa:"\f785"}.fa-centos{--fa:"\f789"}.fa-confluence{--fa:"\f78d"}.fa-dhl{--fa:"\f790"}.fa-diaspora{--fa:"\f791"}.fa-fedex{--fa:"\f797"}.fa-fedora{--fa:"\f798"}.fa-figma{--fa:"\f799"}.fa-intercom{--fa:"\f7af"}.fa-invision{--fa:"\f7b0"}.fa-jira{--fa:"\f7b1"}.fa-mendeley{--fa:"\f7b3"}.fa-raspberry-pi{--fa:"\f7bb"}.fa-redhat{--fa:"\f7bc"}.fa-sketch{--fa:"\f7c6"}.fa-sourcetree{--fa:"\f7d3"}.fa-suse{--fa:"\f7d6"}.fa-ubuntu{--fa:"\f7df"}.fa-ups{--fa:"\f7e0"}.fa-usps{--fa:"\f7e1"}.fa-yarn{--fa:"\f7e3"}.fa-airbnb{--fa:"\f834"}.fa-battle-net{--fa:"\f835"}.fa-bootstrap{--fa:"\f836"}.fa-buffer{--fa:"\f837"}.fa-chromecast{--fa:"\f838"}.fa-evernote{--fa:"\f839"}.fa-itch-io{--fa:"\f83a"}.fa-salesforce{--fa:"\f83b"}.fa-speaker-deck{--fa:"\f83c"}.fa-symfony{--fa:"\f83d"}.fa-waze{--fa:"\f83f"}.fa-yammer{--fa:"\f840"}.fa-git-alt{--fa:"\f841"}.fa-stackpath{--fa:"\f842"}.fa-cotton-bureau{--fa:"\f89e"}.fa-buy-n-large{--fa:"\f8a6"}.fa-mdb{--fa:"\f8ca"}.fa-orcid{--fa:"\f8d2"}.fa-swift{--fa:"\f8e1"}.fa-umbraco{--fa:"\f8e8"}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\fontawesome.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa-solid,
.fa-regular,
.fa-brands,
.fa-classic,
.fas,
.far,
.fab,
.fa {
  --_fa-family: var(--fa-family, var(--fa-style-family, "Font Awesome 7 Free"));
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
  display: var(--fa-display, inline-block);
  font-family: var(--_fa-family);
  font-feature-settings: normal;
  font-style: normal;
  font-synthesis: none;
  font-variant: normal;
  font-weight: var(--fa-style, 900);
  line-height: 1;
  text-align: center;
  text-rendering: auto;
  width: var(--fa-width, 1.25em);
}

:is(.fas,
.far,
.fab,
.fa-solid,
.fa-regular,
.fa-brands,
.fa-classic,
.fa)::before {
  content: var(--fa)/"";
}

@supports not (content: ""/"") {
  :is(.fas,
  .far,
  .fab,
  .fa-solid,
  .fa-regular,
  .fa-brands,
  .fa-classic,
  .fa)::before {
    content: var(--fa);
  }
}
.fa-1x {
  font-size: 1em;
}

.fa-2x {
  font-size: 2em;
}

.fa-3x {
  font-size: 3em;
}

.fa-4x {
  font-size: 4em;
}

.fa-5x {
  font-size: 5em;
}

.fa-6x {
  font-size: 6em;
}

.fa-7x {
  font-size: 7em;
}

.fa-8x {
  font-size: 8em;
}

.fa-9x {
  font-size: 9em;
}

.fa-10x {
  font-size: 10em;
}

.fa-2xs {
  font-size: calc(10 / 16 * 1em); /* converts a 10px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 10 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 10 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xs {
  font-size: calc(12 / 16 * 1em); /* converts a 12px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 12 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 12 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-sm {
  font-size: calc(14 / 16 * 1em); /* converts a 14px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 14 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 14 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-lg {
  font-size: calc(20 / 16 * 1em); /* converts a 20px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 20 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 20 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xl {
  font-size: calc(24 / 16 * 1em); /* converts a 24px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 24 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 24 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-2xl {
  font-size: calc(32 / 16 * 1em); /* converts a 32px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 32 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 32 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-width-auto {
  --fa-width: auto;
}

.fa-fw,
.fa-width-fixed {
  --fa-width: 1.25em;
}

.fa-ul {
  list-style-type: none;
  margin-inline-start: var(--fa-li-margin, 2.5em);
  padding-inline-start: 0;
}
.fa-ul > li {
  position: relative;
}

.fa-li {
  inset-inline-start: calc(-1 * var(--fa-li-width, 2em));
  position: absolute;
  text-align: center;
  width: var(--fa-li-width, 2em);
  line-height: inherit;
}

/* Heads Up: Bordered Icons will not be supported in the future!
  - This feature will be deprecated in the next major release of Font Awesome (v8)!
  - You may continue to use it in this version *v7), but it will not be supported in Font Awesome v8.
*/
/* Notes:
* --@{v.$css-prefix}-border-width = 1/16 by default (to render as ~1px based on a 16px default font-size)
* --@{v.$css-prefix}-border-padding =
  ** 3/16 for vertical padding (to give ~2px of vertical whitespace around an icon considering it's vertical alignment)
  ** 4/16 for horizontal padding (to give ~4px of horizontal whitespace around an icon)
*/
.fa-border {
  border-color: var(--fa-border-color, #eee);
  border-radius: var(--fa-border-radius, 0.1em);
  border-style: var(--fa-border-style, solid);
  border-width: var(--fa-border-width, 0.0625em);
  box-sizing: var(--fa-border-box-sizing, content-box);
  padding: var(--fa-border-padding, 0.1875em 0.25em);
}

.fa-pull-left,
.fa-pull-start {
  float: inline-start;
  margin-inline-end: var(--fa-pull-margin, 0.3em);
}

.fa-pull-right,
.fa-pull-end {
  float: inline-end;
  margin-inline-start: var(--fa-pull-margin, 0.3em);
}

.fa-beat {
  animation-name: fa-beat;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-bounce {
  animation-name: fa-bounce;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.28, 0.84, 0.42, 1));
}

.fa-fade {
  animation-name: fa-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-beat-fade {
  animation-name: fa-beat-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-flip {
  animation-name: fa-flip;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-shake {
  animation-name: fa-shake;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin {
  animation-name: fa-spin;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 2s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin-reverse {
  --fa-animation-direction: reverse;
}

.fa-pulse,
.fa-spin-pulse {
  animation-name: fa-spin;
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, steps(8));
}

@media (prefers-reduced-motion: reduce) {
  .fa-beat,
  .fa-bounce,
  .fa-fade,
  .fa-beat-fade,
  .fa-flip,
  .fa-pulse,
  .fa-shake,
  .fa-spin,
  .fa-spin-pulse {
    animation: none !important;
    transition: none !important;
  }
}
@keyframes fa-beat {
  0%, 90% {
    transform: scale(1);
  }
  45% {
    transform: scale(var(--fa-beat-scale, 1.25));
  }
}
@keyframes fa-bounce {
  0% {
    transform: scale(1, 1) translateY(0);
  }
  10% {
    transform: scale(var(--fa-bounce-start-scale-x, 1.1), var(--fa-bounce-start-scale-y, 0.9)) translateY(0);
  }
  30% {
    transform: scale(var(--fa-bounce-jump-scale-x, 0.9), var(--fa-bounce-jump-scale-y, 1.1)) translateY(var(--fa-bounce-height, -0.5em));
  }
  50% {
    transform: scale(var(--fa-bounce-land-scale-x, 1.05), var(--fa-bounce-land-scale-y, 0.95)) translateY(0);
  }
  57% {
    transform: scale(1, 1) translateY(var(--fa-bounce-rebound, -0.125em));
  }
  64% {
    transform: scale(1, 1) translateY(0);
  }
  100% {
    transform: scale(1, 1) translateY(0);
  }
}
@keyframes fa-fade {
  50% {
    opacity: var(--fa-fade-opacity, 0.4);
  }
}
@keyframes fa-beat-fade {
  0%, 100% {
    opacity: var(--fa-beat-fade-opacity, 0.4);
    transform: scale(1);
  }
  50% {
    opacity: 1;
    transform: scale(var(--fa-beat-fade-scale, 1.125));
  }
}
@keyframes fa-flip {
  50% {
    transform: rotate3d(var(--fa-flip-x, 0), var(--fa-flip-y, 1), var(--fa-flip-z, 0), var(--fa-flip-angle, -180deg));
  }
}
@keyframes fa-shake {
  0% {
    transform: rotate(-15deg);
  }
  4% {
    transform: rotate(15deg);
  }
  8%, 24% {
    transform: rotate(-18deg);
  }
  12%, 28% {
    transform: rotate(18deg);
  }
  16% {
    transform: rotate(-22deg);
  }
  20% {
    transform: rotate(22deg);
  }
  32% {
    transform: rotate(-12deg);
  }
  36% {
    transform: rotate(12deg);
  }
  40%, 100% {
    transform: rotate(0deg);
  }
}
@keyframes fa-spin {
  0% {
    transform: rotate(0deg);
  }
  100% {
    transform: rotate(360deg);
  }
}
.fa-rotate-90 {
  transform: rotate(90deg);
}

.fa-rotate-180 {
  transform: rotate(180deg);
}

.fa-rotate-270 {
  transform: rotate(270deg);
}

.fa-flip-horizontal {
  transform: scale(-1, 1);
}

.fa-flip-vertical {
  transform: scale(1, -1);
}

.fa-flip-both,
.fa-flip-horizontal.fa-flip-vertical {
  transform: scale(-1, -1);
}

.fa-rotate-by {
  transform: rotate(var(--fa-rotate-angle, 0));
}

.fa-stack {
  display: inline-block;
  height: 2em;
  line-height: 2em;
  position: relative;
  vertical-align: middle;
  width: 2.5em;
}

.fa-stack-1x,
.fa-stack-2x {
  --fa-width: 100%;
  inset: 0;
  position: absolute;
  text-align: center;
  width: var(--fa-width);
  z-index: var(--fa-stack-z-index, auto);
}

.fa-stack-1x {
  line-height: inherit;
}

.fa-stack-2x {
  font-size: 2em;
}

.fa-inverse {
  color: var(--fa-inverse, #fff);
}

/* Font Awesome uses the Unicode Private Use Area (PUA) to ensure screen
   readers do not read off random characters that represent icons */

.fa-0 {
  --fa: "\30 ";
}

.fa-1 {
  --fa: "\31 ";
}

.fa-2 {
  --fa: "\32 ";
}

.fa-3 {
  --fa: "\33 ";
}

.fa-4 {
  --fa: "\34 ";
}

.fa-5 {
  --fa: "\35 ";
}

.fa-6 {
  --fa: "\36 ";
}

.fa-7 {
  --fa: "\37 ";
}

.fa-8 {
  --fa: "\38 ";
}

.fa-9 {
  --fa: "\39 ";
}

.fa-exclamation {
  --fa: "\!";
}

.fa-hashtag {
  --fa: "\#";
}

.fa-dollar-sign {
  --fa: "\$";
}

.fa-dollar {
  --fa: "\$";
}

.fa-usd {
  --fa: "\$";
}

.fa-percent {
  --fa: "\%";
}

.fa-percentage {
  --fa: "\%";
}

.fa-asterisk {
  --fa: "\*";
}

.fa-plus {
  --fa: "\+";
}

.fa-add {
  --fa: "\+";
}

.fa-less-than {
  --fa: "\<";
}

.fa-equals {
  --fa: "\=";
}

.fa-greater-than {
  --fa: "\>";
}

.fa-question {
  --fa: "\?";
}

.fa-at {
  --fa: "\@";
}

.fa-a {
  --fa: "A";
}

.fa-b {
  --fa: "B";
}

.fa-c {
  --fa: "C";
}

.fa-d {
  --fa: "D";
}

.fa-e {
  --fa: "E";
}

.fa-f {
  --fa: "F";
}

.fa-g {
  --fa: "G";
}

.fa-h {
  --fa: "H";
}

.fa-i {
  --fa: "I";
}

.fa-j {
  --fa: "J";
}

.fa-k {
  --fa: "K";
}

.fa-l {
  --fa: "L";
}

.fa-m {
  --fa: "M";
}

.fa-n {
  --fa: "N";
}

.fa-o {
  --fa: "O";
}

.fa-p {
  --fa: "P";
}

.fa-q {
  --fa: "Q";
}

.fa-r {
  --fa: "R";
}

.fa-s {
  --fa: "S";
}

.fa-t {
  --fa: "T";
}

.fa-u {
  --fa: "U";
}

.fa-v {
  --fa: "V";
}

.fa-w {
  --fa: "W";
}

.fa-x {
  --fa: "X";
}

.fa-y {
  --fa: "Y";
}

.fa-z {
  --fa: "Z";
}

.fa-faucet {
  --fa: "\e005";
}

.fa-faucet-drip {
  --fa: "\e006";
}

.fa-house-chimney-window {
  --fa: "\e00d";
}

.fa-house-signal {
  --fa: "\e012";
}

.fa-temperature-arrow-down {
  --fa: "\e03f";
}

.fa-temperature-down {
  --fa: "\e03f";
}

.fa-temperature-arrow-up {
  --fa: "\e040";
}

.fa-temperature-up {
  --fa: "\e040";
}

.fa-trailer {
  --fa: "\e041";
}

.fa-bacteria {
  --fa: "\e059";
}

.fa-bacterium {
  --fa: "\e05a";
}

.fa-box-tissue {
  --fa: "\e05b";
}

.fa-hand-holding-medical {
  --fa: "\e05c";
}

.fa-hand-sparkles {
  --fa: "\e05d";
}

.fa-hands-bubbles {
  --fa: "\e05e";
}

.fa-hands-wash {
  --fa: "\e05e";
}

.fa-handshake-slash {
  --fa: "\e060";
}

.fa-handshake-alt-slash {
  --fa: "\e060";
}

.fa-handshake-simple-slash {
  --fa: "\e060";
}

.fa-head-side-cough {
  --fa: "\e061";
}

.fa-head-side-cough-slash {
  --fa: "\e062";
}

.fa-head-side-mask {
  --fa: "\e063";
}

.fa-head-side-virus {
  --fa: "\e064";
}

.fa-house-chimney-user {
  --fa: "\e065";
}

.fa-house-laptop {
  --fa: "\e066";
}

.fa-laptop-house {
  --fa: "\e066";
}

.fa-lungs-virus {
  --fa: "\e067";
}

.fa-people-arrows {
  --fa: "\e068";
}

.fa-people-arrows-left-right {
  --fa: "\e068";
}

.fa-plane-slash {
  --fa: "\e069";
}

.fa-pump-medical {
  --fa: "\e06a";
}

.fa-pump-soap {
  --fa: "\e06b";
}

.fa-shield-virus {
  --fa: "\e06c";
}

.fa-sink {
  --fa: "\e06d";
}

.fa-soap {
  --fa: "\e06e";
}

.fa-stopwatch-20 {
  --fa: "\e06f";
}

.fa-shop-slash {
  --fa: "\e070";
}

.fa-store-alt-slash {
  --fa: "\e070";
}

.fa-store-slash {
  --fa: "\e071";
}

.fa-toilet-paper-slash {
  --fa: "\e072";
}

.fa-users-slash {
  --fa: "\e073";
}

.fa-virus {
  --fa: "\e074";
}

.fa-virus-slash {
  --fa: "\e075";
}

.fa-viruses {
  --fa: "\e076";
}

.fa-vest {
  --fa: "\e085";
}

.fa-vest-patches {
  --fa: "\e086";
}

.fa-arrow-trend-down {
  --fa: "\e097";
}

.fa-arrow-trend-up {
  --fa: "\e098";
}

.fa-arrow-up-from-bracket {
  --fa: "\e09a";
}

.fa-austral-sign {
  --fa: "\e0a9";
}

.fa-baht-sign {
  --fa: "\e0ac";
}

.fa-bitcoin-sign {
  --fa: "\e0b4";
}

.fa-bolt-lightning {
  --fa: "\e0b7";
}

.fa-book-bookmark {
  --fa: "\e0bb";
}

.fa-camera-rotate {
  --fa: "\e0d8";
}

.fa-cedi-sign {
  --fa: "\e0df";
}

.fa-chart-column {
  --fa: "\e0e3";
}

.fa-chart-gantt {
  --fa: "\e0e4";
}

.fa-clapperboard {
  --fa: "\e131";
}

.fa-clover {
  --fa: "\e139";
}

.fa-code-compare {
  --fa: "\e13a";
}

.fa-code-fork {
  --fa: "\e13b";
}

.fa-code-pull-request {
  --fa: "\e13c";
}

.fa-colon-sign {
  --fa: "\e140";
}

.fa-cruzeiro-sign {
  --fa: "\e152";
}

.fa-display {
  --fa: "\e163";
}

.fa-dong-sign {
  --fa: "\e169";
}

.fa-elevator {
  --fa: "\e16d";
}

.fa-filter-circle-xmark {
  --fa: "\e17b";
}

.fa-florin-sign {
  --fa: "\e184";
}

.fa-folder-closed {
  --fa: "\e185";
}

.fa-franc-sign {
  --fa: "\e18f";
}

.fa-guarani-sign {
  --fa: "\e19a";
}

.fa-gun {
  --fa: "\e19b";
}

.fa-hands-clapping {
  --fa: "\e1a8";
}

.fa-house-user {
  --fa: "\e1b0";
}

.fa-home-user {
  --fa: "\e1b0";
}

.fa-indian-rupee-sign {
  --fa: "\e1bc";
}

.fa-indian-rupee {
  --fa: "\e1bc";
}

.fa-inr {
  --fa: "\e1bc";
}

.fa-kip-sign {
  --fa: "\e1c4";
}

.fa-lari-sign {
  --fa: "\e1c8";
}

.fa-litecoin-sign {
  --fa: "\e1d3";
}

.fa-manat-sign {
  --fa: "\e1d5";
}

.fa-mask-face {
  --fa: "\e1d7";
}

.fa-mill-sign {
  --fa: "\e1ed";
}

.fa-money-bills {
  --fa: "\e1f3";
}

.fa-naira-sign {
  --fa: "\e1f6";
}

.fa-notdef {
  --fa: "\e1fe";
}

.fa-panorama {
  --fa: "\e209";
}

.fa-peseta-sign {
  --fa: "\e221";
}

.fa-peso-sign {
  --fa: "\e222";
}

.fa-plane-up {
  --fa: "\e22d";
}

.fa-rupiah-sign {
  --fa: "\e23d";
}

.fa-stairs {
  --fa: "\e289";
}

.fa-timeline {
  --fa: "\e29c";
}

.fa-truck-front {
  --fa: "\e2b7";
}

.fa-turkish-lira-sign {
  --fa: "\e2bb";
}

.fa-try {
  --fa: "\e2bb";
}

.fa-turkish-lira {
  --fa: "\e2bb";
}

.fa-vault {
  --fa: "\e2c5";
}

.fa-wand-magic-sparkles {
  --fa: "\e2ca";
}

.fa-magic-wand-sparkles {
  --fa: "\e2ca";
}

.fa-wheat-awn {
  --fa: "\e2cd";
}

.fa-wheat-alt {
  --fa: "\e2cd";
}

.fa-wheelchair-move {
  --fa: "\e2ce";
}

.fa-wheelchair-alt {
  --fa: "\e2ce";
}

.fa-bangladeshi-taka-sign {
  --fa: "\e2e6";
}

.fa-bowl-rice {
  --fa: "\e2eb";
}

.fa-person-pregnant {
  --fa: "\e31e";
}

.fa-house-chimney {
  --fa: "\e3af";
}

.fa-home-lg {
  --fa: "\e3af";
}

.fa-house-crack {
  --fa: "\e3b1";
}

.fa-house-medical {
  --fa: "\e3b2";
}

.fa-cent-sign {
  --fa: "\e3f5";
}

.fa-plus-minus {
  --fa: "\e43c";
}

.fa-sailboat {
  --fa: "\e445";
}

.fa-section {
  --fa: "\e447";
}

.fa-shrimp {
  --fa: "\e448";
}

.fa-brazilian-real-sign {
  --fa: "\e46c";
}

.fa-chart-simple {
  --fa: "\e473";
}

.fa-diagram-next {
  --fa: "\e476";
}

.fa-diagram-predecessor {
  --fa: "\e477";
}

.fa-diagram-successor {
  --fa: "\e47a";
}

.fa-earth-oceania {
  --fa: "\e47b";
}

.fa-globe-oceania {
  --fa: "\e47b";
}

.fa-bug-slash {
  --fa: "\e490";
}

.fa-file-circle-plus {
  --fa: "\e494";
}

.fa-shop-lock {
  --fa: "\e4a5";
}

.fa-virus-covid {
  --fa: "\e4a8";
}

.fa-virus-covid-slash {
  --fa: "\e4a9";
}

.fa-anchor-circle-check {
  --fa: "\e4aa";
}

.fa-anchor-circle-exclamation {
  --fa: "\e4ab";
}

.fa-anchor-circle-xmark {
  --fa: "\e4ac";
}

.fa-anchor-lock {
  --fa: "\e4ad";
}

.fa-arrow-down-up-across-line {
  --fa: "\e4af";
}

.fa-arrow-down-up-lock {
  --fa: "\e4b0";
}

.fa-arrow-right-to-city {
  --fa: "\e4b3";
}

.fa-arrow-up-from-ground-water {
  --fa: "\e4b5";
}

.fa-arrow-up-from-water-pump {
  --fa: "\e4b6";
}

.fa-arrow-up-right-dots {
  --fa: "\e4b7";
}

.fa-arrows-down-to-line {
  --fa: "\e4b8";
}

.fa-arrows-down-to-people {
  --fa: "\e4b9";
}

.fa-arrows-left-right-to-line {
  --fa: "\e4ba";
}

.fa-arrows-spin {
  --fa: "\e4bb";
}

.fa-arrows-split-up-and-left {
  --fa: "\e4bc";
}

.fa-arrows-to-circle {
  --fa: "\e4bd";
}

.fa-arrows-to-dot {
  --fa: "\e4be";
}

.fa-arrows-to-eye {
  --fa: "\e4bf";
}

.fa-arrows-turn-right {
  --fa: "\e4c0";
}

.fa-arrows-turn-to-dots {
  --fa: "\e4c1";
}

.fa-arrows-up-to-line {
  --fa: "\e4c2";
}

.fa-bore-hole {
  --fa: "\e4c3";
}

.fa-bottle-droplet {
  --fa: "\e4c4";
}

.fa-bottle-water {
  --fa: "\e4c5";
}

.fa-bowl-food {
  --fa: "\e4c6";
}

.fa-boxes-packing {
  --fa: "\e4c7";
}

.fa-bridge {
  --fa: "\e4c8";
}

.fa-bridge-circle-check {
  --fa: "\e4c9";
}

.fa-bridge-circle-exclamation {
  --fa: "\e4ca";
}

.fa-bridge-circle-xmark {
  --fa: "\e4cb";
}

.fa-bridge-lock {
  --fa: "\e4cc";
}

.fa-bridge-water {
  --fa: "\e4ce";
}

.fa-bucket {
  --fa: "\e4cf";
}

.fa-bugs {
  --fa: "\e4d0";
}

.fa-building-circle-arrow-right {
  --fa: "\e4d1";
}

.fa-building-circle-check {
  --fa: "\e4d2";
}

.fa-building-circle-exclamation {
  --fa: "\e4d3";
}

.fa-building-circle-xmark {
  --fa: "\e4d4";
}

.fa-building-flag {
  --fa: "\e4d5";
}

.fa-building-lock {
  --fa: "\e4d6";
}

.fa-building-ngo {
  --fa: "\e4d7";
}

.fa-building-shield {
  --fa: "\e4d8";
}

.fa-building-un {
  --fa: "\e4d9";
}

.fa-building-user {
  --fa: "\e4da";
}

.fa-building-wheat {
  --fa: "\e4db";
}

.fa-burst {
  --fa: "\e4dc";
}

.fa-car-on {
  --fa: "\e4dd";
}

.fa-car-tunnel {
  --fa: "\e4de";
}

.fa-child-combatant {
  --fa: "\e4e0";
}

.fa-child-rifle {
  --fa: "\e4e0";
}

.fa-children {
  --fa: "\e4e1";
}

.fa-circle-nodes {
  --fa: "\e4e2";
}

.fa-clipboard-question {
  --fa: "\e4e3";
}

.fa-cloud-showers-water {
  --fa: "\e4e4";
}

.fa-computer {
  --fa: "\e4e5";
}

.fa-cubes-stacked {
  --fa: "\e4e6";
}

.fa-envelope-circle-check {
  --fa: "\e4e8";
}

.fa-explosion {
  --fa: "\e4e9";
}

.fa-ferry {
  --fa: "\e4ea";
}

.fa-file-circle-exclamation {
  --fa: "\e4eb";
}

.fa-file-circle-minus {
  --fa: "\e4ed";
}

.fa-file-circle-question {
  --fa: "\e4ef";
}

.fa-file-shield {
  --fa: "\e4f0";
}

.fa-fire-burner {
  --fa: "\e4f1";
}

.fa-fish-fins {
  --fa: "\e4f2";
}

.fa-flask-vial {
  --fa: "\e4f3";
}

.fa-glass-water {
  --fa: "\e4f4";
}

.fa-glass-water-droplet {
  --fa: "\e4f5";
}

.fa-group-arrows-rotate {
  --fa: "\e4f6";
}

.fa-hand-holding-hand {
  --fa: "\e4f7";
}

.fa-handcuffs {
  --fa: "\e4f8";
}

.fa-hands-bound {
  --fa: "\e4f9";
}

.fa-hands-holding-child {
  --fa: "\e4fa";
}

.fa-hands-holding-circle {
  --fa: "\e4fb";
}

.fa-heart-circle-bolt {
  --fa: "\e4fc";
}

.fa-heart-circle-check {
  --fa: "\e4fd";
}

.fa-heart-circle-exclamation {
  --fa: "\e4fe";
}

.fa-heart-circle-minus {
  --fa: "\e4ff";
}

.fa-heart-circle-plus {
  --fa: "\e500";
}

.fa-heart-circle-xmark {
  --fa: "\e501";
}

.fa-helicopter-symbol {
  --fa: "\e502";
}

.fa-helmet-un {
  --fa: "\e503";
}

.fa-hill-avalanche {
  --fa: "\e507";
}

.fa-hill-rockslide {
  --fa: "\e508";
}

.fa-house-circle-check {
  --fa: "\e509";
}

.fa-house-circle-exclamation {
  --fa: "\e50a";
}

.fa-house-circle-xmark {
  --fa: "\e50b";
}

.fa-house-fire {
  --fa: "\e50c";
}

.fa-house-flag {
  --fa: "\e50d";
}

.fa-house-flood-water {
  --fa: "\e50e";
}

.fa-house-flood-water-circle-arrow-right {
  --fa: "\e50f";
}

.fa-house-lock {
  --fa: "\e510";
}

.fa-house-medical-circle-check {
  --fa: "\e511";
}

.fa-house-medical-circle-exclamation {
  --fa: "\e512";
}

.fa-house-medical-circle-xmark {
  --fa: "\e513";
}

.fa-house-medical-flag {
  --fa: "\e514";
}

.fa-house-tsunami {
  --fa: "\e515";
}

.fa-jar {
  --fa: "\e516";
}

.fa-jar-wheat {
  --fa: "\e517";
}

.fa-jet-fighter-up {
  --fa: "\e518";
}

.fa-jug-detergent {
  --fa: "\e519";
}

.fa-kitchen-set {
  --fa: "\e51a";
}

.fa-land-mine-on {
  --fa: "\e51b";
}

.fa-landmark-flag {
  --fa: "\e51c";
}

.fa-laptop-file {
  --fa: "\e51d";
}

.fa-lines-leaning {
  --fa: "\e51e";
}

.fa-location-pin-lock {
  --fa: "\e51f";
}

.fa-locust {
  --fa: "\e520";
}

.fa-magnifying-glass-arrow-right {
  --fa: "\e521";
}

.fa-magnifying-glass-chart {
  --fa: "\e522";
}

.fa-mars-and-venus-burst {
  --fa: "\e523";
}

.fa-mask-ventilator {
  --fa: "\e524";
}

.fa-mattress-pillow {
  --fa: "\e525";
}

.fa-mobile-retro {
  --fa: "\e527";
}

.fa-money-bill-transfer {
  --fa: "\e528";
}

.fa-money-bill-trend-up {
  --fa: "\e529";
}

.fa-money-bill-wheat {
  --fa: "\e52a";
}

.fa-mosquito {
  --fa: "\e52b";
}

.fa-mosquito-net {
  --fa: "\e52c";
}

.fa-mound {
  --fa: "\e52d";
}

.fa-mountain-city {
  --fa: "\e52e";
}

.fa-mountain-sun {
  --fa: "\e52f";
}

.fa-oil-well {
  --fa: "\e532";
}

.fa-people-group {
  --fa: "\e533";
}

.fa-people-line {
  --fa: "\e534";
}

.fa-people-pulling {
  --fa: "\e535";
}

.fa-people-robbery {
  --fa: "\e536";
}

.fa-people-roof {
  --fa: "\e537";
}

.fa-person-arrow-down-to-line {
  --fa: "\e538";
}

.fa-person-arrow-up-from-line {
  --fa: "\e539";
}

.fa-person-breastfeeding {
  --fa: "\e53a";
}

.fa-person-burst {
  --fa: "\e53b";
}

.fa-person-cane {
  --fa: "\e53c";
}

.fa-person-chalkboard {
  --fa: "\e53d";
}

.fa-person-circle-check {
  --fa: "\e53e";
}

.fa-person-circle-exclamation {
  --fa: "\e53f";
}

.fa-person-circle-minus {
  --fa: "\e540";
}

.fa-person-circle-plus {
  --fa: "\e541";
}

.fa-person-circle-question {
  --fa: "\e542";
}

.fa-person-circle-xmark {
  --fa: "\e543";
}

.fa-person-dress-burst {
  --fa: "\e544";
}

.fa-person-drowning {
  --fa: "\e545";
}

.fa-person-falling {
  --fa: "\e546";
}

.fa-person-falling-burst {
  --fa: "\e547";
}

.fa-person-half-dress {
  --fa: "\e548";
}

.fa-person-harassing {
  --fa: "\e549";
}

.fa-person-military-pointing {
  --fa: "\e54a";
}

.fa-person-military-rifle {
  --fa: "\e54b";
}

.fa-person-military-to-person {
  --fa: "\e54c";
}

.fa-person-rays {
  --fa: "\e54d";
}

.fa-person-rifle {
  --fa: "\e54e";
}

.fa-person-shelter {
  --fa: "\e54f";
}

.fa-person-walking-arrow-loop-left {
  --fa: "\e551";
}

.fa-person-walking-arrow-right {
  --fa: "\e552";
}

.fa-person-walking-dashed-line-arrow-right {
  --fa: "\e553";
}

.fa-person-walking-luggage {
  --fa: "\e554";
}

.fa-plane-circle-check {
  --fa: "\e555";
}

.fa-plane-circle-exclamation {
  --fa: "\e556";
}

.fa-plane-circle-xmark {
  --fa: "\e557";
}

.fa-plane-lock {
  --fa: "\e558";
}

.fa-plate-wheat {
  --fa: "\e55a";
}

.fa-plug-circle-bolt {
  --fa: "\e55b";
}

.fa-plug-circle-check {
  --fa: "\e55c";
}

.fa-plug-circle-exclamation {
  --fa: "\e55d";
}

.fa-plug-circle-minus {
  --fa: "\e55e";
}

.fa-plug-circle-plus {
  --fa: "\e55f";
}

.fa-plug-circle-xmark {
  --fa: "\e560";
}

.fa-ranking-star {
  --fa: "\e561";
}

.fa-road-barrier {
  --fa: "\e562";
}

.fa-road-bridge {
  --fa: "\e563";
}

.fa-road-circle-check {
  --fa: "\e564";
}

.fa-road-circle-exclamation {
  --fa: "\e565";
}

.fa-road-circle-xmark {
  --fa: "\e566";
}

.fa-road-lock {
  --fa: "\e567";
}

.fa-road-spikes {
  --fa: "\e568";
}

.fa-rug {
  --fa: "\e569";
}

.fa-sack-xmark {
  --fa: "\e56a";
}

.fa-school-circle-check {
  --fa: "\e56b";
}

.fa-school-circle-exclamation {
  --fa: "\e56c";
}

.fa-school-circle-xmark {
  --fa: "\e56d";
}

.fa-school-flag {
  --fa: "\e56e";
}

.fa-school-lock {
  --fa: "\e56f";
}

.fa-sheet-plastic {
  --fa: "\e571";
}

.fa-shield-cat {
  --fa: "\e572";
}

.fa-shield-dog {
  --fa: "\e573";
}

.fa-shield-heart {
  --fa: "\e574";
}

.fa-square-nfi {
  --fa: "\e576";
}

.fa-square-person-confined {
  --fa: "\e577";
}

.fa-square-virus {
  --fa: "\e578";
}

.fa-staff-snake {
  --fa: "\e579";
}

.fa-rod-asclepius {
  --fa: "\e579";
}

.fa-rod-snake {
  --fa: "\e579";
}

.fa-staff-aesculapius {
  --fa: "\e579";
}

.fa-sun-plant-wilt {
  --fa: "\e57a";
}

.fa-tarp {
  --fa: "\e57b";
}

.fa-tarp-droplet {
  --fa: "\e57c";
}

.fa-tent {
  --fa: "\e57d";
}

.fa-tent-arrow-down-to-line {
  --fa: "\e57e";
}

.fa-tent-arrow-left-right {
  --fa: "\e57f";
}

.fa-tent-arrow-turn-left {
  --fa: "\e580";
}

.fa-tent-arrows-down {
  --fa: "\e581";
}

.fa-tents {
  --fa: "\e582";
}

.fa-toilet-portable {
  --fa: "\e583";
}

.fa-toilets-portable {
  --fa: "\e584";
}

.fa-tower-cell {
  --fa: "\e585";
}

.fa-tower-observation {
  --fa: "\e586";
}

.fa-tree-city {
  --fa: "\e587";
}

.fa-trowel {
  --fa: "\e589";
}

.fa-trowel-bricks {
  --fa: "\e58a";
}

.fa-truck-arrow-right {
  --fa: "\e58b";
}

.fa-truck-droplet {
  --fa: "\e58c";
}

.fa-truck-field {
  --fa: "\e58d";
}

.fa-truck-field-un {
  --fa: "\e58e";
}

.fa-truck-plane {
  --fa: "\e58f";
}

.fa-users-between-lines {
  --fa: "\e591";
}

.fa-users-line {
  --fa: "\e592";
}

.fa-users-rays {
  --fa: "\e593";
}

.fa-users-rectangle {
  --fa: "\e594";
}

.fa-users-viewfinder {
  --fa: "\e595";
}

.fa-vial-circle-check {
  --fa: "\e596";
}

.fa-vial-virus {
  --fa: "\e597";
}

.fa-wheat-awn-circle-exclamation {
  --fa: "\e598";
}

.fa-worm {
  --fa: "\e599";
}

.fa-xmarks-lines {
  --fa: "\e59a";
}

.fa-child-dress {
  --fa: "\e59c";
}

.fa-child-reaching {
  --fa: "\e59d";
}

.fa-file-circle-check {
  --fa: "\e5a0";
}

.fa-file-circle-xmark {
  --fa: "\e5a1";
}

.fa-person-through-window {
  --fa: "\e5a9";
}

.fa-plant-wilt {
  --fa: "\e5aa";
}

.fa-stapler {
  --fa: "\e5af";
}

.fa-train-tram {
  --fa: "\e5b4";
}

.fa-table-cells-column-lock {
  --fa: "\e678";
}

.fa-table-cells-row-lock {
  --fa: "\e67a";
}

.fa-web-awesome {
  --fa: "\e682";
}

.fa-thumbtack-slash {
  --fa: "\e68f";
}

.fa-thumb-tack-slash {
  --fa: "\e68f";
}

.fa-table-cells-row-unlock {
  --fa: "\e691";
}

.fa-chart-diagram {
  --fa: "\e695";
}

.fa-comment-nodes {
  --fa: "\e696";
}

.fa-file-fragment {
  --fa: "\e697";
}

.fa-file-half-dashed {
  --fa: "\e698";
}

.fa-hexagon-nodes {
  --fa: "\e699";
}

.fa-hexagon-nodes-bolt {
  --fa: "\e69a";
}

.fa-square-binary {
  --fa: "\e69b";
}

.fa-pentagon {
  --fa: "\e790";
}

.fa-non-binary {
  --fa: "\e807";
}

.fa-spiral {
  --fa: "\e80a";
}

.fa-mobile-vibrate {
  --fa: "\e816";
}

.fa-single-quote-left {
  --fa: "\e81b";
}

.fa-single-quote-right {
  --fa: "\e81c";
}

.fa-bus-side {
  --fa: "\e81d";
}

.fa-septagon {
  --fa: "\e820";
}

.fa-heptagon {
  --fa: "\e820";
}

.fa-martini-glass-empty {
  --fa: "\f000";
}

.fa-glass-martini {
  --fa: "\f000";
}

.fa-music {
  --fa: "\f001";
}

.fa-magnifying-glass {
  --fa: "\f002";
}

.fa-search {
  --fa: "\f002";
}

.fa-heart {
  --fa: "\f004";
}

.fa-star {
  --fa: "\f005";
}

.fa-user {
  --fa: "\f007";
}

.fa-user-alt {
  --fa: "\f007";
}

.fa-user-large {
  --fa: "\f007";
}

.fa-film {
  --fa: "\f008";
}

.fa-film-alt {
  --fa: "\f008";
}

.fa-film-simple {
  --fa: "\f008";
}

.fa-table-cells-large {
  --fa: "\f009";
}

.fa-th-large {
  --fa: "\f009";
}

.fa-table-cells {
  --fa: "\f00a";
}

.fa-th {
  --fa: "\f00a";
}

.fa-table-list {
  --fa: "\f00b";
}

.fa-th-list {
  --fa: "\f00b";
}

.fa-check {
  --fa: "\f00c";
}

.fa-xmark {
  --fa: "\f00d";
}

.fa-close {
  --fa: "\f00d";
}

.fa-multiply {
  --fa: "\f00d";
}

.fa-remove {
  --fa: "\f00d";
}

.fa-times {
  --fa: "\f00d";
}

.fa-magnifying-glass-plus {
  --fa: "\f00e";
}

.fa-search-plus {
  --fa: "\f00e";
}

.fa-magnifying-glass-minus {
  --fa: "\f010";
}

.fa-search-minus {
  --fa: "\f010";
}

.fa-power-off {
  --fa: "\f011";
}

.fa-signal {
  --fa: "\f012";
}

.fa-signal-5 {
  --fa: "\f012";
}

.fa-signal-perfect {
  --fa: "\f012";
}

.fa-gear {
  --fa: "\f013";
}

.fa-cog {
  --fa: "\f013";
}

.fa-house {
  --fa: "\f015";
}

.fa-home {
  --fa: "\f015";
}

.fa-home-alt {
  --fa: "\f015";
}

.fa-home-lg-alt {
  --fa: "\f015";
}

.fa-clock {
  --fa: "\f017";
}

.fa-clock-four {
  --fa: "\f017";
}

.fa-road {
  --fa: "\f018";
}

.fa-download {
  --fa: "\f019";
}

.fa-inbox {
  --fa: "\f01c";
}

.fa-arrow-rotate-right {
  --fa: "\f01e";
}

.fa-arrow-right-rotate {
  --fa: "\f01e";
}

.fa-arrow-rotate-forward {
  --fa: "\f01e";
}

.fa-redo {
  --fa: "\f01e";
}

.fa-arrows-rotate {
  --fa: "\f021";
}

.fa-refresh {
  --fa: "\f021";
}

.fa-sync {
  --fa: "\f021";
}

.fa-rectangle-list {
  --fa: "\f022";
}

.fa-list-alt {
  --fa: "\f022";
}

.fa-lock {
  --fa: "\f023";
}

.fa-flag {
  --fa: "\f024";
}

.fa-headphones {
  --fa: "\f025";
}

.fa-headphones-alt {
  --fa: "\f025";
}

.fa-headphones-simple {
  --fa: "\f025";
}

.fa-volume-off {
  --fa: "\f026";
}

.fa-volume-low {
  --fa: "\f027";
}

.fa-volume-down {
  --fa: "\f027";
}

.fa-volume-high {
  --fa: "\f028";
}

.fa-volume-up {
  --fa: "\f028";
}

.fa-qrcode {
  --fa: "\f029";
}

.fa-barcode {
  --fa: "\f02a";
}

.fa-tag {
  --fa: "\f02b";
}

.fa-tags {
  --fa: "\f02c";
}

.fa-book {
  --fa: "\f02d";
}

.fa-bookmark {
  --fa: "\f02e";
}

.fa-print {
  --fa: "\f02f";
}

.fa-camera {
  --fa: "\f030";
}

.fa-camera-alt {
  --fa: "\f030";
}

.fa-font {
  --fa: "\f031";
}

.fa-bold {
  --fa: "\f032";
}

.fa-italic {
  --fa: "\f033";
}

.fa-text-height {
  --fa: "\f034";
}

.fa-text-width {
  --fa: "\f035";
}

.fa-align-left {
  --fa: "\f036";
}

.fa-align-center {
  --fa: "\f037";
}

.fa-align-right {
  --fa: "\f038";
}

.fa-align-justify {
  --fa: "\f039";
}

.fa-list {
  --fa: "\f03a";
}

.fa-list-squares {
  --fa: "\f03a";
}

.fa-outdent {
  --fa: "\f03b";
}

.fa-dedent {
  --fa: "\f03b";
}

.fa-indent {
  --fa: "\f03c";
}

.fa-video {
  --fa: "\f03d";
}

.fa-video-camera {
  --fa: "\f03d";
}

.fa-image {
  --fa: "\f03e";
}

.fa-location-pin {
  --fa: "\f041";
}

.fa-map-marker {
  --fa: "\f041";
}

.fa-circle-half-stroke {
  --fa: "\f042";
}

.fa-adjust {
  --fa: "\f042";
}

.fa-droplet {
  --fa: "\f043";
}

.fa-tint {
  --fa: "\f043";
}

.fa-pen-to-square {
  --fa: "\f044";
}

.fa-edit {
  --fa: "\f044";
}

.fa-arrows-up-down-left-right {
  --fa: "\f047";
}

.fa-arrows {
  --fa: "\f047";
}

.fa-backward-step {
  --fa: "\f048";
}

.fa-step-backward {
  --fa: "\f048";
}

.fa-backward-fast {
  --fa: "\f049";
}

.fa-fast-backward {
  --fa: "\f049";
}

.fa-backward {
  --fa: "\f04a";
}

.fa-play {
  --fa: "\f04b";
}

.fa-pause {
  --fa: "\f04c";
}

.fa-stop {
  --fa: "\f04d";
}

.fa-forward {
  --fa: "\f04e";
}

.fa-forward-fast {
  --fa: "\f050";
}

.fa-fast-forward {
  --fa: "\f050";
}

.fa-forward-step {
  --fa: "\f051";
}

.fa-step-forward {
  --fa: "\f051";
}

.fa-eject {
  --fa: "\f052";
}

.fa-chevron-left {
  --fa: "\f053";
}

.fa-chevron-right {
  --fa: "\f054";
}

.fa-circle-plus {
  --fa: "\f055";
}

.fa-plus-circle {
  --fa: "\f055";
}

.fa-circle-minus {
  --fa: "\f056";
}

.fa-minus-circle {
  --fa: "\f056";
}

.fa-circle-xmark {
  --fa: "\f057";
}

.fa-times-circle {
  --fa: "\f057";
}

.fa-xmark-circle {
  --fa: "\f057";
}

.fa-circle-check {
  --fa: "\f058";
}

.fa-check-circle {
  --fa: "\f058";
}

.fa-circle-question {
  --fa: "\f059";
}

.fa-question-circle {
  --fa: "\f059";
}

.fa-circle-info {
  --fa: "\f05a";
}

.fa-info-circle {
  --fa: "\f05a";
}

.fa-crosshairs {
  --fa: "\f05b";
}

.fa-ban {
  --fa: "\f05e";
}

.fa-cancel {
  --fa: "\f05e";
}

.fa-arrow-left {
  --fa: "\f060";
}

.fa-arrow-right {
  --fa: "\f061";
}

.fa-arrow-up {
  --fa: "\f062";
}

.fa-arrow-down {
  --fa: "\f063";
}

.fa-share {
  --fa: "\f064";
}

.fa-mail-forward {
  --fa: "\f064";
}

.fa-expand {
  --fa: "\f065";
}

.fa-compress {
  --fa: "\f066";
}

.fa-minus {
  --fa: "\f068";
}

.fa-subtract {
  --fa: "\f068";
}

.fa-circle-exclamation {
  --fa: "\f06a";
}

.fa-exclamation-circle {
  --fa: "\f06a";
}

.fa-gift {
  --fa: "\f06b";
}

.fa-leaf {
  --fa: "\f06c";
}

.fa-fire {
  --fa: "\f06d";
}

.fa-eye {
  --fa: "\f06e";
}

.fa-eye-slash {
  --fa: "\f070";
}

.fa-triangle-exclamation {
  --fa: "\f071";
}

.fa-exclamation-triangle {
  --fa: "\f071";
}

.fa-warning {
  --fa: "\f071";
}

.fa-plane {
  --fa: "\f072";
}

.fa-calendar-days {
  --fa: "\f073";
}

.fa-calendar-alt {
  --fa: "\f073";
}

.fa-shuffle {
  --fa: "\f074";
}

.fa-random {
  --fa: "\f074";
}

.fa-comment {
  --fa: "\f075";
}

.fa-magnet {
  --fa: "\f076";
}

.fa-chevron-up {
  --fa: "\f077";
}

.fa-chevron-down {
  --fa: "\f078";
}

.fa-retweet {
  --fa: "\f079";
}

.fa-cart-shopping {
  --fa: "\f07a";
}

.fa-shopping-cart {
  --fa: "\f07a";
}

.fa-folder {
  --fa: "\f07b";
}

.fa-folder-blank {
  --fa: "\f07b";
}

.fa-folder-open {
  --fa: "\f07c";
}

.fa-arrows-up-down {
  --fa: "\f07d";
}

.fa-arrows-v {
  --fa: "\f07d";
}

.fa-arrows-left-right {
  --fa: "\f07e";
}

.fa-arrows-h {
  --fa: "\f07e";
}

.fa-chart-bar {
  --fa: "\f080";
}

.fa-bar-chart {
  --fa: "\f080";
}

.fa-camera-retro {
  --fa: "\f083";
}

.fa-key {
  --fa: "\f084";
}

.fa-gears {
  --fa: "\f085";
}

.fa-cogs {
  --fa: "\f085";
}

.fa-comments {
  --fa: "\f086";
}

.fa-star-half {
  --fa: "\f089";
}

.fa-arrow-right-from-bracket {
  --fa: "\f08b";
}

.fa-sign-out {
  --fa: "\f08b";
}

.fa-thumbtack {
  --fa: "\f08d";
}

.fa-thumb-tack {
  --fa: "\f08d";
}

.fa-arrow-up-right-from-square {
  --fa: "\f08e";
}

.fa-external-link {
  --fa: "\f08e";
}

.fa-arrow-right-to-bracket {
  --fa: "\f090";
}

.fa-sign-in {
  --fa: "\f090";
}

.fa-trophy {
  --fa: "\f091";
}

.fa-upload {
  --fa: "\f093";
}

.fa-lemon {
  --fa: "\f094";
}

.fa-phone {
  --fa: "\f095";
}

.fa-square-phone {
  --fa: "\f098";
}

.fa-phone-square {
  --fa: "\f098";
}

.fa-unlock {
  --fa: "\f09c";
}

.fa-credit-card {
  --fa: "\f09d";
}

.fa-credit-card-alt {
  --fa: "\f09d";
}

.fa-rss {
  --fa: "\f09e";
}

.fa-feed {
  --fa: "\f09e";
}

.fa-hard-drive {
  --fa: "\f0a0";
}

.fa-hdd {
  --fa: "\f0a0";
}

.fa-bullhorn {
  --fa: "\f0a1";
}

.fa-certificate {
  --fa: "\f0a3";
}

.fa-hand-point-right {
  --fa: "\f0a4";
}

.fa-hand-point-left {
  --fa: "\f0a5";
}

.fa-hand-point-up {
  --fa: "\f0a6";
}

.fa-hand-point-down {
  --fa: "\f0a7";
}

.fa-circle-arrow-left {
  --fa: "\f0a8";
}

.fa-arrow-circle-left {
  --fa: "\f0a8";
}

.fa-circle-arrow-right {
  --fa: "\f0a9";
}

.fa-arrow-circle-right {
  --fa: "\f0a9";
}

.fa-circle-arrow-up {
  --fa: "\f0aa";
}

.fa-arrow-circle-up {
  --fa: "\f0aa";
}

.fa-circle-arrow-down {
  --fa: "\f0ab";
}

.fa-arrow-circle-down {
  --fa: "\f0ab";
}

.fa-globe {
  --fa: "\f0ac";
}

.fa-wrench {
  --fa: "\f0ad";
}

.fa-list-check {
  --fa: "\f0ae";
}

.fa-tasks {
  --fa: "\f0ae";
}

.fa-filter {
  --fa: "\f0b0";
}

.fa-briefcase {
  --fa: "\f0b1";
}

.fa-up-down-left-right {
  --fa: "\f0b2";
}

.fa-arrows-alt {
  --fa: "\f0b2";
}

.fa-users {
  --fa: "\f0c0";
}

.fa-link {
  --fa: "\f0c1";
}

.fa-chain {
  --fa: "\f0c1";
}

.fa-cloud {
  --fa: "\f0c2";
}

.fa-flask {
  --fa: "\f0c3";
}

.fa-scissors {
  --fa: "\f0c4";
}

.fa-cut {
  --fa: "\f0c4";
}

.fa-copy {
  --fa: "\f0c5";
}

.fa-paperclip {
  --fa: "\f0c6";
}

.fa-floppy-disk {
  --fa: "\f0c7";
}

.fa-save {
  --fa: "\f0c7";
}

.fa-square {
  --fa: "\f0c8";
}

.fa-bars {
  --fa: "\f0c9";
}

.fa-navicon {
  --fa: "\f0c9";
}

.fa-list-ul {
  --fa: "\f0ca";
}

.fa-list-dots {
  --fa: "\f0ca";
}

.fa-list-ol {
  --fa: "\f0cb";
}

.fa-list-1-2 {
  --fa: "\f0cb";
}

.fa-list-numeric {
  --fa: "\f0cb";
}

.fa-strikethrough {
  --fa: "\f0cc";
}

.fa-underline {
  --fa: "\f0cd";
}

.fa-table {
  --fa: "\f0ce";
}

.fa-wand-magic {
  --fa: "\f0d0";
}

.fa-magic {
  --fa: "\f0d0";
}

.fa-truck {
  --fa: "\f0d1";
}

.fa-money-bill {
  --fa: "\f0d6";
}

.fa-caret-down {
  --fa: "\f0d7";
}

.fa-caret-up {
  --fa: "\f0d8";
}

.fa-caret-left {
  --fa: "\f0d9";
}

.fa-caret-right {
  --fa: "\f0da";
}

.fa-table-columns {
  --fa: "\f0db";
}

.fa-columns {
  --fa: "\f0db";
}

.fa-sort {
  --fa: "\f0dc";
}

.fa-unsorted {
  --fa: "\f0dc";
}

.fa-sort-down {
  --fa: "\f0dd";
}

.fa-sort-desc {
  --fa: "\f0dd";
}

.fa-sort-up {
  --fa: "\f0de";
}

.fa-sort-asc {
  --fa: "\f0de";
}

.fa-envelope {
  --fa: "\f0e0";
}

.fa-arrow-rotate-left {
  --fa: "\f0e2";
}

.fa-arrow-left-rotate {
  --fa: "\f0e2";
}

.fa-arrow-rotate-back {
  --fa: "\f0e2";
}

.fa-arrow-rotate-backward {
  --fa: "\f0e2";
}

.fa-undo {
  --fa: "\f0e2";
}

.fa-gavel {
  --fa: "\f0e3";
}

.fa-legal {
  --fa: "\f0e3";
}

.fa-bolt {
  --fa: "\f0e7";
}

.fa-zap {
  --fa: "\f0e7";
}

.fa-sitemap {
  --fa: "\f0e8";
}

.fa-umbrella {
  --fa: "\f0e9";
}

.fa-paste {
  --fa: "\f0ea";
}

.fa-file-clipboard {
  --fa: "\f0ea";
}

.fa-lightbulb {
  --fa: "\f0eb";
}

.fa-arrow-right-arrow-left {
  --fa: "\f0ec";
}

.fa-exchange {
  --fa: "\f0ec";
}

.fa-cloud-arrow-down {
  --fa: "\f0ed";
}

.fa-cloud-download {
  --fa: "\f0ed";
}

.fa-cloud-download-alt {
  --fa: "\f0ed";
}

.fa-cloud-arrow-up {
  --fa: "\f0ee";
}

.fa-cloud-upload {
  --fa: "\f0ee";
}

.fa-cloud-upload-alt {
  --fa: "\f0ee";
}

.fa-user-doctor {
  --fa: "\f0f0";
}

.fa-user-md {
  --fa: "\f0f0";
}

.fa-stethoscope {
  --fa: "\f0f1";
}

.fa-suitcase {
  --fa: "\f0f2";
}

.fa-bell {
  --fa: "\f0f3";
}

.fa-mug-saucer {
  --fa: "\f0f4";
}

.fa-coffee {
  --fa: "\f0f4";
}

.fa-hospital {
  --fa: "\f0f8";
}

.fa-hospital-alt {
  --fa: "\f0f8";
}

.fa-hospital-wide {
  --fa: "\f0f8";
}

.fa-truck-medical {
  --fa: "\f0f9";
}

.fa-ambulance {
  --fa: "\f0f9";
}

.fa-suitcase-medical {
  --fa: "\f0fa";
}

.fa-medkit {
  --fa: "\f0fa";
}

.fa-jet-fighter {
  --fa: "\f0fb";
}

.fa-fighter-jet {
  --fa: "\f0fb";
}

.fa-beer-mug-empty {
  --fa: "\f0fc";
}

.fa-beer {
  --fa: "\f0fc";
}

.fa-square-h {
  --fa: "\f0fd";
}

.fa-h-square {
  --fa: "\f0fd";
}

.fa-square-plus {
  --fa: "\f0fe";
}

.fa-plus-square {
  --fa: "\f0fe";
}

.fa-angles-left {
  --fa: "\f100";
}

.fa-angle-double-left {
  --fa: "\f100";
}

.fa-angles-right {
  --fa: "\f101";
}

.fa-angle-double-right {
  --fa: "\f101";
}

.fa-angles-up {
  --fa: "\f102";
}

.fa-angle-double-up {
  --fa: "\f102";
}

.fa-angles-down {
  --fa: "\f103";
}

.fa-angle-double-down {
  --fa: "\f103";
}

.fa-angle-left {
  --fa: "\f104";
}

.fa-angle-right {
  --fa: "\f105";
}

.fa-angle-up {
  --fa: "\f106";
}

.fa-angle-down {
  --fa: "\f107";
}

.fa-laptop {
  --fa: "\f109";
}

.fa-tablet-button {
  --fa: "\f10a";
}

.fa-mobile-button {
  --fa: "\f10b";
}

.fa-quote-left {
  --fa: "\f10d";
}

.fa-quote-left-alt {
  --fa: "\f10d";
}

.fa-quote-right {
  --fa: "\f10e";
}

.fa-quote-right-alt {
  --fa: "\f10e";
}

.fa-spinner {
  --fa: "\f110";
}

.fa-circle {
  --fa: "\f111";
}

.fa-face-smile {
  --fa: "\f118";
}

.fa-smile {
  --fa: "\f118";
}

.fa-face-frown {
  --fa: "\f119";
}

.fa-frown {
  --fa: "\f119";
}

.fa-face-meh {
  --fa: "\f11a";
}

.fa-meh {
  --fa: "\f11a";
}

.fa-gamepad {
  --fa: "\f11b";
}

.fa-keyboard {
  --fa: "\f11c";
}

.fa-flag-checkered {
  --fa: "\f11e";
}

.fa-terminal {
  --fa: "\f120";
}

.fa-code {
  --fa: "\f121";
}

.fa-reply-all {
  --fa: "\f122";
}

.fa-mail-reply-all {
  --fa: "\f122";
}

.fa-location-arrow {
  --fa: "\f124";
}

.fa-crop {
  --fa: "\f125";
}

.fa-code-branch {
  --fa: "\f126";
}

.fa-link-slash {
  --fa: "\f127";
}

.fa-chain-broken {
  --fa: "\f127";
}

.fa-chain-slash {
  --fa: "\f127";
}

.fa-unlink {
  --fa: "\f127";
}

.fa-info {
  --fa: "\f129";
}

.fa-superscript {
  --fa: "\f12b";
}

.fa-subscript {
  --fa: "\f12c";
}

.fa-eraser {
  --fa: "\f12d";
}

.fa-puzzle-piece {
  --fa: "\f12e";
}

.fa-microphone {
  --fa: "\f130";
}

.fa-microphone-slash {
  --fa: "\f131";
}

.fa-shield {
  --fa: "\f132";
}

.fa-shield-blank {
  --fa: "\f132";
}

.fa-calendar {
  --fa: "\f133";
}

.fa-fire-extinguisher {
  --fa: "\f134";
}

.fa-rocket {
  --fa: "\f135";
}

.fa-circle-chevron-left {
  --fa: "\f137";
}

.fa-chevron-circle-left {
  --fa: "\f137";
}

.fa-circle-chevron-right {
  --fa: "\f138";
}

.fa-chevron-circle-right {
  --fa: "\f138";
}

.fa-circle-chevron-up {
  --fa: "\f139";
}

.fa-chevron-circle-up {
  --fa: "\f139";
}

.fa-circle-chevron-down {
  --fa: "\f13a";
}

.fa-chevron-circle-down {
  --fa: "\f13a";
}

.fa-anchor {
  --fa: "\f13d";
}

.fa-unlock-keyhole {
  --fa: "\f13e";
}

.fa-unlock-alt {
  --fa: "\f13e";
}

.fa-bullseye {
  --fa: "\f140";
}

.fa-ellipsis {
  --fa: "\f141";
}

.fa-ellipsis-h {
  --fa: "\f141";
}

.fa-ellipsis-vertical {
  --fa: "\f142";
}

.fa-ellipsis-v {
  --fa: "\f142";
}

.fa-square-rss {
  --fa: "\f143";
}

.fa-rss-square {
  --fa: "\f143";
}

.fa-circle-play {
  --fa: "\f144";
}

.fa-play-circle {
  --fa: "\f144";
}

.fa-ticket {
  --fa: "\f145";
}

.fa-square-minus {
  --fa: "\f146";
}

.fa-minus-square {
  --fa: "\f146";
}

.fa-arrow-turn-up {
  --fa: "\f148";
}

.fa-level-up {
  --fa: "\f148";
}

.fa-arrow-turn-down {
  --fa: "\f149";
}

.fa-level-down {
  --fa: "\f149";
}

.fa-square-check {
  --fa: "\f14a";
}

.fa-check-square {
  --fa: "\f14a";
}

.fa-square-pen {
  --fa: "\f14b";
}

.fa-pen-square {
  --fa: "\f14b";
}

.fa-pencil-square {
  --fa: "\f14b";
}

.fa-square-arrow-up-right {
  --fa: "\f14c";
}

.fa-external-link-square {
  --fa: "\f14c";
}

.fa-share-from-square {
  --fa: "\f14d";
}

.fa-share-square {
  --fa: "\f14d";
}

.fa-compass {
  --fa: "\f14e";
}

.fa-square-caret-down {
  --fa: "\f150";
}

.fa-caret-square-down {
  --fa: "\f150";
}

.fa-square-caret-up {
  --fa: "\f151";
}

.fa-caret-square-up {
  --fa: "\f151";
}

.fa-square-caret-right {
  --fa: "\f152";
}

.fa-caret-square-right {
  --fa: "\f152";
}

.fa-euro-sign {
  --fa: "\f153";
}

.fa-eur {
  --fa: "\f153";
}

.fa-euro {
  --fa: "\f153";
}

.fa-sterling-sign {
  --fa: "\f154";
}

.fa-gbp {
  --fa: "\f154";
}

.fa-pound-sign {
  --fa: "\f154";
}

.fa-rupee-sign {
  --fa: "\f156";
}

.fa-rupee {
  --fa: "\f156";
}

.fa-yen-sign {
  --fa: "\f157";
}

.fa-cny {
  --fa: "\f157";
}

.fa-jpy {
  --fa: "\f157";
}

.fa-rmb {
  --fa: "\f157";
}

.fa-yen {
  --fa: "\f157";
}

.fa-ruble-sign {
  --fa: "\f158";
}

.fa-rouble {
  --fa: "\f158";
}

.fa-rub {
  --fa: "\f158";
}

.fa-ruble {
  --fa: "\f158";
}

.fa-won-sign {
  --fa: "\f159";
}

.fa-krw {
  --fa: "\f159";
}

.fa-won {
  --fa: "\f159";
}

.fa-file {
  --fa: "\f15b";
}

.fa-file-lines {
  --fa: "\f15c";
}

.fa-file-alt {
  --fa: "\f15c";
}

.fa-file-text {
  --fa: "\f15c";
}

.fa-arrow-down-a-z {
  --fa: "\f15d";
}

.fa-sort-alpha-asc {
  --fa: "\f15d";
}

.fa-sort-alpha-down {
  --fa: "\f15d";
}

.fa-arrow-up-a-z {
  --fa: "\f15e";
}

.fa-sort-alpha-up {
  --fa: "\f15e";
}

.fa-arrow-down-wide-short {
  --fa: "\f160";
}

.fa-sort-amount-asc {
  --fa: "\f160";
}

.fa-sort-amount-down {
  --fa: "\f160";
}

.fa-arrow-up-wide-short {
  --fa: "\f161";
}

.fa-sort-amount-up {
  --fa: "\f161";
}

.fa-arrow-down-1-9 {
  --fa: "\f162";
}

.fa-sort-numeric-asc {
  --fa: "\f162";
}

.fa-sort-numeric-down {
  --fa: "\f162";
}

.fa-arrow-up-1-9 {
  --fa: "\f163";
}

.fa-sort-numeric-up {
  --fa: "\f163";
}

.fa-thumbs-up {
  --fa: "\f164";
}

.fa-thumbs-down {
  --fa: "\f165";
}

.fa-arrow-down-long {
  --fa: "\f175";
}

.fa-long-arrow-down {
  --fa: "\f175";
}

.fa-arrow-up-long {
  --fa: "\f176";
}

.fa-long-arrow-up {
  --fa: "\f176";
}

.fa-arrow-left-long {
  --fa: "\f177";
}

.fa-long-arrow-left {
  --fa: "\f177";
}

.fa-arrow-right-long {
  --fa: "\f178";
}

.fa-long-arrow-right {
  --fa: "\f178";
}

.fa-person-dress {
  --fa: "\f182";
}

.fa-female {
  --fa: "\f182";
}

.fa-person {
  --fa: "\f183";
}

.fa-male {
  --fa: "\f183";
}

.fa-sun {
  --fa: "\f185";
}

.fa-moon {
  --fa: "\f186";
}

.fa-box-archive {
  --fa: "\f187";
}

.fa-archive {
  --fa: "\f187";
}

.fa-bug {
  --fa: "\f188";
}

.fa-square-caret-left {
  --fa: "\f191";
}

.fa-caret-square-left {
  --fa: "\f191";
}

.fa-circle-dot {
  --fa: "\f192";
}

.fa-dot-circle {
  --fa: "\f192";
}

.fa-wheelchair {
  --fa: "\f193";
}

.fa-lira-sign {
  --fa: "\f195";
}

.fa-shuttle-space {
  --fa: "\f197";
}

.fa-space-shuttle {
  --fa: "\f197";
}

.fa-square-envelope {
  --fa: "\f199";
}

.fa-envelope-square {
  --fa: "\f199";
}

.fa-building-columns {
  --fa: "\f19c";
}

.fa-bank {
  --fa: "\f19c";
}

.fa-institution {
  --fa: "\f19c";
}

.fa-museum {
  --fa: "\f19c";
}

.fa-university {
  --fa: "\f19c";
}

.fa-graduation-cap {
  --fa: "\f19d";
}

.fa-mortar-board {
  --fa: "\f19d";
}

.fa-language {
  --fa: "\f1ab";
}

.fa-fax {
  --fa: "\f1ac";
}

.fa-building {
  --fa: "\f1ad";
}

.fa-child {
  --fa: "\f1ae";
}

.fa-paw {
  --fa: "\f1b0";
}

.fa-cube {
  --fa: "\f1b2";
}

.fa-cubes {
  --fa: "\f1b3";
}

.fa-recycle {
  --fa: "\f1b8";
}

.fa-car {
  --fa: "\f1b9";
}

.fa-automobile {
  --fa: "\f1b9";
}

.fa-taxi {
  --fa: "\f1ba";
}

.fa-cab {
  --fa: "\f1ba";
}

.fa-tree {
  --fa: "\f1bb";
}

.fa-database {
  --fa: "\f1c0";
}

.fa-file-pdf {
  --fa: "\f1c1";
}

.fa-file-word {
  --fa: "\f1c2";
}

.fa-file-excel {
  --fa: "\f1c3";
}

.fa-file-powerpoint {
  --fa: "\f1c4";
}

.fa-file-image {
  --fa: "\f1c5";
}

.fa-file-zipper {
  --fa: "\f1c6";
}

.fa-file-archive {
  --fa: "\f1c6";
}

.fa-file-audio {
  --fa: "\f1c7";
}

.fa-file-video {
  --fa: "\f1c8";
}

.fa-file-code {
  --fa: "\f1c9";
}

.fa-life-ring {
  --fa: "\f1cd";
}

.fa-circle-notch {
  --fa: "\f1ce";
}

.fa-paper-plane {
  --fa: "\f1d8";
}

.fa-clock-rotate-left {
  --fa: "\f1da";
}

.fa-history {
  --fa: "\f1da";
}

.fa-heading {
  --fa: "\f1dc";
}

.fa-header {
  --fa: "\f1dc";
}

.fa-paragraph {
  --fa: "\f1dd";
}

.fa-sliders {
  --fa: "\f1de";
}

.fa-sliders-h {
  --fa: "\f1de";
}

.fa-share-nodes {
  --fa: "\f1e0";
}

.fa-share-alt {
  --fa: "\f1e0";
}

.fa-square-share-nodes {
  --fa: "\f1e1";
}

.fa-share-alt-square {
  --fa: "\f1e1";
}

.fa-bomb {
  --fa: "\f1e2";
}

.fa-futbol {
  --fa: "\f1e3";
}

.fa-futbol-ball {
  --fa: "\f1e3";
}

.fa-soccer-ball {
  --fa: "\f1e3";
}

.fa-tty {
  --fa: "\f1e4";
}

.fa-teletype {
  --fa: "\f1e4";
}

.fa-binoculars {
  --fa: "\f1e5";
}

.fa-plug {
  --fa: "\f1e6";
}

.fa-newspaper {
  --fa: "\f1ea";
}

.fa-wifi {
  --fa: "\f1eb";
}

.fa-wifi-3 {
  --fa: "\f1eb";
}

.fa-wifi-strong {
  --fa: "\f1eb";
}

.fa-calculator {
  --fa: "\f1ec";
}

.fa-bell-slash {
  --fa: "\f1f6";
}

.fa-trash {
  --fa: "\f1f8";
}

.fa-copyright {
  --fa: "\f1f9";
}

.fa-eye-dropper {
  --fa: "\f1fb";
}

.fa-eye-dropper-empty {
  --fa: "\f1fb";
}

.fa-eyedropper {
  --fa: "\f1fb";
}

.fa-paintbrush {
  --fa: "\f1fc";
}

.fa-paint-brush {
  --fa: "\f1fc";
}

.fa-cake-candles {
  --fa: "\f1fd";
}

.fa-birthday-cake {
  --fa: "\f1fd";
}

.fa-cake {
  --fa: "\f1fd";
}

.fa-chart-area {
  --fa: "\f1fe";
}

.fa-area-chart {
  --fa: "\f1fe";
}

.fa-chart-pie {
  --fa: "\f200";
}

.fa-pie-chart {
  --fa: "\f200";
}

.fa-chart-line {
  --fa: "\f201";
}

.fa-line-chart {
  --fa: "\f201";
}

.fa-toggle-off {
  --fa: "\f204";
}

.fa-toggle-on {
  --fa: "\f205";
}

.fa-bicycle {
  --fa: "\f206";
}

.fa-bus {
  --fa: "\f207";
}

.fa-closed-captioning {
  --fa: "\f20a";
}

.fa-shekel-sign {
  --fa: "\f20b";
}

.fa-ils {
  --fa: "\f20b";
}

.fa-shekel {
  --fa: "\f20b";
}

.fa-sheqel {
  --fa: "\f20b";
}

.fa-sheqel-sign {
  --fa: "\f20b";
}

.fa-cart-plus {
  --fa: "\f217";
}

.fa-cart-arrow-down {
  --fa: "\f218";
}

.fa-diamond {
  --fa: "\f219";
}

.fa-ship {
  --fa: "\f21a";
}

.fa-user-secret {
  --fa: "\f21b";
}

.fa-motorcycle {
  --fa: "\f21c";
}

.fa-street-view {
  --fa: "\f21d";
}

.fa-heart-pulse {
  --fa: "\f21e";
}

.fa-heartbeat {
  --fa: "\f21e";
}

.fa-venus {
  --fa: "\f221";
}

.fa-mars {
  --fa: "\f222";
}

.fa-mercury {
  --fa: "\f223";
}

.fa-mars-and-venus {
  --fa: "\f224";
}

.fa-transgender {
  --fa: "\f225";
}

.fa-transgender-alt {
  --fa: "\f225";
}

.fa-venus-double {
  --fa: "\f226";
}

.fa-mars-double {
  --fa: "\f227";
}

.fa-venus-mars {
  --fa: "\f228";
}

.fa-mars-stroke {
  --fa: "\f229";
}

.fa-mars-stroke-up {
  --fa: "\f22a";
}

.fa-mars-stroke-v {
  --fa: "\f22a";
}

.fa-mars-stroke-right {
  --fa: "\f22b";
}

.fa-mars-stroke-h {
  --fa: "\f22b";
}

.fa-neuter {
  --fa: "\f22c";
}

.fa-genderless {
  --fa: "\f22d";
}

.fa-server {
  --fa: "\f233";
}

.fa-user-plus {
  --fa: "\f234";
}

.fa-user-xmark {
  --fa: "\f235";
}

.fa-user-times {
  --fa: "\f235";
}

.fa-bed {
  --fa: "\f236";
}

.fa-train {
  --fa: "\f238";
}

.fa-train-subway {
  --fa: "\f239";
}

.fa-subway {
  --fa: "\f239";
}

.fa-battery-full {
  --fa: "\f240";
}

.fa-battery {
  --fa: "\f240";
}

.fa-battery-5 {
  --fa: "\f240";
}

.fa-battery-three-quarters {
  --fa: "\f241";
}

.fa-battery-4 {
  --fa: "\f241";
}

.fa-battery-half {
  --fa: "\f242";
}

.fa-battery-3 {
  --fa: "\f242";
}

.fa-battery-quarter {
  --fa: "\f243";
}

.fa-battery-2 {
  --fa: "\f243";
}

.fa-battery-empty {
  --fa: "\f244";
}

.fa-battery-0 {
  --fa: "\f244";
}

.fa-arrow-pointer {
  --fa: "\f245";
}

.fa-mouse-pointer {
  --fa: "\f245";
}

.fa-i-cursor {
  --fa: "\f246";
}

.fa-object-group {
  --fa: "\f247";
}

.fa-object-ungroup {
  --fa: "\f248";
}

.fa-note-sticky {
  --fa: "\f249";
}

.fa-sticky-note {
  --fa: "\f249";
}

.fa-clone {
  --fa: "\f24d";
}

.fa-scale-balanced {
  --fa: "\f24e";
}

.fa-balance-scale {
  --fa: "\f24e";
}

.fa-hourglass-start {
  --fa: "\f251";
}

.fa-hourglass-1 {
  --fa: "\f251";
}

.fa-hourglass-half {
  --fa: "\f252";
}

.fa-hourglass-2 {
  --fa: "\f252";
}

.fa-hourglass-end {
  --fa: "\f253";
}

.fa-hourglass-3 {
  --fa: "\f253";
}

.fa-hourglass {
  --fa: "\f254";
}

.fa-hourglass-empty {
  --fa: "\f254";
}

.fa-hand-back-fist {
  --fa: "\f255";
}

.fa-hand-rock {
  --fa: "\f255";
}

.fa-hand {
  --fa: "\f256";
}

.fa-hand-paper {
  --fa: "\f256";
}

.fa-hand-scissors {
  --fa: "\f257";
}

.fa-hand-lizard {
  --fa: "\f258";
}

.fa-hand-spock {
  --fa: "\f259";
}

.fa-hand-pointer {
  --fa: "\f25a";
}

.fa-hand-peace {
  --fa: "\f25b";
}

.fa-trademark {
  --fa: "\f25c";
}

.fa-registered {
  --fa: "\f25d";
}

.fa-tv {
  --fa: "\f26c";
}

.fa-television {
  --fa: "\f26c";
}

.fa-tv-alt {
  --fa: "\f26c";
}

.fa-calendar-plus {
  --fa: "\f271";
}

.fa-calendar-minus {
  --fa: "\f272";
}

.fa-calendar-xmark {
  --fa: "\f273";
}

.fa-calendar-times {
  --fa: "\f273";
}

.fa-calendar-check {
  --fa: "\f274";
}

.fa-industry {
  --fa: "\f275";
}

.fa-map-pin {
  --fa: "\f276";
}

.fa-signs-post {
  --fa: "\f277";
}

.fa-map-signs {
  --fa: "\f277";
}

.fa-map {
  --fa: "\f279";
}

.fa-message {
  --fa: "\f27a";
}

.fa-comment-alt {
  --fa: "\f27a";
}

.fa-circle-pause {
  --fa: "\f28b";
}

.fa-pause-circle {
  --fa: "\f28b";
}

.fa-circle-stop {
  --fa: "\f28d";
}

.fa-stop-circle {
  --fa: "\f28d";
}

.fa-bag-shopping {
  --fa: "\f290";
}

.fa-shopping-bag {
  --fa: "\f290";
}

.fa-basket-shopping {
  --fa: "\f291";
}

.fa-shopping-basket {
  --fa: "\f291";
}

.fa-universal-access {
  --fa: "\f29a";
}

.fa-person-walking-with-cane {
  --fa: "\f29d";
}

.fa-blind {
  --fa: "\f29d";
}

.fa-audio-description {
  --fa: "\f29e";
}

.fa-phone-volume {
  --fa: "\f2a0";
}

.fa-volume-control-phone {
  --fa: "\f2a0";
}

.fa-braille {
  --fa: "\f2a1";
}

.fa-ear-listen {
  --fa: "\f2a2";
}

.fa-assistive-listening-systems {
  --fa: "\f2a2";
}

.fa-hands-asl-interpreting {
  --fa: "\f2a3";
}

.fa-american-sign-language-interpreting {
  --fa: "\f2a3";
}

.fa-asl-interpreting {
  --fa: "\f2a3";
}

.fa-hands-american-sign-language-interpreting {
  --fa: "\f2a3";
}

.fa-ear-deaf {
  --fa: "\f2a4";
}

.fa-deaf {
  --fa: "\f2a4";
}

.fa-deafness {
  --fa: "\f2a4";
}

.fa-hard-of-hearing {
  --fa: "\f2a4";
}

.fa-hands {
  --fa: "\f2a7";
}

.fa-sign-language {
  --fa: "\f2a7";
}

.fa-signing {
  --fa: "\f2a7";
}

.fa-eye-low-vision {
  --fa: "\f2a8";
}

.fa-low-vision {
  --fa: "\f2a8";
}

.fa-font-awesome {
  --fa: "\f2b4";
}

.fa-font-awesome-flag {
  --fa: "\f2b4";
}

.fa-font-awesome-logo-full {
  --fa: "\f2b4";
}

.fa-handshake {
  --fa: "\f2b5";
}

.fa-handshake-alt {
  --fa: "\f2b5";
}

.fa-handshake-simple {
  --fa: "\f2b5";
}

.fa-envelope-open {
  --fa: "\f2b6";
}

.fa-address-book {
  --fa: "\f2b9";
}

.fa-contact-book {
  --fa: "\f2b9";
}

.fa-address-card {
  --fa: "\f2bb";
}

.fa-contact-card {
  --fa: "\f2bb";
}

.fa-vcard {
  --fa: "\f2bb";
}

.fa-circle-user {
  --fa: "\f2bd";
}

.fa-user-circle {
  --fa: "\f2bd";
}

.fa-id-badge {
  --fa: "\f2c1";
}

.fa-id-card {
  --fa: "\f2c2";
}

.fa-drivers-license {
  --fa: "\f2c2";
}

.fa-temperature-full {
  --fa: "\f2c7";
}

.fa-temperature-4 {
  --fa: "\f2c7";
}

.fa-thermometer-4 {
  --fa: "\f2c7";
}

.fa-thermometer-full {
  --fa: "\f2c7";
}

.fa-temperature-three-quarters {
  --fa: "\f2c8";
}

.fa-temperature-3 {
  --fa: "\f2c8";
}

.fa-thermometer-3 {
  --fa: "\f2c8";
}

.fa-thermometer-three-quarters {
  --fa: "\f2c8";
}

.fa-temperature-half {
  --fa: "\f2c9";
}

.fa-temperature-2 {
  --fa: "\f2c9";
}

.fa-thermometer-2 {
  --fa: "\f2c9";
}

.fa-thermometer-half {
  --fa: "\f2c9";
}

.fa-temperature-quarter {
  --fa: "\f2ca";
}

.fa-temperature-1 {
  --fa: "\f2ca";
}

.fa-thermometer-1 {
  --fa: "\f2ca";
}

.fa-thermometer-quarter {
  --fa: "\f2ca";
}

.fa-temperature-empty {
  --fa: "\f2cb";
}

.fa-temperature-0 {
  --fa: "\f2cb";
}

.fa-thermometer-0 {
  --fa: "\f2cb";
}

.fa-thermometer-empty {
  --fa: "\f2cb";
}

.fa-shower {
  --fa: "\f2cc";
}

.fa-bath {
  --fa: "\f2cd";
}

.fa-bathtub {
  --fa: "\f2cd";
}

.fa-podcast {
  --fa: "\f2ce";
}

.fa-window-maximize {
  --fa: "\f2d0";
}

.fa-window-minimize {
  --fa: "\f2d1";
}

.fa-window-restore {
  --fa: "\f2d2";
}

.fa-square-xmark {
  --fa: "\f2d3";
}

.fa-times-square {
  --fa: "\f2d3";
}

.fa-xmark-square {
  --fa: "\f2d3";
}

.fa-microchip {
  --fa: "\f2db";
}

.fa-snowflake {
  --fa: "\f2dc";
}

.fa-spoon {
  --fa: "\f2e5";
}

.fa-utensil-spoon {
  --fa: "\f2e5";
}

.fa-utensils {
  --fa: "\f2e7";
}

.fa-cutlery {
  --fa: "\f2e7";
}

.fa-rotate-left {
  --fa: "\f2ea";
}

.fa-rotate-back {
  --fa: "\f2ea";
}

.fa-rotate-backward {
  --fa: "\f2ea";
}

.fa-undo-alt {
  --fa: "\f2ea";
}

.fa-trash-can {
  --fa: "\f2ed";
}

.fa-trash-alt {
  --fa: "\f2ed";
}

.fa-rotate {
  --fa: "\f2f1";
}

.fa-sync-alt {
  --fa: "\f2f1";
}

.fa-stopwatch {
  --fa: "\f2f2";
}

.fa-right-from-bracket {
  --fa: "\f2f5";
}

.fa-sign-out-alt {
  --fa: "\f2f5";
}

.fa-right-to-bracket {
  --fa: "\f2f6";
}

.fa-sign-in-alt {
  --fa: "\f2f6";
}

.fa-rotate-right {
  --fa: "\f2f9";
}

.fa-redo-alt {
  --fa: "\f2f9";
}

.fa-rotate-forward {
  --fa: "\f2f9";
}

.fa-poo {
  --fa: "\f2fe";
}

.fa-images {
  --fa: "\f302";
}

.fa-pencil {
  --fa: "\f303";
}

.fa-pencil-alt {
  --fa: "\f303";
}

.fa-pen {
  --fa: "\f304";
}

.fa-pen-clip {
  --fa: "\f305";
}

.fa-pen-alt {
  --fa: "\f305";
}

.fa-octagon {
  --fa: "\f306";
}

.fa-down-long {
  --fa: "\f309";
}

.fa-long-arrow-alt-down {
  --fa: "\f309";
}

.fa-left-long {
  --fa: "\f30a";
}

.fa-long-arrow-alt-left {
  --fa: "\f30a";
}

.fa-right-long {
  --fa: "\f30b";
}

.fa-long-arrow-alt-right {
  --fa: "\f30b";
}

.fa-up-long {
  --fa: "\f30c";
}

.fa-long-arrow-alt-up {
  --fa: "\f30c";
}

.fa-hexagon {
  --fa: "\f312";
}

.fa-file-pen {
  --fa: "\f31c";
}

.fa-file-edit {
  --fa: "\f31c";
}

.fa-maximize {
  --fa: "\f31e";
}

.fa-expand-arrows-alt {
  --fa: "\f31e";
}

.fa-clipboard {
  --fa: "\f328";
}

.fa-left-right {
  --fa: "\f337";
}

.fa-arrows-alt-h {
  --fa: "\f337";
}

.fa-up-down {
  --fa: "\f338";
}

.fa-arrows-alt-v {
  --fa: "\f338";
}

.fa-alarm-clock {
  --fa: "\f34e";
}

.fa-circle-down {
  --fa: "\f358";
}

.fa-arrow-alt-circle-down {
  --fa: "\f358";
}

.fa-circle-left {
  --fa: "\f359";
}

.fa-arrow-alt-circle-left {
  --fa: "\f359";
}

.fa-circle-right {
  --fa: "\f35a";
}

.fa-arrow-alt-circle-right {
  --fa: "\f35a";
}

.fa-circle-up {
  --fa: "\f35b";
}

.fa-arrow-alt-circle-up {
  --fa: "\f35b";
}

.fa-up-right-from-square {
  --fa: "\f35d";
}

.fa-external-link-alt {
  --fa: "\f35d";
}

.fa-square-up-right {
  --fa: "\f360";
}

.fa-external-link-square-alt {
  --fa: "\f360";
}

.fa-right-left {
  --fa: "\f362";
}

.fa-exchange-alt {
  --fa: "\f362";
}

.fa-repeat {
  --fa: "\f363";
}

.fa-code-commit {
  --fa: "\f386";
}

.fa-code-merge {
  --fa: "\f387";
}

.fa-desktop {
  --fa: "\f390";
}

.fa-desktop-alt {
  --fa: "\f390";
}

.fa-gem {
  --fa: "\f3a5";
}

.fa-turn-down {
  --fa: "\f3be";
}

.fa-level-down-alt {
  --fa: "\f3be";
}

.fa-turn-up {
  --fa: "\f3bf";
}

.fa-level-up-alt {
  --fa: "\f3bf";
}

.fa-lock-open {
  --fa: "\f3c1";
}

.fa-location-dot {
  --fa: "\f3c5";
}

.fa-map-marker-alt {
  --fa: "\f3c5";
}

.fa-microphone-lines {
  --fa: "\f3c9";
}

.fa-microphone-alt {
  --fa: "\f3c9";
}

.fa-mobile-screen-button {
  --fa: "\f3cd";
}

.fa-mobile-alt {
  --fa: "\f3cd";
}

.fa-mobile {
  --fa: "\f3ce";
}

.fa-mobile-android {
  --fa: "\f3ce";
}

.fa-mobile-phone {
  --fa: "\f3ce";
}

.fa-mobile-screen {
  --fa: "\f3cf";
}

.fa-mobile-android-alt {
  --fa: "\f3cf";
}

.fa-money-bill-1 {
  --fa: "\f3d1";
}

.fa-money-bill-alt {
  --fa: "\f3d1";
}

.fa-phone-slash {
  --fa: "\f3dd";
}

.fa-image-portrait {
  --fa: "\f3e0";
}

.fa-portrait {
  --fa: "\f3e0";
}

.fa-reply {
  --fa: "\f3e5";
}

.fa-mail-reply {
  --fa: "\f3e5";
}

.fa-shield-halved {
  --fa: "\f3ed";
}

.fa-shield-alt {
  --fa: "\f3ed";
}

.fa-tablet-screen-button {
  --fa: "\f3fa";
}

.fa-tablet-alt {
  --fa: "\f3fa";
}

.fa-tablet {
  --fa: "\f3fb";
}

.fa-tablet-android {
  --fa: "\f3fb";
}

.fa-ticket-simple {
  --fa: "\f3ff";
}

.fa-ticket-alt {
  --fa: "\f3ff";
}

.fa-rectangle-xmark {
  --fa: "\f410";
}

.fa-rectangle-times {
  --fa: "\f410";
}

.fa-times-rectangle {
  --fa: "\f410";
}

.fa-window-close {
  --fa: "\f410";
}

.fa-down-left-and-up-right-to-center {
  --fa: "\f422";
}

.fa-compress-alt {
  --fa: "\f422";
}

.fa-up-right-and-down-left-from-center {
  --fa: "\f424";
}

.fa-expand-alt {
  --fa: "\f424";
}

.fa-baseball-bat-ball {
  --fa: "\f432";
}

.fa-baseball {
  --fa: "\f433";
}

.fa-baseball-ball {
  --fa: "\f433";
}

.fa-basketball {
  --fa: "\f434";
}

.fa-basketball-ball {
  --fa: "\f434";
}

.fa-bowling-ball {
  --fa: "\f436";
}

.fa-chess {
  --fa: "\f439";
}

.fa-chess-bishop {
  --fa: "\f43a";
}

.fa-chess-board {
  --fa: "\f43c";
}

.fa-chess-king {
  --fa: "\f43f";
}

.fa-chess-knight {
  --fa: "\f441";
}

.fa-chess-pawn {
  --fa: "\f443";
}

.fa-chess-queen {
  --fa: "\f445";
}

.fa-chess-rook {
  --fa: "\f447";
}

.fa-dumbbell {
  --fa: "\f44b";
}

.fa-football {
  --fa: "\f44e";
}

.fa-football-ball {
  --fa: "\f44e";
}

.fa-golf-ball-tee {
  --fa: "\f450";
}

.fa-golf-ball {
  --fa: "\f450";
}

.fa-hockey-puck {
  --fa: "\f453";
}

.fa-broom-ball {
  --fa: "\f458";
}

.fa-quidditch {
  --fa: "\f458";
}

.fa-quidditch-broom-ball {
  --fa: "\f458";
}

.fa-square-full {
  --fa: "\f45c";
}

.fa-table-tennis-paddle-ball {
  --fa: "\f45d";
}

.fa-ping-pong-paddle-ball {
  --fa: "\f45d";
}

.fa-table-tennis {
  --fa: "\f45d";
}

.fa-volleyball {
  --fa: "\f45f";
}

.fa-volleyball-ball {
  --fa: "\f45f";
}

.fa-hand-dots {
  --fa: "\f461";
}

.fa-allergies {
  --fa: "\f461";
}

.fa-bandage {
  --fa: "\f462";
}

.fa-band-aid {
  --fa: "\f462";
}

.fa-box {
  --fa: "\f466";
}

.fa-boxes-stacked {
  --fa: "\f468";
}

.fa-boxes {
  --fa: "\f468";
}

.fa-boxes-alt {
  --fa: "\f468";
}

.fa-briefcase-medical {
  --fa: "\f469";
}

.fa-fire-flame-simple {
  --fa: "\f46a";
}

.fa-burn {
  --fa: "\f46a";
}

.fa-capsules {
  --fa: "\f46b";
}

.fa-clipboard-check {
  --fa: "\f46c";
}

.fa-clipboard-list {
  --fa: "\f46d";
}

.fa-person-dots-from-line {
  --fa: "\f470";
}

.fa-diagnoses {
  --fa: "\f470";
}

.fa-dna {
  --fa: "\f471";
}

.fa-dolly {
  --fa: "\f472";
}

.fa-dolly-box {
  --fa: "\f472";
}

.fa-cart-flatbed {
  --fa: "\f474";
}

.fa-dolly-flatbed {
  --fa: "\f474";
}

.fa-file-medical {
  --fa: "\f477";
}

.fa-file-waveform {
  --fa: "\f478";
}

.fa-file-medical-alt {
  --fa: "\f478";
}

.fa-kit-medical {
  --fa: "\f479";
}

.fa-first-aid {
  --fa: "\f479";
}

.fa-circle-h {
  --fa: "\f47e";
}

.fa-hospital-symbol {
  --fa: "\f47e";
}

.fa-id-card-clip {
  --fa: "\f47f";
}

.fa-id-card-alt {
  --fa: "\f47f";
}

.fa-notes-medical {
  --fa: "\f481";
}

.fa-pallet {
  --fa: "\f482";
}

.fa-pills {
  --fa: "\f484";
}

.fa-prescription-bottle {
  --fa: "\f485";
}

.fa-prescription-bottle-medical {
  --fa: "\f486";
}

.fa-prescription-bottle-alt {
  --fa: "\f486";
}

.fa-bed-pulse {
  --fa: "\f487";
}

.fa-procedures {
  --fa: "\f487";
}

.fa-truck-fast {
  --fa: "\f48b";
}

.fa-shipping-fast {
  --fa: "\f48b";
}

.fa-smoking {
  --fa: "\f48d";
}

.fa-syringe {
  --fa: "\f48e";
}

.fa-tablets {
  --fa: "\f490";
}

.fa-thermometer {
  --fa: "\f491";
}

.fa-vial {
  --fa: "\f492";
}

.fa-vials {
  --fa: "\f493";
}

.fa-warehouse {
  --fa: "\f494";
}

.fa-weight-scale {
  --fa: "\f496";
}

.fa-weight {
  --fa: "\f496";
}

.fa-x-ray {
  --fa: "\f497";
}

.fa-box-open {
  --fa: "\f49e";
}

.fa-comment-dots {
  --fa: "\f4ad";
}

.fa-commenting {
  --fa: "\f4ad";
}

.fa-comment-slash {
  --fa: "\f4b3";
}

.fa-couch {
  --fa: "\f4b8";
}

.fa-circle-dollar-to-slot {
  --fa: "\f4b9";
}

.fa-donate {
  --fa: "\f4b9";
}

.fa-dove {
  --fa: "\f4ba";
}

.fa-hand-holding {
  --fa: "\f4bd";
}

.fa-hand-holding-heart {
  --fa: "\f4be";
}

.fa-hand-holding-dollar {
  --fa: "\f4c0";
}

.fa-hand-holding-usd {
  --fa: "\f4c0";
}

.fa-hand-holding-droplet {
  --fa: "\f4c1";
}

.fa-hand-holding-water {
  --fa: "\f4c1";
}

.fa-hands-holding {
  --fa: "\f4c2";
}

.fa-handshake-angle {
  --fa: "\f4c4";
}

.fa-hands-helping {
  --fa: "\f4c4";
}

.fa-parachute-box {
  --fa: "\f4cd";
}

.fa-people-carry-box {
  --fa: "\f4ce";
}

.fa-people-carry {
  --fa: "\f4ce";
}

.fa-piggy-bank {
  --fa: "\f4d3";
}

.fa-ribbon {
  --fa: "\f4d6";
}

.fa-route {
  --fa: "\f4d7";
}

.fa-seedling {
  --fa: "\f4d8";
}

.fa-sprout {
  --fa: "\f4d8";
}

.fa-sign-hanging {
  --fa: "\f4d9";
}

.fa-sign {
  --fa: "\f4d9";
}

.fa-face-smile-wink {
  --fa: "\f4da";
}

.fa-smile-wink {
  --fa: "\f4da";
}

.fa-tape {
  --fa: "\f4db";
}

.fa-truck-ramp-box {
  --fa: "\f4de";
}

.fa-truck-loading {
  --fa: "\f4de";
}

.fa-truck-moving {
  --fa: "\f4df";
}

.fa-video-slash {
  --fa: "\f4e2";
}

.fa-wine-glass {
  --fa: "\f4e3";
}

.fa-user-astronaut {
  --fa: "\f4fb";
}

.fa-user-check {
  --fa: "\f4fc";
}

.fa-user-clock {
  --fa: "\f4fd";
}

.fa-user-gear {
  --fa: "\f4fe";
}

.fa-user-cog {
  --fa: "\f4fe";
}

.fa-user-pen {
  --fa: "\f4ff";
}

.fa-user-edit {
  --fa: "\f4ff";
}

.fa-user-group {
  --fa: "\f500";
}

.fa-user-friends {
  --fa: "\f500";
}

.fa-user-graduate {
  --fa: "\f501";
}

.fa-user-lock {
  --fa: "\f502";
}

.fa-user-minus {
  --fa: "\f503";
}

.fa-user-ninja {
  --fa: "\f504";
}

.fa-user-shield {
  --fa: "\f505";
}

.fa-user-slash {
  --fa: "\f506";
}

.fa-user-alt-slash {
  --fa: "\f506";
}

.fa-user-large-slash {
  --fa: "\f506";
}

.fa-user-tag {
  --fa: "\f507";
}

.fa-user-tie {
  --fa: "\f508";
}

.fa-users-gear {
  --fa: "\f509";
}

.fa-users-cog {
  --fa: "\f509";
}

.fa-scale-unbalanced {
  --fa: "\f515";
}

.fa-balance-scale-left {
  --fa: "\f515";
}

.fa-scale-unbalanced-flip {
  --fa: "\f516";
}

.fa-balance-scale-right {
  --fa: "\f516";
}

.fa-blender {
  --fa: "\f517";
}

.fa-book-open {
  --fa: "\f518";
}

.fa-tower-broadcast {
  --fa: "\f519";
}

.fa-broadcast-tower {
  --fa: "\f519";
}

.fa-broom {
  --fa: "\f51a";
}

.fa-chalkboard {
  --fa: "\f51b";
}

.fa-blackboard {
  --fa: "\f51b";
}

.fa-chalkboard-user {
  --fa: "\f51c";
}

.fa-chalkboard-teacher {
  --fa: "\f51c";
}

.fa-church {
  --fa: "\f51d";
}

.fa-coins {
  --fa: "\f51e";
}

.fa-compact-disc {
  --fa: "\f51f";
}

.fa-crow {
  --fa: "\f520";
}

.fa-crown {
  --fa: "\f521";
}

.fa-dice {
  --fa: "\f522";
}

.fa-dice-five {
  --fa: "\f523";
}

.fa-dice-four {
  --fa: "\f524";
}

.fa-dice-one {
  --fa: "\f525";
}

.fa-dice-six {
  --fa: "\f526";
}

.fa-dice-three {
  --fa: "\f527";
}

.fa-dice-two {
  --fa: "\f528";
}

.fa-divide {
  --fa: "\f529";
}

.fa-door-closed {
  --fa: "\f52a";
}

.fa-door-open {
  --fa: "\f52b";
}

.fa-feather {
  --fa: "\f52d";
}

.fa-frog {
  --fa: "\f52e";
}

.fa-gas-pump {
  --fa: "\f52f";
}

.fa-glasses {
  --fa: "\f530";
}

.fa-greater-than-equal {
  --fa: "\f532";
}

.fa-helicopter {
  --fa: "\f533";
}

.fa-infinity {
  --fa: "\f534";
}

.fa-kiwi-bird {
  --fa: "\f535";
}

.fa-less-than-equal {
  --fa: "\f537";
}

.fa-memory {
  --fa: "\f538";
}

.fa-microphone-lines-slash {
  --fa: "\f539";
}

.fa-microphone-alt-slash {
  --fa: "\f539";
}

.fa-money-bill-wave {
  --fa: "\f53a";
}

.fa-money-bill-1-wave {
  --fa: "\f53b";
}

.fa-money-bill-wave-alt {
  --fa: "\f53b";
}

.fa-money-check {
  --fa: "\f53c";
}

.fa-money-check-dollar {
  --fa: "\f53d";
}

.fa-money-check-alt {
  --fa: "\f53d";
}

.fa-not-equal {
  --fa: "\f53e";
}

.fa-palette {
  --fa: "\f53f";
}

.fa-square-parking {
  --fa: "\f540";
}

.fa-parking {
  --fa: "\f540";
}

.fa-diagram-project {
  --fa: "\f542";
}

.fa-project-diagram {
  --fa: "\f542";
}

.fa-receipt {
  --fa: "\f543";
}

.fa-robot {
  --fa: "\f544";
}

.fa-ruler {
  --fa: "\f545";
}

.fa-ruler-combined {
  --fa: "\f546";
}

.fa-ruler-horizontal {
  --fa: "\f547";
}

.fa-ruler-vertical {
  --fa: "\f548";
}

.fa-school {
  --fa: "\f549";
}

.fa-screwdriver {
  --fa: "\f54a";
}

.fa-shoe-prints {
  --fa: "\f54b";
}

.fa-skull {
  --fa: "\f54c";
}

.fa-ban-smoking {
  --fa: "\f54d";
}

.fa-smoking-ban {
  --fa: "\f54d";
}

.fa-store {
  --fa: "\f54e";
}

.fa-shop {
  --fa: "\f54f";
}

.fa-store-alt {
  --fa: "\f54f";
}

.fa-bars-staggered {
  --fa: "\f550";
}

.fa-reorder {
  --fa: "\f550";
}

.fa-stream {
  --fa: "\f550";
}

.fa-stroopwafel {
  --fa: "\f551";
}

.fa-toolbox {
  --fa: "\f552";
}

.fa-shirt {
  --fa: "\f553";
}

.fa-t-shirt {
  --fa: "\f553";
}

.fa-tshirt {
  --fa: "\f553";
}

.fa-person-walking {
  --fa: "\f554";
}

.fa-walking {
  --fa: "\f554";
}

.fa-wallet {
  --fa: "\f555";
}

.fa-face-angry {
  --fa: "\f556";
}

.fa-angry {
  --fa: "\f556";
}

.fa-archway {
  --fa: "\f557";
}

.fa-book-atlas {
  --fa: "\f558";
}

.fa-atlas {
  --fa: "\f558";
}

.fa-award {
  --fa: "\f559";
}

.fa-delete-left {
  --fa: "\f55a";
}

.fa-backspace {
  --fa: "\f55a";
}

.fa-bezier-curve {
  --fa: "\f55b";
}

.fa-bong {
  --fa: "\f55c";
}

.fa-brush {
  --fa: "\f55d";
}

.fa-bus-simple {
  --fa: "\f55e";
}

.fa-bus-alt {
  --fa: "\f55e";
}

.fa-cannabis {
  --fa: "\f55f";
}

.fa-check-double {
  --fa: "\f560";
}

.fa-martini-glass-citrus {
  --fa: "\f561";
}

.fa-cocktail {
  --fa: "\f561";
}

.fa-bell-concierge {
  --fa: "\f562";
}

.fa-concierge-bell {
  --fa: "\f562";
}

.fa-cookie {
  --fa: "\f563";
}

.fa-cookie-bite {
  --fa: "\f564";
}

.fa-crop-simple {
  --fa: "\f565";
}

.fa-crop-alt {
  --fa: "\f565";
}

.fa-tachograph-digital {
  --fa: "\f566";
}

.fa-digital-tachograph {
  --fa: "\f566";
}

.fa-face-dizzy {
  --fa: "\f567";
}

.fa-dizzy {
  --fa: "\f567";
}

.fa-compass-drafting {
  --fa: "\f568";
}

.fa-drafting-compass {
  --fa: "\f568";
}

.fa-drum {
  --fa: "\f569";
}

.fa-drum-steelpan {
  --fa: "\f56a";
}

.fa-feather-pointed {
  --fa: "\f56b";
}

.fa-feather-alt {
  --fa: "\f56b";
}

.fa-file-contract {
  --fa: "\f56c";
}

.fa-file-arrow-down {
  --fa: "\f56d";
}

.fa-file-download {
  --fa: "\f56d";
}

.fa-file-export {
  --fa: "\f56e";
}

.fa-arrow-right-from-file {
  --fa: "\f56e";
}

.fa-file-import {
  --fa: "\f56f";
}

.fa-arrow-right-to-file {
  --fa: "\f56f";
}

.fa-file-invoice {
  --fa: "\f570";
}

.fa-file-invoice-dollar {
  --fa: "\f571";
}

.fa-file-prescription {
  --fa: "\f572";
}

.fa-file-signature {
  --fa: "\f573";
}

.fa-file-arrow-up {
  --fa: "\f574";
}

.fa-file-upload {
  --fa: "\f574";
}

.fa-fill {
  --fa: "\f575";
}

.fa-fill-drip {
  --fa: "\f576";
}

.fa-fingerprint {
  --fa: "\f577";
}

.fa-fish {
  --fa: "\f578";
}

.fa-face-flushed {
  --fa: "\f579";
}

.fa-flushed {
  --fa: "\f579";
}

.fa-face-frown-open {
  --fa: "\f57a";
}

.fa-frown-open {
  --fa: "\f57a";
}

.fa-martini-glass {
  --fa: "\f57b";
}

.fa-glass-martini-alt {
  --fa: "\f57b";
}

.fa-earth-africa {
  --fa: "\f57c";
}

.fa-globe-africa {
  --fa: "\f57c";
}

.fa-earth-americas {
  --fa: "\f57d";
}

.fa-earth {
  --fa: "\f57d";
}

.fa-earth-america {
  --fa: "\f57d";
}

.fa-globe-americas {
  --fa: "\f57d";
}

.fa-earth-asia {
  --fa: "\f57e";
}

.fa-globe-asia {
  --fa: "\f57e";
}

.fa-face-grimace {
  --fa: "\f57f";
}

.fa-grimace {
  --fa: "\f57f";
}

.fa-face-grin {
  --fa: "\f580";
}

.fa-grin {
  --fa: "\f580";
}

.fa-face-grin-wide {
  --fa: "\f581";
}

.fa-grin-alt {
  --fa: "\f581";
}

.fa-face-grin-beam {
  --fa: "\f582";
}

.fa-grin-beam {
  --fa: "\f582";
}

.fa-face-grin-beam-sweat {
  --fa: "\f583";
}

.fa-grin-beam-sweat {
  --fa: "\f583";
}

.fa-face-grin-hearts {
  --fa: "\f584";
}

.fa-grin-hearts {
  --fa: "\f584";
}

.fa-face-grin-squint {
  --fa: "\f585";
}

.fa-grin-squint {
  --fa: "\f585";
}

.fa-face-grin-squint-tears {
  --fa: "\f586";
}

.fa-grin-squint-tears {
  --fa: "\f586";
}

.fa-face-grin-stars {
  --fa: "\f587";
}

.fa-grin-stars {
  --fa: "\f587";
}

.fa-face-grin-tears {
  --fa: "\f588";
}

.fa-grin-tears {
  --fa: "\f588";
}

.fa-face-grin-tongue {
  --fa: "\f589";
}

.fa-grin-tongue {
  --fa: "\f589";
}

.fa-face-grin-tongue-squint {
  --fa: "\f58a";
}

.fa-grin-tongue-squint {
  --fa: "\f58a";
}

.fa-face-grin-tongue-wink {
  --fa: "\f58b";
}

.fa-grin-tongue-wink {
  --fa: "\f58b";
}

.fa-face-grin-wink {
  --fa: "\f58c";
}

.fa-grin-wink {
  --fa: "\f58c";
}

.fa-grip {
  --fa: "\f58d";
}

.fa-grid-horizontal {
  --fa: "\f58d";
}

.fa-grip-horizontal {
  --fa: "\f58d";
}

.fa-grip-vertical {
  --fa: "\f58e";
}

.fa-grid-vertical {
  --fa: "\f58e";
}

.fa-headset {
  --fa: "\f590";
}

.fa-highlighter {
  --fa: "\f591";
}

.fa-hot-tub-person {
  --fa: "\f593";
}

.fa-hot-tub {
  --fa: "\f593";
}

.fa-hotel {
  --fa: "\f594";
}

.fa-joint {
  --fa: "\f595";
}

.fa-face-kiss {
  --fa: "\f596";
}

.fa-kiss {
  --fa: "\f596";
}

.fa-face-kiss-beam {
  --fa: "\f597";
}

.fa-kiss-beam {
  --fa: "\f597";
}

.fa-face-kiss-wink-heart {
  --fa: "\f598";
}

.fa-kiss-wink-heart {
  --fa: "\f598";
}

.fa-face-laugh {
  --fa: "\f599";
}

.fa-laugh {
  --fa: "\f599";
}

.fa-face-laugh-beam {
  --fa: "\f59a";
}

.fa-laugh-beam {
  --fa: "\f59a";
}

.fa-face-laugh-squint {
  --fa: "\f59b";
}

.fa-laugh-squint {
  --fa: "\f59b";
}

.fa-face-laugh-wink {
  --fa: "\f59c";
}

.fa-laugh-wink {
  --fa: "\f59c";
}

.fa-cart-flatbed-suitcase {
  --fa: "\f59d";
}

.fa-luggage-cart {
  --fa: "\f59d";
}

.fa-map-location {
  --fa: "\f59f";
}

.fa-map-marked {
  --fa: "\f59f";
}

.fa-map-location-dot {
  --fa: "\f5a0";
}

.fa-map-marked-alt {
  --fa: "\f5a0";
}

.fa-marker {
  --fa: "\f5a1";
}

.fa-medal {
  --fa: "\f5a2";
}

.fa-face-meh-blank {
  --fa: "\f5a4";
}

.fa-meh-blank {
  --fa: "\f5a4";
}

.fa-face-rolling-eyes {
  --fa: "\f5a5";
}

.fa-meh-rolling-eyes {
  --fa: "\f5a5";
}

.fa-monument {
  --fa: "\f5a6";
}

.fa-mortar-pestle {
  --fa: "\f5a7";
}

.fa-paint-roller {
  --fa: "\f5aa";
}

.fa-passport {
  --fa: "\f5ab";
}

.fa-pen-fancy {
  --fa: "\f5ac";
}

.fa-pen-nib {
  --fa: "\f5ad";
}

.fa-pen-ruler {
  --fa: "\f5ae";
}

.fa-pencil-ruler {
  --fa: "\f5ae";
}

.fa-plane-arrival {
  --fa: "\f5af";
}

.fa-plane-departure {
  --fa: "\f5b0";
}

.fa-prescription {
  --fa: "\f5b1";
}

.fa-face-sad-cry {
  --fa: "\f5b3";
}

.fa-sad-cry {
  --fa: "\f5b3";
}

.fa-face-sad-tear {
  --fa: "\f5b4";
}

.fa-sad-tear {
  --fa: "\f5b4";
}

.fa-van-shuttle {
  --fa: "\f5b6";
}

.fa-shuttle-van {
  --fa: "\f5b6";
}

.fa-signature {
  --fa: "\f5b7";
}

.fa-face-smile-beam {
  --fa: "\f5b8";
}

.fa-smile-beam {
  --fa: "\f5b8";
}

.fa-solar-panel {
  --fa: "\f5ba";
}

.fa-spa {
  --fa: "\f5bb";
}

.fa-splotch {
  --fa: "\f5bc";
}

.fa-spray-can {
  --fa: "\f5bd";
}

.fa-stamp {
  --fa: "\f5bf";
}

.fa-star-half-stroke {
  --fa: "\f5c0";
}

.fa-star-half-alt {
  --fa: "\f5c0";
}

.fa-suitcase-rolling {
  --fa: "\f5c1";
}

.fa-face-surprise {
  --fa: "\f5c2";
}

.fa-surprise {
  --fa: "\f5c2";
}

.fa-swatchbook {
  --fa: "\f5c3";
}

.fa-person-swimming {
  --fa: "\f5c4";
}

.fa-swimmer {
  --fa: "\f5c4";
}

.fa-water-ladder {
  --fa: "\f5c5";
}

.fa-ladder-water {
  --fa: "\f5c5";
}

.fa-swimming-pool {
  --fa: "\f5c5";
}

.fa-droplet-slash {
  --fa: "\f5c7";
}

.fa-tint-slash {
  --fa: "\f5c7";
}

.fa-face-tired {
  --fa: "\f5c8";
}

.fa-tired {
  --fa: "\f5c8";
}

.fa-tooth {
  --fa: "\f5c9";
}

.fa-umbrella-beach {
  --fa: "\f5ca";
}

.fa-weight-hanging {
  --fa: "\f5cd";
}

.fa-wine-glass-empty {
  --fa: "\f5ce";
}

.fa-wine-glass-alt {
  --fa: "\f5ce";
}

.fa-spray-can-sparkles {
  --fa: "\f5d0";
}

.fa-air-freshener {
  --fa: "\f5d0";
}

.fa-apple-whole {
  --fa: "\f5d1";
}

.fa-apple-alt {
  --fa: "\f5d1";
}

.fa-atom {
  --fa: "\f5d2";
}

.fa-bone {
  --fa: "\f5d7";
}

.fa-book-open-reader {
  --fa: "\f5da";
}

.fa-book-reader {
  --fa: "\f5da";
}

.fa-brain {
  --fa: "\f5dc";
}

.fa-car-rear {
  --fa: "\f5de";
}

.fa-car-alt {
  --fa: "\f5de";
}

.fa-car-battery {
  --fa: "\f5df";
}

.fa-battery-car {
  --fa: "\f5df";
}

.fa-car-burst {
  --fa: "\f5e1";
}

.fa-car-crash {
  --fa: "\f5e1";
}

.fa-car-side {
  --fa: "\f5e4";
}

.fa-charging-station {
  --fa: "\f5e7";
}

.fa-diamond-turn-right {
  --fa: "\f5eb";
}

.fa-directions {
  --fa: "\f5eb";
}

.fa-draw-polygon {
  --fa: "\f5ee";
}

.fa-vector-polygon {
  --fa: "\f5ee";
}

.fa-laptop-code {
  --fa: "\f5fc";
}

.fa-layer-group {
  --fa: "\f5fd";
}

.fa-location-crosshairs {
  --fa: "\f601";
}

.fa-location {
  --fa: "\f601";
}

.fa-lungs {
  --fa: "\f604";
}

.fa-microscope {
  --fa: "\f610";
}

.fa-oil-can {
  --fa: "\f613";
}

.fa-poop {
  --fa: "\f619";
}

.fa-shapes {
  --fa: "\f61f";
}

.fa-triangle-circle-square {
  --fa: "\f61f";
}

.fa-star-of-life {
  --fa: "\f621";
}

.fa-gauge {
  --fa: "\f624";
}

.fa-dashboard {
  --fa: "\f624";
}

.fa-gauge-med {
  --fa: "\f624";
}

.fa-tachometer-alt-average {
  --fa: "\f624";
}

.fa-gauge-high {
  --fa: "\f625";
}

.fa-tachometer-alt {
  --fa: "\f625";
}

.fa-tachometer-alt-fast {
  --fa: "\f625";
}

.fa-gauge-simple {
  --fa: "\f629";
}

.fa-gauge-simple-med {
  --fa: "\f629";
}

.fa-tachometer-average {
  --fa: "\f629";
}

.fa-gauge-simple-high {
  --fa: "\f62a";
}

.fa-tachometer {
  --fa: "\f62a";
}

.fa-tachometer-fast {
  --fa: "\f62a";
}

.fa-teeth {
  --fa: "\f62e";
}

.fa-teeth-open {
  --fa: "\f62f";
}

.fa-masks-theater {
  --fa: "\f630";
}

.fa-theater-masks {
  --fa: "\f630";
}

.fa-traffic-light {
  --fa: "\f637";
}

.fa-truck-monster {
  --fa: "\f63b";
}

.fa-truck-pickup {
  --fa: "\f63c";
}

.fa-rectangle-ad {
  --fa: "\f641";
}

.fa-ad {
  --fa: "\f641";
}

.fa-ankh {
  --fa: "\f644";
}

.fa-book-bible {
  --fa: "\f647";
}

.fa-bible {
  --fa: "\f647";
}

.fa-business-time {
  --fa: "\f64a";
}

.fa-briefcase-clock {
  --fa: "\f64a";
}

.fa-city {
  --fa: "\f64f";
}

.fa-comment-dollar {
  --fa: "\f651";
}

.fa-comments-dollar {
  --fa: "\f653";
}

.fa-cross {
  --fa: "\f654";
}

.fa-dharmachakra {
  --fa: "\f655";
}

.fa-envelope-open-text {
  --fa: "\f658";
}

.fa-folder-minus {
  --fa: "\f65d";
}

.fa-folder-plus {
  --fa: "\f65e";
}

.fa-filter-circle-dollar {
  --fa: "\f662";
}

.fa-funnel-dollar {
  --fa: "\f662";
}

.fa-gopuram {
  --fa: "\f664";
}

.fa-hamsa {
  --fa: "\f665";
}

.fa-bahai {
  --fa: "\f666";
}

.fa-haykal {
  --fa: "\f666";
}

.fa-jedi {
  --fa: "\f669";
}

.fa-book-journal-whills {
  --fa: "\f66a";
}

.fa-journal-whills {
  --fa: "\f66a";
}

.fa-kaaba {
  --fa: "\f66b";
}

.fa-khanda {
  --fa: "\f66d";
}

.fa-landmark {
  --fa: "\f66f";
}

.fa-envelopes-bulk {
  --fa: "\f674";
}

.fa-mail-bulk {
  --fa: "\f674";
}

.fa-menorah {
  --fa: "\f676";
}

.fa-mosque {
  --fa: "\f678";
}

.fa-om {
  --fa: "\f679";
}

.fa-spaghetti-monster-flying {
  --fa: "\f67b";
}

.fa-pastafarianism {
  --fa: "\f67b";
}

.fa-peace {
  --fa: "\f67c";
}

.fa-place-of-worship {
  --fa: "\f67f";
}

.fa-square-poll-vertical {
  --fa: "\f681";
}

.fa-poll {
  --fa: "\f681";
}

.fa-square-poll-horizontal {
  --fa: "\f682";
}

.fa-poll-h {
  --fa: "\f682";
}

.fa-person-praying {
  --fa: "\f683";
}

.fa-pray {
  --fa: "\f683";
}

.fa-hands-praying {
  --fa: "\f684";
}

.fa-praying-hands {
  --fa: "\f684";
}

.fa-book-quran {
  --fa: "\f687";
}

.fa-quran {
  --fa: "\f687";
}

.fa-magnifying-glass-dollar {
  --fa: "\f688";
}

.fa-search-dollar {
  --fa: "\f688";
}

.fa-magnifying-glass-location {
  --fa: "\f689";
}

.fa-search-location {
  --fa: "\f689";
}

.fa-socks {
  --fa: "\f696";
}

.fa-square-root-variable {
  --fa: "\f698";
}

.fa-square-root-alt {
  --fa: "\f698";
}

.fa-star-and-crescent {
  --fa: "\f699";
}

.fa-star-of-david {
  --fa: "\f69a";
}

.fa-synagogue {
  --fa: "\f69b";
}

.fa-scroll-torah {
  --fa: "\f6a0";
}

.fa-torah {
  --fa: "\f6a0";
}

.fa-torii-gate {
  --fa: "\f6a1";
}

.fa-vihara {
  --fa: "\f6a7";
}

.fa-volume-xmark {
  --fa: "\f6a9";
}

.fa-volume-mute {
  --fa: "\f6a9";
}

.fa-volume-times {
  --fa: "\f6a9";
}

.fa-yin-yang {
  --fa: "\f6ad";
}

.fa-blender-phone {
  --fa: "\f6b6";
}

.fa-book-skull {
  --fa: "\f6b7";
}

.fa-book-dead {
  --fa: "\f6b7";
}

.fa-campground {
  --fa: "\f6bb";
}

.fa-cat {
  --fa: "\f6be";
}

.fa-chair {
  --fa: "\f6c0";
}

.fa-cloud-moon {
  --fa: "\f6c3";
}

.fa-cloud-sun {
  --fa: "\f6c4";
}

.fa-cow {
  --fa: "\f6c8";
}

.fa-dice-d20 {
  --fa: "\f6cf";
}

.fa-dice-d6 {
  --fa: "\f6d1";
}

.fa-dog {
  --fa: "\f6d3";
}

.fa-dragon {
  --fa: "\f6d5";
}

.fa-drumstick-bite {
  --fa: "\f6d7";
}

.fa-dungeon {
  --fa: "\f6d9";
}

.fa-file-csv {
  --fa: "\f6dd";
}

.fa-hand-fist {
  --fa: "\f6de";
}

.fa-fist-raised {
  --fa: "\f6de";
}

.fa-ghost {
  --fa: "\f6e2";
}

.fa-hammer {
  --fa: "\f6e3";
}

.fa-hanukiah {
  --fa: "\f6e6";
}

.fa-hat-wizard {
  --fa: "\f6e8";
}

.fa-person-hiking {
  --fa: "\f6ec";
}

.fa-hiking {
  --fa: "\f6ec";
}

.fa-hippo {
  --fa: "\f6ed";
}

.fa-horse {
  --fa: "\f6f0";
}

.fa-house-chimney-crack {
  --fa: "\f6f1";
}

.fa-house-damage {
  --fa: "\f6f1";
}

.fa-hryvnia-sign {
  --fa: "\f6f2";
}

.fa-hryvnia {
  --fa: "\f6f2";
}

.fa-mask {
  --fa: "\f6fa";
}

.fa-mountain {
  --fa: "\f6fc";
}

.fa-network-wired {
  --fa: "\f6ff";
}

.fa-otter {
  --fa: "\f700";
}

.fa-ring {
  --fa: "\f70b";
}

.fa-person-running {
  --fa: "\f70c";
}

.fa-running {
  --fa: "\f70c";
}

.fa-scroll {
  --fa: "\f70e";
}

.fa-skull-crossbones {
  --fa: "\f714";
}

.fa-slash {
  --fa: "\f715";
}

.fa-spider {
  --fa: "\f717";
}

.fa-toilet-paper {
  --fa: "\f71e";
}

.fa-toilet-paper-alt {
  --fa: "\f71e";
}

.fa-toilet-paper-blank {
  --fa: "\f71e";
}

.fa-tractor {
  --fa: "\f722";
}

.fa-user-injured {
  --fa: "\f728";
}

.fa-vr-cardboard {
  --fa: "\f729";
}

.fa-wand-sparkles {
  --fa: "\f72b";
}

.fa-wind {
  --fa: "\f72e";
}

.fa-wine-bottle {
  --fa: "\f72f";
}

.fa-cloud-meatball {
  --fa: "\f73b";
}

.fa-cloud-moon-rain {
  --fa: "\f73c";
}

.fa-cloud-rain {
  --fa: "\f73d";
}

.fa-cloud-showers-heavy {
  --fa: "\f740";
}

.fa-cloud-sun-rain {
  --fa: "\f743";
}

.fa-democrat {
  --fa: "\f747";
}

.fa-flag-usa {
  --fa: "\f74d";
}

.fa-hurricane {
  --fa: "\f751";
}

.fa-landmark-dome {
  --fa: "\f752";
}

.fa-landmark-alt {
  --fa: "\f752";
}

.fa-meteor {
  --fa: "\f753";
}

.fa-person-booth {
  --fa: "\f756";
}

.fa-poo-storm {
  --fa: "\f75a";
}

.fa-poo-bolt {
  --fa: "\f75a";
}

.fa-rainbow {
  --fa: "\f75b";
}

.fa-republican {
  --fa: "\f75e";
}

.fa-smog {
  --fa: "\f75f";
}

.fa-temperature-high {
  --fa: "\f769";
}

.fa-temperature-low {
  --fa: "\f76b";
}

.fa-cloud-bolt {
  --fa: "\f76c";
}

.fa-thunderstorm {
  --fa: "\f76c";
}

.fa-tornado {
  --fa: "\f76f";
}

.fa-volcano {
  --fa: "\f770";
}

.fa-check-to-slot {
  --fa: "\f772";
}

.fa-vote-yea {
  --fa: "\f772";
}

.fa-water {
  --fa: "\f773";
}

.fa-baby {
  --fa: "\f77c";
}

.fa-baby-carriage {
  --fa: "\f77d";
}

.fa-carriage-baby {
  --fa: "\f77d";
}

.fa-biohazard {
  --fa: "\f780";
}

.fa-blog {
  --fa: "\f781";
}

.fa-calendar-day {
  --fa: "\f783";
}

.fa-calendar-week {
  --fa: "\f784";
}

.fa-candy-cane {
  --fa: "\f786";
}

.fa-carrot {
  --fa: "\f787";
}

.fa-cash-register {
  --fa: "\f788";
}

.fa-minimize {
  --fa: "\f78c";
}

.fa-compress-arrows-alt {
  --fa: "\f78c";
}

.fa-dumpster {
  --fa: "\f793";
}

.fa-dumpster-fire {
  --fa: "\f794";
}

.fa-ethernet {
  --fa: "\f796";
}

.fa-gifts {
  --fa: "\f79c";
}

.fa-champagne-glasses {
  --fa: "\f79f";
}

.fa-glass-cheers {
  --fa: "\f79f";
}

.fa-whiskey-glass {
  --fa: "\f7a0";
}

.fa-glass-whiskey {
  --fa: "\f7a0";
}

.fa-earth-europe {
  --fa: "\f7a2";
}

.fa-globe-europe {
  --fa: "\f7a2";
}

.fa-grip-lines {
  --fa: "\f7a4";
}

.fa-grip-lines-vertical {
  --fa: "\f7a5";
}

.fa-guitar {
  --fa: "\f7a6";
}

.fa-heart-crack {
  --fa: "\f7a9";
}

.fa-heart-broken {
  --fa: "\f7a9";
}

.fa-holly-berry {
  --fa: "\f7aa";
}

.fa-horse-head {
  --fa: "\f7ab";
}

.fa-icicles {
  --fa: "\f7ad";
}

.fa-igloo {
  --fa: "\f7ae";
}

.fa-mitten {
  --fa: "\f7b5";
}

.fa-mug-hot {
  --fa: "\f7b6";
}

.fa-radiation {
  --fa: "\f7b9";
}

.fa-circle-radiation {
  --fa: "\f7ba";
}

.fa-radiation-alt {
  --fa: "\f7ba";
}

.fa-restroom {
  --fa: "\f7bd";
}

.fa-satellite {
  --fa: "\f7bf";
}

.fa-satellite-dish {
  --fa: "\f7c0";
}

.fa-sd-card {
  --fa: "\f7c2";
}

.fa-sim-card {
  --fa: "\f7c4";
}

.fa-person-skating {
  --fa: "\f7c5";
}

.fa-skating {
  --fa: "\f7c5";
}

.fa-person-skiing {
  --fa: "\f7c9";
}

.fa-skiing {
  --fa: "\f7c9";
}

.fa-person-skiing-nordic {
  --fa: "\f7ca";
}

.fa-skiing-nordic {
  --fa: "\f7ca";
}

.fa-sleigh {
  --fa: "\f7cc";
}

.fa-comment-sms {
  --fa: "\f7cd";
}

.fa-sms {
  --fa: "\f7cd";
}

.fa-person-snowboarding {
  --fa: "\f7ce";
}

.fa-snowboarding {
  --fa: "\f7ce";
}

.fa-snowman {
  --fa: "\f7d0";
}

.fa-snowplow {
  --fa: "\f7d2";
}

.fa-tenge-sign {
  --fa: "\f7d7";
}

.fa-tenge {
  --fa: "\f7d7";
}

.fa-toilet {
  --fa: "\f7d8";
}

.fa-screwdriver-wrench {
  --fa: "\f7d9";
}

.fa-tools {
  --fa: "\f7d9";
}

.fa-cable-car {
  --fa: "\f7da";
}

.fa-tram {
  --fa: "\f7da";
}

.fa-fire-flame-curved {
  --fa: "\f7e4";
}

.fa-fire-alt {
  --fa: "\f7e4";
}

.fa-bacon {
  --fa: "\f7e5";
}

.fa-book-medical {
  --fa: "\f7e6";
}

.fa-bread-slice {
  --fa: "\f7ec";
}

.fa-cheese {
  --fa: "\f7ef";
}

.fa-house-chimney-medical {
  --fa: "\f7f2";
}

.fa-clinic-medical {
  --fa: "\f7f2";
}

.fa-clipboard-user {
  --fa: "\f7f3";
}

.fa-comment-medical {
  --fa: "\f7f5";
}

.fa-crutch {
  --fa: "\f7f7";
}

.fa-disease {
  --fa: "\f7fa";
}

.fa-egg {
  --fa: "\f7fb";
}

.fa-folder-tree {
  --fa: "\f802";
}

.fa-burger {
  --fa: "\f805";
}

.fa-hamburger {
  --fa: "\f805";
}

.fa-hand-middle-finger {
  --fa: "\f806";
}

.fa-helmet-safety {
  --fa: "\f807";
}

.fa-hard-hat {
  --fa: "\f807";
}

.fa-hat-hard {
  --fa: "\f807";
}

.fa-hospital-user {
  --fa: "\f80d";
}

.fa-hotdog {
  --fa: "\f80f";
}

.fa-ice-cream {
  --fa: "\f810";
}

.fa-laptop-medical {
  --fa: "\f812";
}

.fa-pager {
  --fa: "\f815";
}

.fa-pepper-hot {
  --fa: "\f816";
}

.fa-pizza-slice {
  --fa: "\f818";
}

.fa-sack-dollar {
  --fa: "\f81d";
}

.fa-book-tanakh {
  --fa: "\f827";
}

.fa-tanakh {
  --fa: "\f827";
}

.fa-bars-progress {
  --fa: "\f828";
}

.fa-tasks-alt {
  --fa: "\f828";
}

.fa-trash-arrow-up {
  --fa: "\f829";
}

.fa-trash-restore {
  --fa: "\f829";
}

.fa-trash-can-arrow-up {
  --fa: "\f82a";
}

.fa-trash-restore-alt {
  --fa: "\f82a";
}

.fa-user-nurse {
  --fa: "\f82f";
}

.fa-wave-square {
  --fa: "\f83e";
}

.fa-person-biking {
  --fa: "\f84a";
}

.fa-biking {
  --fa: "\f84a";
}

.fa-border-all {
  --fa: "\f84c";
}

.fa-border-none {
  --fa: "\f850";
}

.fa-border-top-left {
  --fa: "\f853";
}

.fa-border-style {
  --fa: "\f853";
}

.fa-person-digging {
  --fa: "\f85e";
}

.fa-digging {
  --fa: "\f85e";
}

.fa-fan {
  --fa: "\f863";
}

.fa-icons {
  --fa: "\f86d";
}

.fa-heart-music-camera-bolt {
  --fa: "\f86d";
}

.fa-phone-flip {
  --fa: "\f879";
}

.fa-phone-alt {
  --fa: "\f879";
}

.fa-square-phone-flip {
  --fa: "\f87b";
}

.fa-phone-square-alt {
  --fa: "\f87b";
}

.fa-photo-film {
  --fa: "\f87c";
}

.fa-photo-video {
  --fa: "\f87c";
}

.fa-text-slash {
  --fa: "\f87d";
}

.fa-remove-format {
  --fa: "\f87d";
}

.fa-arrow-down-z-a {
  --fa: "\f881";
}

.fa-sort-alpha-desc {
  --fa: "\f881";
}

.fa-sort-alpha-down-alt {
  --fa: "\f881";
}

.fa-arrow-up-z-a {
  --fa: "\f882";
}

.fa-sort-alpha-up-alt {
  --fa: "\f882";
}

.fa-arrow-down-short-wide {
  --fa: "\f884";
}

.fa-sort-amount-desc {
  --fa: "\f884";
}

.fa-sort-amount-down-alt {
  --fa: "\f884";
}

.fa-arrow-up-short-wide {
  --fa: "\f885";
}

.fa-sort-amount-up-alt {
  --fa: "\f885";
}

.fa-arrow-down-9-1 {
  --fa: "\f886";
}

.fa-sort-numeric-desc {
  --fa: "\f886";
}

.fa-sort-numeric-down-alt {
  --fa: "\f886";
}

.fa-arrow-up-9-1 {
  --fa: "\f887";
}

.fa-sort-numeric-up-alt {
  --fa: "\f887";
}

.fa-spell-check {
  --fa: "\f891";
}

.fa-voicemail {
  --fa: "\f897";
}

.fa-hat-cowboy {
  --fa: "\f8c0";
}

.fa-hat-cowboy-side {
  --fa: "\f8c1";
}

.fa-computer-mouse {
  --fa: "\f8cc";
}

.fa-mouse {
  --fa: "\f8cc";
}

.fa-radio {
  --fa: "\f8d7";
}

.fa-record-vinyl {
  --fa: "\f8d9";
}

.fa-walkie-talkie {
  --fa: "\f8ef";
}

.fa-caravan {
  --fa: "\f8ff";
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\fontawesome.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa,.fa-brands,.fa-classic,.fa-regular,.fa-solid,.fab,.far,.fas{--_fa-family:var(--fa-family,var(--fa-style-family,"Font Awesome 7 Free"));-webkit-font-smoothing:antialiased;-moz-osx-font-smoothing:grayscale;display:var(--fa-display,inline-block);font-family:var(--_fa-family);font-feature-settings:normal;font-style:normal;font-synthesis:none;font-variant:normal;font-weight:var(--fa-style,900);line-height:1;text-align:center;text-rendering:auto;width:var(--fa-width,1.25em)}:is(.fas,.far,.fab,.fa-solid,.fa-regular,.fa-brands,.fa-classic,.fa):before{content:var(--fa)/""}@supports not (content:""/""){:is(.fas,.far,.fab,.fa-solid,.fa-regular,.fa-brands,.fa-classic,.fa):before{content:var(--fa)}}.fa-1x{font-size:1em}.fa-2x{font-size:2em}.fa-3x{font-size:3em}.fa-4x{font-size:4em}.fa-5x{font-size:5em}.fa-6x{font-size:6em}.fa-7x{font-size:7em}.fa-8x{font-size:8em}.fa-9x{font-size:9em}.fa-10x{font-size:10em}.fa-2xs{font-size:.625em;line-height:.1em;vertical-align:.225em}.fa-xs{font-size:.75em;line-height:.08333em;vertical-align:.125em}.fa-sm{font-size:.875em;line-height:.07143em;vertical-align:.05357em}.fa-lg{font-size:1.25em;line-height:.05em;vertical-align:-.075em}.fa-xl{font-size:1.5em;line-height:.04167em;vertical-align:-.125em}.fa-2xl{font-size:2em;line-height:.03125em;vertical-align:-.1875em}.fa-width-auto{--fa-width:auto}.fa-fw,.fa-width-fixed{--fa-width:1.25em}.fa-ul{list-style-type:none;margin-inline-start:var(--fa-li-margin,2.5em);padding-inline-start:0}.fa-ul>li{position:relative}.fa-li{inset-inline-start:calc(var(--fa-li-width, 2em)*-1);position:absolute;text-align:center;width:var(--fa-li-width,2em);line-height:inherit}.fa-border{border-radius:var(--fa-border-radius,.1em);border:var(--fa-border-width,.0625em) var(--fa-border-style,solid) var(--fa-border-color,#eee);box-sizing:var(--fa-border-box-sizing,content-box);padding:var(--fa-border-padding,.1875em .25em)}.fa-pull-left,.fa-pull-start{float:inline-start;margin-inline-end:var(--fa-pull-margin,.3em)}.fa-pull-end,.fa-pull-right{float:inline-end;margin-inline-start:var(--fa-pull-margin,.3em)}.fa-beat{animation-name:fa-beat;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-bounce{animation-name:fa-bounce;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.28,.84,.42,1))}.fa-fade{animation-name:fa-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-beat-fade,.fa-fade{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s)}.fa-beat-fade{animation-name:fa-beat-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-flip{animation-name:fa-flip;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-shake{animation-name:fa-shake;animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-shake,.fa-spin{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal)}.fa-spin{animation-name:fa-spin;animation-duration:var(--fa-animation-duration,2s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-spin-reverse{--fa-animation-direction:reverse}.fa-pulse,.fa-spin-pulse{animation-name:fa-spin;animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,steps(8))}@media (prefers-reduced-motion:reduce){.fa-beat,.fa-beat-fade,.fa-bounce,.fa-fade,.fa-flip,.fa-pulse,.fa-shake,.fa-spin,.fa-spin-pulse{animation:none!important;transition:none!important}}@keyframes fa-beat{0%,90%{transform:scale(1)}45%{transform:scale(var(--fa-beat-scale,1.25))}}@keyframes fa-bounce{0%{transform:scale(1) translateY(0)}10%{transform:scale(var(--fa-bounce-start-scale-x,1.1),var(--fa-bounce-start-scale-y,.9)) translateY(0)}30%{transform:scale(var(--fa-bounce-jump-scale-x,.9),var(--fa-bounce-jump-scale-y,1.1)) translateY(var(--fa-bounce-height,-.5em))}50%{transform:scale(var(--fa-bounce-land-scale-x,1.05),var(--fa-bounce-land-scale-y,.95)) translateY(0)}57%{transform:scale(1) translateY(var(--fa-bounce-rebound,-.125em))}64%{transform:scale(1) translateY(0)}to{transform:scale(1) translateY(0)}}@keyframes fa-fade{50%{opacity:var(--fa-fade-opacity,.4)}}@keyframes fa-beat-fade{0%,to{opacity:var(--fa-beat-fade-opacity,.4);transform:scale(1)}50%{opacity:1;transform:scale(var(--fa-beat-fade-scale,1.125))}}@keyframes fa-flip{50%{transform:rotate3d(var(--fa-flip-x,0),var(--fa-flip-y,1),var(--fa-flip-z,0),var(--fa-flip-angle,-180deg))}}@keyframes fa-shake{0%{transform:rotate(-15deg)}4%{transform:rotate(15deg)}8%,24%{transform:rotate(-18deg)}12%,28%{transform:rotate(18deg)}16%{transform:rotate(-22deg)}20%{transform:rotate(22deg)}32%{transform:rotate(-12deg)}36%{transform:rotate(12deg)}40%,to{transform:rotate(0deg)}}@keyframes fa-spin{0%{transform:rotate(0deg)}to{transform:rotate(1turn)}}.fa-rotate-90{transform:rotate(90deg)}.fa-rotate-180{transform:rotate(180deg)}.fa-rotate-270{transform:rotate(270deg)}.fa-flip-horizontal{transform:scaleX(-1)}.fa-flip-vertical{transform:scaleY(-1)}.fa-flip-both,.fa-flip-horizontal.fa-flip-vertical{transform:scale(-1)}.fa-rotate-by{transform:rotate(var(--fa-rotate-angle,0))}.fa-stack{display:inline-block;height:2em;line-height:2em;position:relative;vertical-align:middle;width:2.5em}.fa-stack-1x,.fa-stack-2x{--fa-width:100%;inset:0;position:absolute;text-align:center;width:var(--fa-width);z-index:var(--fa-stack-z-index,auto)}.fa-stack-1x{line-height:inherit}.fa-stack-2x{font-size:2em}.fa-inverse{color:var(--fa-inverse,#fff)}

.fa-0{--fa:"\30 "}.fa-1{--fa:"\31 "}.fa-2{--fa:"\32 "}.fa-3{--fa:"\33 "}.fa-4{--fa:"\34 "}.fa-5{--fa:"\35 "}.fa-6{--fa:"\36 "}.fa-7{--fa:"\37 "}.fa-8{--fa:"\38 "}.fa-9{--fa:"\39 "}.fa-exclamation{--fa:"\!"}.fa-hashtag{--fa:"\#"}.fa-dollar,.fa-dollar-sign,.fa-usd{--fa:"\$"}.fa-percent,.fa-percentage{--fa:"\%"}.fa-asterisk{--fa:"\*"}.fa-add,.fa-plus{--fa:"\+"}.fa-less-than{--fa:"\<"}.fa-equals{--fa:"\="}.fa-greater-than{--fa:"\>"}.fa-question{--fa:"\?"}.fa-at{--fa:"\@"}.fa-a{--fa:"A"}.fa-b{--fa:"B"}.fa-c{--fa:"C"}.fa-d{--fa:"D"}.fa-e{--fa:"E"}.fa-f{--fa:"F"}.fa-g{--fa:"G"}.fa-h{--fa:"H"}.fa-i{--fa:"I"}.fa-j{--fa:"J"}.fa-k{--fa:"K"}.fa-l{--fa:"L"}.fa-m{--fa:"M"}.fa-n{--fa:"N"}.fa-o{--fa:"O"}.fa-p{--fa:"P"}.fa-q{--fa:"Q"}.fa-r{--fa:"R"}.fa-s{--fa:"S"}.fa-t{--fa:"T"}.fa-u{--fa:"U"}.fa-v{--fa:"V"}.fa-w{--fa:"W"}.fa-x{--fa:"X"}.fa-y{--fa:"Y"}.fa-z{--fa:"Z"}.fa-faucet{--fa:"\e005"}.fa-faucet-drip{--fa:"\e006"}.fa-house-chimney-window{--fa:"\e00d"}.fa-house-signal{--fa:"\e012"}.fa-temperature-arrow-down,.fa-temperature-down{--fa:"\e03f"}.fa-temperature-arrow-up,.fa-temperature-up{--fa:"\e040"}.fa-trailer{--fa:"\e041"}.fa-bacteria{--fa:"\e059"}.fa-bacterium{--fa:"\e05a"}.fa-box-tissue{--fa:"\e05b"}.fa-hand-holding-medical{--fa:"\e05c"}.fa-hand-sparkles{--fa:"\e05d"}.fa-hands-bubbles,.fa-hands-wash{--fa:"\e05e"}.fa-handshake-alt-slash,.fa-handshake-simple-slash,.fa-handshake-slash{--fa:"\e060"}.fa-head-side-cough{--fa:"\e061"}.fa-head-side-cough-slash{--fa:"\e062"}.fa-head-side-mask{--fa:"\e063"}.fa-head-side-virus{--fa:"\e064"}.fa-house-chimney-user{--fa:"\e065"}.fa-house-laptop,.fa-laptop-house{--fa:"\e066"}.fa-lungs-virus{--fa:"\e067"}.fa-people-arrows,.fa-people-arrows-left-right{--fa:"\e068"}.fa-plane-slash{--fa:"\e069"}.fa-pump-medical{--fa:"\e06a"}.fa-pump-soap{--fa:"\e06b"}.fa-shield-virus{--fa:"\e06c"}.fa-sink{--fa:"\e06d"}.fa-soap{--fa:"\e06e"}.fa-stopwatch-20{--fa:"\e06f"}.fa-shop-slash,.fa-store-alt-slash{--fa:"\e070"}.fa-store-slash{--fa:"\e071"}.fa-toilet-paper-slash{--fa:"\e072"}.fa-users-slash{--fa:"\e073"}.fa-virus{--fa:"\e074"}.fa-virus-slash{--fa:"\e075"}.fa-viruses{--fa:"\e076"}.fa-vest{--fa:"\e085"}.fa-vest-patches{--fa:"\e086"}.fa-arrow-trend-down{--fa:"\e097"}.fa-arrow-trend-up{--fa:"\e098"}.fa-arrow-up-from-bracket{--fa:"\e09a"}.fa-austral-sign{--fa:"\e0a9"}.fa-baht-sign{--fa:"\e0ac"}.fa-bitcoin-sign{--fa:"\e0b4"}.fa-bolt-lightning{--fa:"\e0b7"}.fa-book-bookmark{--fa:"\e0bb"}.fa-camera-rotate{--fa:"\e0d8"}.fa-cedi-sign{--fa:"\e0df"}.fa-chart-column{--fa:"\e0e3"}.fa-chart-gantt{--fa:"\e0e4"}.fa-clapperboard{--fa:"\e131"}.fa-clover{--fa:"\e139"}.fa-code-compare{--fa:"\e13a"}.fa-code-fork{--fa:"\e13b"}.fa-code-pull-request{--fa:"\e13c"}.fa-colon-sign{--fa:"\e140"}.fa-cruzeiro-sign{--fa:"\e152"}.fa-display{--fa:"\e163"}.fa-dong-sign{--fa:"\e169"}.fa-elevator{--fa:"\e16d"}.fa-filter-circle-xmark{--fa:"\e17b"}.fa-florin-sign{--fa:"\e184"}.fa-folder-closed{--fa:"\e185"}.fa-franc-sign{--fa:"\e18f"}.fa-guarani-sign{--fa:"\e19a"}.fa-gun{--fa:"\e19b"}.fa-hands-clapping{--fa:"\e1a8"}.fa-home-user,.fa-house-user{--fa:"\e1b0"}.fa-indian-rupee,.fa-indian-rupee-sign,.fa-inr{--fa:"\e1bc"}.fa-kip-sign{--fa:"\e1c4"}.fa-lari-sign{--fa:"\e1c8"}.fa-litecoin-sign{--fa:"\e1d3"}.fa-manat-sign{--fa:"\e1d5"}.fa-mask-face{--fa:"\e1d7"}.fa-mill-sign{--fa:"\e1ed"}.fa-money-bills{--fa:"\e1f3"}.fa-naira-sign{--fa:"\e1f6"}.fa-notdef{--fa:"\e1fe"}.fa-panorama{--fa:"\e209"}.fa-peseta-sign{--fa:"\e221"}.fa-peso-sign{--fa:"\e222"}.fa-plane-up{--fa:"\e22d"}.fa-rupiah-sign{--fa:"\e23d"}.fa-stairs{--fa:"\e289"}.fa-timeline{--fa:"\e29c"}.fa-truck-front{--fa:"\e2b7"}.fa-try,.fa-turkish-lira,.fa-turkish-lira-sign{--fa:"\e2bb"}.fa-vault{--fa:"\e2c5"}.fa-magic-wand-sparkles,.fa-wand-magic-sparkles{--fa:"\e2ca"}.fa-wheat-alt,.fa-wheat-awn{--fa:"\e2cd"}.fa-wheelchair-alt,.fa-wheelchair-move{--fa:"\e2ce"}.fa-bangladeshi-taka-sign{--fa:"\e2e6"}.fa-bowl-rice{--fa:"\e2eb"}.fa-person-pregnant{--fa:"\e31e"}.fa-home-lg,.fa-house-chimney{--fa:"\e3af"}.fa-house-crack{--fa:"\e3b1"}.fa-house-medical{--fa:"\e3b2"}.fa-cent-sign{--fa:"\e3f5"}.fa-plus-minus{--fa:"\e43c"}.fa-sailboat{--fa:"\e445"}.fa-section{--fa:"\e447"}.fa-shrimp{--fa:"\e448"}.fa-brazilian-real-sign{--fa:"\e46c"}.fa-chart-simple{--fa:"\e473"}.fa-diagram-next{--fa:"\e476"}.fa-diagram-predecessor{--fa:"\e477"}.fa-diagram-successor{--fa:"\e47a"}.fa-earth-oceania,.fa-globe-oceania{--fa:"\e47b"}.fa-bug-slash{--fa:"\e490"}.fa-file-circle-plus{--fa:"\e494"}.fa-shop-lock{--fa:"\e4a5"}.fa-virus-covid{--fa:"\e4a8"}.fa-virus-covid-slash{--fa:"\e4a9"}.fa-anchor-circle-check{--fa:"\e4aa"}.fa-anchor-circle-exclamation{--fa:"\e4ab"}.fa-anchor-circle-xmark{--fa:"\e4ac"}.fa-anchor-lock{--fa:"\e4ad"}.fa-arrow-down-up-across-line{--fa:"\e4af"}.fa-arrow-down-up-lock{--fa:"\e4b0"}.fa-arrow-right-to-city{--fa:"\e4b3"}.fa-arrow-up-from-ground-water{--fa:"\e4b5"}.fa-arrow-up-from-water-pump{--fa:"\e4b6"}.fa-arrow-up-right-dots{--fa:"\e4b7"}.fa-arrows-down-to-line{--fa:"\e4b8"}.fa-arrows-down-to-people{--fa:"\e4b9"}.fa-arrows-left-right-to-line{--fa:"\e4ba"}.fa-arrows-spin{--fa:"\e4bb"}.fa-arrows-split-up-and-left{--fa:"\e4bc"}.fa-arrows-to-circle{--fa:"\e4bd"}.fa-arrows-to-dot{--fa:"\e4be"}.fa-arrows-to-eye{--fa:"\e4bf"}.fa-arrows-turn-right{--fa:"\e4c0"}.fa-arrows-turn-to-dots{--fa:"\e4c1"}.fa-arrows-up-to-line{--fa:"\e4c2"}.fa-bore-hole{--fa:"\e4c3"}.fa-bottle-droplet{--fa:"\e4c4"}.fa-bottle-water{--fa:"\e4c5"}.fa-bowl-food{--fa:"\e4c6"}.fa-boxes-packing{--fa:"\e4c7"}.fa-bridge{--fa:"\e4c8"}.fa-bridge-circle-check{--fa:"\e4c9"}.fa-bridge-circle-exclamation{--fa:"\e4ca"}.fa-bridge-circle-xmark{--fa:"\e4cb"}.fa-bridge-lock{--fa:"\e4cc"}.fa-bridge-water{--fa:"\e4ce"}.fa-bucket{--fa:"\e4cf"}.fa-bugs{--fa:"\e4d0"}.fa-building-circle-arrow-right{--fa:"\e4d1"}.fa-building-circle-check{--fa:"\e4d2"}.fa-building-circle-exclamation{--fa:"\e4d3"}.fa-building-circle-xmark{--fa:"\e4d4"}.fa-building-flag{--fa:"\e4d5"}.fa-building-lock{--fa:"\e4d6"}.fa-building-ngo{--fa:"\e4d7"}.fa-building-shield{--fa:"\e4d8"}.fa-building-un{--fa:"\e4d9"}.fa-building-user{--fa:"\e4da"}.fa-building-wheat{--fa:"\e4db"}.fa-burst{--fa:"\e4dc"}.fa-car-on{--fa:"\e4dd"}.fa-car-tunnel{--fa:"\e4de"}.fa-child-combatant,.fa-child-rifle{--fa:"\e4e0"}.fa-children{--fa:"\e4e1"}.fa-circle-nodes{--fa:"\e4e2"}.fa-clipboard-question{--fa:"\e4e3"}.fa-cloud-showers-water{--fa:"\e4e4"}.fa-computer{--fa:"\e4e5"}.fa-cubes-stacked{--fa:"\e4e6"}.fa-envelope-circle-check{--fa:"\e4e8"}.fa-explosion{--fa:"\e4e9"}.fa-ferry{--fa:"\e4ea"}.fa-file-circle-exclamation{--fa:"\e4eb"}.fa-file-circle-minus{--fa:"\e4ed"}.fa-file-circle-question{--fa:"\e4ef"}.fa-file-shield{--fa:"\e4f0"}.fa-fire-burner{--fa:"\e4f1"}.fa-fish-fins{--fa:"\e4f2"}.fa-flask-vial{--fa:"\e4f3"}.fa-glass-water{--fa:"\e4f4"}.fa-glass-water-droplet{--fa:"\e4f5"}.fa-group-arrows-rotate{--fa:"\e4f6"}.fa-hand-holding-hand{--fa:"\e4f7"}.fa-handcuffs{--fa:"\e4f8"}.fa-hands-bound{--fa:"\e4f9"}.fa-hands-holding-child{--fa:"\e4fa"}.fa-hands-holding-circle{--fa:"\e4fb"}.fa-heart-circle-bolt{--fa:"\e4fc"}.fa-heart-circle-check{--fa:"\e4fd"}.fa-heart-circle-exclamation{--fa:"\e4fe"}.fa-heart-circle-minus{--fa:"\e4ff"}.fa-heart-circle-plus{--fa:"\e500"}.fa-heart-circle-xmark{--fa:"\e501"}.fa-helicopter-symbol{--fa:"\e502"}.fa-helmet-un{--fa:"\e503"}.fa-hill-avalanche{--fa:"\e507"}.fa-hill-rockslide{--fa:"\e508"}.fa-house-circle-check{--fa:"\e509"}.fa-house-circle-exclamation{--fa:"\e50a"}.fa-house-circle-xmark{--fa:"\e50b"}.fa-house-fire{--fa:"\e50c"}.fa-house-flag{--fa:"\e50d"}.fa-house-flood-water{--fa:"\e50e"}.fa-house-flood-water-circle-arrow-right{--fa:"\e50f"}.fa-house-lock{--fa:"\e510"}.fa-house-medical-circle-check{--fa:"\e511"}.fa-house-medical-circle-exclamation{--fa:"\e512"}.fa-house-medical-circle-xmark{--fa:"\e513"}.fa-house-medical-flag{--fa:"\e514"}.fa-house-tsunami{--fa:"\e515"}.fa-jar{--fa:"\e516"}.fa-jar-wheat{--fa:"\e517"}.fa-jet-fighter-up{--fa:"\e518"}.fa-jug-detergent{--fa:"\e519"}.fa-kitchen-set{--fa:"\e51a"}.fa-land-mine-on{--fa:"\e51b"}.fa-landmark-flag{--fa:"\e51c"}.fa-laptop-file{--fa:"\e51d"}.fa-lines-leaning{--fa:"\e51e"}.fa-location-pin-lock{--fa:"\e51f"}.fa-locust{--fa:"\e520"}.fa-magnifying-glass-arrow-right{--fa:"\e521"}.fa-magnifying-glass-chart{--fa:"\e522"}.fa-mars-and-venus-burst{--fa:"\e523"}.fa-mask-ventilator{--fa:"\e524"}.fa-mattress-pillow{--fa:"\e525"}.fa-mobile-retro{--fa:"\e527"}.fa-money-bill-transfer{--fa:"\e528"}.fa-money-bill-trend-up{--fa:"\e529"}.fa-money-bill-wheat{--fa:"\e52a"}.fa-mosquito{--fa:"\e52b"}.fa-mosquito-net{--fa:"\e52c"}.fa-mound{--fa:"\e52d"}.fa-mountain-city{--fa:"\e52e"}.fa-mountain-sun{--fa:"\e52f"}.fa-oil-well{--fa:"\e532"}.fa-people-group{--fa:"\e533"}.fa-people-line{--fa:"\e534"}.fa-people-pulling{--fa:"\e535"}.fa-people-robbery{--fa:"\e536"}.fa-people-roof{--fa:"\e537"}.fa-person-arrow-down-to-line{--fa:"\e538"}.fa-person-arrow-up-from-line{--fa:"\e539"}.fa-person-breastfeeding{--fa:"\e53a"}.fa-person-burst{--fa:"\e53b"}.fa-person-cane{--fa:"\e53c"}.fa-person-chalkboard{--fa:"\e53d"}.fa-person-circle-check{--fa:"\e53e"}.fa-person-circle-exclamation{--fa:"\e53f"}.fa-person-circle-minus{--fa:"\e540"}.fa-person-circle-plus{--fa:"\e541"}.fa-person-circle-question{--fa:"\e542"}.fa-person-circle-xmark{--fa:"\e543"}.fa-person-dress-burst{--fa:"\e544"}.fa-person-drowning{--fa:"\e545"}.fa-person-falling{--fa:"\e546"}.fa-person-falling-burst{--fa:"\e547"}.fa-person-half-dress{--fa:"\e548"}.fa-person-harassing{--fa:"\e549"}.fa-person-military-pointing{--fa:"\e54a"}.fa-person-military-rifle{--fa:"\e54b"}.fa-person-military-to-person{--fa:"\e54c"}.fa-person-rays{--fa:"\e54d"}.fa-person-rifle{--fa:"\e54e"}.fa-person-shelter{--fa:"\e54f"}.fa-person-walking-arrow-loop-left{--fa:"\e551"}.fa-person-walking-arrow-right{--fa:"\e552"}.fa-person-walking-dashed-line-arrow-right{--fa:"\e553"}.fa-person-walking-luggage{--fa:"\e554"}.fa-plane-circle-check{--fa:"\e555"}.fa-plane-circle-exclamation{--fa:"\e556"}.fa-plane-circle-xmark{--fa:"\e557"}.fa-plane-lock{--fa:"\e558"}.fa-plate-wheat{--fa:"\e55a"}.fa-plug-circle-bolt{--fa:"\e55b"}.fa-plug-circle-check{--fa:"\e55c"}.fa-plug-circle-exclamation{--fa:"\e55d"}.fa-plug-circle-minus{--fa:"\e55e"}.fa-plug-circle-plus{--fa:"\e55f"}.fa-plug-circle-xmark{--fa:"\e560"}.fa-ranking-star{--fa:"\e561"}.fa-road-barrier{--fa:"\e562"}.fa-road-bridge{--fa:"\e563"}.fa-road-circle-check{--fa:"\e564"}.fa-road-circle-exclamation{--fa:"\e565"}.fa-road-circle-xmark{--fa:"\e566"}.fa-road-lock{--fa:"\e567"}.fa-road-spikes{--fa:"\e568"}.fa-rug{--fa:"\e569"}.fa-sack-xmark{--fa:"\e56a"}.fa-school-circle-check{--fa:"\e56b"}.fa-school-circle-exclamation{--fa:"\e56c"}.fa-school-circle-xmark{--fa:"\e56d"}.fa-school-flag{--fa:"\e56e"}.fa-school-lock{--fa:"\e56f"}.fa-sheet-plastic{--fa:"\e571"}.fa-shield-cat{--fa:"\e572"}.fa-shield-dog{--fa:"\e573"}.fa-shield-heart{--fa:"\e574"}.fa-square-nfi{--fa:"\e576"}.fa-square-person-confined{--fa:"\e577"}.fa-square-virus{--fa:"\e578"}.fa-rod-asclepius,.fa-rod-snake,.fa-staff-aesculapius,.fa-staff-snake{--fa:"\e579"}.fa-sun-plant-wilt{--fa:"\e57a"}.fa-tarp{--fa:"\e57b"}.fa-tarp-droplet{--fa:"\e57c"}.fa-tent{--fa:"\e57d"}.fa-tent-arrow-down-to-line{--fa:"\e57e"}.fa-tent-arrow-left-right{--fa:"\e57f"}.fa-tent-arrow-turn-left{--fa:"\e580"}.fa-tent-arrows-down{--fa:"\e581"}.fa-tents{--fa:"\e582"}.fa-toilet-portable{--fa:"\e583"}.fa-toilets-portable{--fa:"\e584"}.fa-tower-cell{--fa:"\e585"}.fa-tower-observation{--fa:"\e586"}.fa-tree-city{--fa:"\e587"}.fa-trowel{--fa:"\e589"}.fa-trowel-bricks{--fa:"\e58a"}.fa-truck-arrow-right{--fa:"\e58b"}.fa-truck-droplet{--fa:"\e58c"}.fa-truck-field{--fa:"\e58d"}.fa-truck-field-un{--fa:"\e58e"}.fa-truck-plane{--fa:"\e58f"}.fa-users-between-lines{--fa:"\e591"}.fa-users-line{--fa:"\e592"}.fa-users-rays{--fa:"\e593"}.fa-users-rectangle{--fa:"\e594"}.fa-users-viewfinder{--fa:"\e595"}.fa-vial-circle-check{--fa:"\e596"}.fa-vial-virus{--fa:"\e597"}.fa-wheat-awn-circle-exclamation{--fa:"\e598"}.fa-worm{--fa:"\e599"}.fa-xmarks-lines{--fa:"\e59a"}.fa-child-dress{--fa:"\e59c"}.fa-child-reaching{--fa:"\e59d"}.fa-file-circle-check{--fa:"\e5a0"}.fa-file-circle-xmark{--fa:"\e5a1"}.fa-person-through-window{--fa:"\e5a9"}.fa-plant-wilt{--fa:"\e5aa"}.fa-stapler{--fa:"\e5af"}.fa-train-tram{--fa:"\e5b4"}.fa-table-cells-column-lock{--fa:"\e678"}.fa-table-cells-row-lock{--fa:"\e67a"}.fa-web-awesome{--fa:"\e682"}.fa-thumb-tack-slash,.fa-thumbtack-slash{--fa:"\e68f"}.fa-table-cells-row-unlock{--fa:"\e691"}.fa-chart-diagram{--fa:"\e695"}.fa-comment-nodes{--fa:"\e696"}.fa-file-fragment{--fa:"\e697"}.fa-file-half-dashed{--fa:"\e698"}.fa-hexagon-nodes{--fa:"\e699"}.fa-hexagon-nodes-bolt{--fa:"\e69a"}.fa-square-binary{--fa:"\e69b"}.fa-pentagon{--fa:"\e790"}.fa-non-binary{--fa:"\e807"}.fa-spiral{--fa:"\e80a"}.fa-mobile-vibrate{--fa:"\e816"}.fa-single-quote-left{--fa:"\e81b"}.fa-single-quote-right{--fa:"\e81c"}.fa-bus-side{--fa:"\e81d"}.fa-heptagon,.fa-septagon{--fa:"\e820"}.fa-glass-martini,.fa-martini-glass-empty{--fa:"\f000"}.fa-music{--fa:"\f001"}.fa-magnifying-glass,.fa-search{--fa:"\f002"}.fa-heart{--fa:"\f004"}.fa-star{--fa:"\f005"}.fa-user,.fa-user-alt,.fa-user-large{--fa:"\f007"}.fa-film,.fa-film-alt,.fa-film-simple{--fa:"\f008"}.fa-table-cells-large,.fa-th-large{--fa:"\f009"}.fa-table-cells,.fa-th{--fa:"\f00a"}.fa-table-list,.fa-th-list{--fa:"\f00b"}.fa-check{--fa:"\f00c"}.fa-close,.fa-multiply,.fa-remove,.fa-times,.fa-xmark{--fa:"\f00d"}.fa-magnifying-glass-plus,.fa-search-plus{--fa:"\f00e"}.fa-magnifying-glass-minus,.fa-search-minus{--fa:"\f010"}.fa-power-off{--fa:"\f011"}.fa-signal,.fa-signal-5,.fa-signal-perfect{--fa:"\f012"}.fa-cog,.fa-gear{--fa:"\f013"}.fa-home,.fa-home-alt,.fa-home-lg-alt,.fa-house{--fa:"\f015"}.fa-clock,.fa-clock-four{--fa:"\f017"}.fa-road{--fa:"\f018"}.fa-download{--fa:"\f019"}.fa-inbox{--fa:"\f01c"}.fa-arrow-right-rotate,.fa-arrow-rotate-forward,.fa-arrow-rotate-right,.fa-redo{--fa:"\f01e"}.fa-arrows-rotate,.fa-refresh,.fa-sync{--fa:"\f021"}.fa-list-alt,.fa-rectangle-list{--fa:"\f022"}.fa-lock{--fa:"\f023"}.fa-flag{--fa:"\f024"}.fa-headphones,.fa-headphones-alt,.fa-headphones-simple{--fa:"\f025"}.fa-volume-off{--fa:"\f026"}.fa-volume-down,.fa-volume-low{--fa:"\f027"}.fa-volume-high,.fa-volume-up{--fa:"\f028"}.fa-qrcode{--fa:"\f029"}.fa-barcode{--fa:"\f02a"}.fa-tag{--fa:"\f02b"}.fa-tags{--fa:"\f02c"}.fa-book{--fa:"\f02d"}.fa-bookmark{--fa:"\f02e"}.fa-print{--fa:"\f02f"}.fa-camera,.fa-camera-alt{--fa:"\f030"}.fa-font{--fa:"\f031"}.fa-bold{--fa:"\f032"}.fa-italic{--fa:"\f033"}.fa-text-height{--fa:"\f034"}.fa-text-width{--fa:"\f035"}.fa-align-left{--fa:"\f036"}.fa-align-center{--fa:"\f037"}.fa-align-right{--fa:"\f038"}.fa-align-justify{--fa:"\f039"}.fa-list,.fa-list-squares{--fa:"\f03a"}.fa-dedent,.fa-outdent{--fa:"\f03b"}.fa-indent{--fa:"\f03c"}.fa-video,.fa-video-camera{--fa:"\f03d"}.fa-image{--fa:"\f03e"}.fa-location-pin,.fa-map-marker{--fa:"\f041"}.fa-adjust,.fa-circle-half-stroke{--fa:"\f042"}.fa-droplet,.fa-tint{--fa:"\f043"}.fa-edit,.fa-pen-to-square{--fa:"\f044"}.fa-arrows,.fa-arrows-up-down-left-right{--fa:"\f047"}.fa-backward-step,.fa-step-backward{--fa:"\f048"}.fa-backward-fast,.fa-fast-backward{--fa:"\f049"}.fa-backward{--fa:"\f04a"}.fa-play{--fa:"\f04b"}.fa-pause{--fa:"\f04c"}.fa-stop{--fa:"\f04d"}.fa-forward{--fa:"\f04e"}.fa-fast-forward,.fa-forward-fast{--fa:"\f050"}.fa-forward-step,.fa-step-forward{--fa:"\f051"}.fa-eject{--fa:"\f052"}.fa-chevron-left{--fa:"\f053"}.fa-chevron-right{--fa:"\f054"}.fa-circle-plus,.fa-plus-circle{--fa:"\f055"}.fa-circle-minus,.fa-minus-circle{--fa:"\f056"}.fa-circle-xmark,.fa-times-circle,.fa-xmark-circle{--fa:"\f057"}.fa-check-circle,.fa-circle-check{--fa:"\f058"}.fa-circle-question,.fa-question-circle{--fa:"\f059"}.fa-circle-info,.fa-info-circle{--fa:"\f05a"}.fa-crosshairs{--fa:"\f05b"}.fa-ban,.fa-cancel{--fa:"\f05e"}.fa-arrow-left{--fa:"\f060"}.fa-arrow-right{--fa:"\f061"}.fa-arrow-up{--fa:"\f062"}.fa-arrow-down{--fa:"\f063"}.fa-mail-forward,.fa-share{--fa:"\f064"}.fa-expand{--fa:"\f065"}.fa-compress{--fa:"\f066"}.fa-minus,.fa-subtract{--fa:"\f068"}.fa-circle-exclamation,.fa-exclamation-circle{--fa:"\f06a"}.fa-gift{--fa:"\f06b"}.fa-leaf{--fa:"\f06c"}.fa-fire{--fa:"\f06d"}.fa-eye{--fa:"\f06e"}.fa-eye-slash{--fa:"\f070"}.fa-exclamation-triangle,.fa-triangle-exclamation,.fa-warning{--fa:"\f071"}.fa-plane{--fa:"\f072"}.fa-calendar-alt,.fa-calendar-days{--fa:"\f073"}.fa-random,.fa-shuffle{--fa:"\f074"}.fa-comment{--fa:"\f075"}.fa-magnet{--fa:"\f076"}.fa-chevron-up{--fa:"\f077"}.fa-chevron-down{--fa:"\f078"}.fa-retweet{--fa:"\f079"}.fa-cart-shopping,.fa-shopping-cart{--fa:"\f07a"}.fa-folder,.fa-folder-blank{--fa:"\f07b"}.fa-folder-open{--fa:"\f07c"}.fa-arrows-up-down,.fa-arrows-v{--fa:"\f07d"}.fa-arrows-h,.fa-arrows-left-right{--fa:"\f07e"}.fa-bar-chart,.fa-chart-bar{--fa:"\f080"}.fa-camera-retro{--fa:"\f083"}.fa-key{--fa:"\f084"}.fa-cogs,.fa-gears{--fa:"\f085"}.fa-comments{--fa:"\f086"}.fa-star-half{--fa:"\f089"}.fa-arrow-right-from-bracket,.fa-sign-out{--fa:"\f08b"}.fa-thumb-tack,.fa-thumbtack{--fa:"\f08d"}.fa-arrow-up-right-from-square,.fa-external-link{--fa:"\f08e"}.fa-arrow-right-to-bracket,.fa-sign-in{--fa:"\f090"}.fa-trophy{--fa:"\f091"}.fa-upload{--fa:"\f093"}.fa-lemon{--fa:"\f094"}.fa-phone{--fa:"\f095"}.fa-phone-square,.fa-square-phone{--fa:"\f098"}.fa-unlock{--fa:"\f09c"}.fa-credit-card,.fa-credit-card-alt{--fa:"\f09d"}.fa-feed,.fa-rss{--fa:"\f09e"}.fa-hard-drive,.fa-hdd{--fa:"\f0a0"}.fa-bullhorn{--fa:"\f0a1"}.fa-certificate{--fa:"\f0a3"}.fa-hand-point-right{--fa:"\f0a4"}.fa-hand-point-left{--fa:"\f0a5"}.fa-hand-point-up{--fa:"\f0a6"}.fa-hand-point-down{--fa:"\f0a7"}.fa-arrow-circle-left,.fa-circle-arrow-left{--fa:"\f0a8"}.fa-arrow-circle-right,.fa-circle-arrow-right{--fa:"\f0a9"}.fa-arrow-circle-up,.fa-circle-arrow-up{--fa:"\f0aa"}.fa-arrow-circle-down,.fa-circle-arrow-down{--fa:"\f0ab"}.fa-globe{--fa:"\f0ac"}.fa-wrench{--fa:"\f0ad"}.fa-list-check,.fa-tasks{--fa:"\f0ae"}.fa-filter{--fa:"\f0b0"}.fa-briefcase{--fa:"\f0b1"}.fa-arrows-alt,.fa-up-down-left-right{--fa:"\f0b2"}.fa-users{--fa:"\f0c0"}.fa-chain,.fa-link{--fa:"\f0c1"}.fa-cloud{--fa:"\f0c2"}.fa-flask{--fa:"\f0c3"}.fa-cut,.fa-scissors{--fa:"\f0c4"}.fa-copy{--fa:"\f0c5"}.fa-paperclip{--fa:"\f0c6"}.fa-floppy-disk,.fa-save{--fa:"\f0c7"}.fa-square{--fa:"\f0c8"}.fa-bars,.fa-navicon{--fa:"\f0c9"}.fa-list-dots,.fa-list-ul{--fa:"\f0ca"}.fa-list-1-2,.fa-list-numeric,.fa-list-ol{--fa:"\f0cb"}.fa-strikethrough{--fa:"\f0cc"}.fa-underline{--fa:"\f0cd"}.fa-table{--fa:"\f0ce"}.fa-magic,.fa-wand-magic{--fa:"\f0d0"}.fa-truck{--fa:"\f0d1"}.fa-money-bill{--fa:"\f0d6"}.fa-caret-down{--fa:"\f0d7"}.fa-caret-up{--fa:"\f0d8"}.fa-caret-left{--fa:"\f0d9"}.fa-caret-right{--fa:"\f0da"}.fa-columns,.fa-table-columns{--fa:"\f0db"}.fa-sort,.fa-unsorted{--fa:"\f0dc"}.fa-sort-desc,.fa-sort-down{--fa:"\f0dd"}.fa-sort-asc,.fa-sort-up{--fa:"\f0de"}.fa-envelope{--fa:"\f0e0"}.fa-arrow-left-rotate,.fa-arrow-rotate-back,.fa-arrow-rotate-backward,.fa-arrow-rotate-left,.fa-undo{--fa:"\f0e2"}.fa-gavel,.fa-legal{--fa:"\f0e3"}.fa-bolt,.fa-zap{--fa:"\f0e7"}.fa-sitemap{--fa:"\f0e8"}.fa-umbrella{--fa:"\f0e9"}.fa-file-clipboard,.fa-paste{--fa:"\f0ea"}.fa-lightbulb{--fa:"\f0eb"}.fa-arrow-right-arrow-left,.fa-exchange{--fa:"\f0ec"}.fa-cloud-arrow-down,.fa-cloud-download,.fa-cloud-download-alt{--fa:"\f0ed"}.fa-cloud-arrow-up,.fa-cloud-upload,.fa-cloud-upload-alt{--fa:"\f0ee"}.fa-user-doctor,.fa-user-md{--fa:"\f0f0"}.fa-stethoscope{--fa:"\f0f1"}.fa-suitcase{--fa:"\f0f2"}.fa-bell{--fa:"\f0f3"}.fa-coffee,.fa-mug-saucer{--fa:"\f0f4"}.fa-hospital,.fa-hospital-alt,.fa-hospital-wide{--fa:"\f0f8"}.fa-ambulance,.fa-truck-medical{--fa:"\f0f9"}.fa-medkit,.fa-suitcase-medical{--fa:"\f0fa"}.fa-fighter-jet,.fa-jet-fighter{--fa:"\f0fb"}.fa-beer,.fa-beer-mug-empty{--fa:"\f0fc"}.fa-h-square,.fa-square-h{--fa:"\f0fd"}.fa-plus-square,.fa-square-plus{--fa:"\f0fe"}.fa-angle-double-left,.fa-angles-left{--fa:"\f100"}.fa-angle-double-right,.fa-angles-right{--fa:"\f101"}.fa-angle-double-up,.fa-angles-up{--fa:"\f102"}.fa-angle-double-down,.fa-angles-down{--fa:"\f103"}.fa-angle-left{--fa:"\f104"}.fa-angle-right{--fa:"\f105"}.fa-angle-up{--fa:"\f106"}.fa-angle-down{--fa:"\f107"}.fa-laptop{--fa:"\f109"}.fa-tablet-button{--fa:"\f10a"}.fa-mobile-button{--fa:"\f10b"}.fa-quote-left,.fa-quote-left-alt{--fa:"\f10d"}.fa-quote-right,.fa-quote-right-alt{--fa:"\f10e"}.fa-spinner{--fa:"\f110"}.fa-circle{--fa:"\f111"}.fa-face-smile,.fa-smile{--fa:"\f118"}.fa-face-frown,.fa-frown{--fa:"\f119"}.fa-face-meh,.fa-meh{--fa:"\f11a"}.fa-gamepad{--fa:"\f11b"}.fa-keyboard{--fa:"\f11c"}.fa-flag-checkered{--fa:"\f11e"}.fa-terminal{--fa:"\f120"}.fa-code{--fa:"\f121"}.fa-mail-reply-all,.fa-reply-all{--fa:"\f122"}.fa-location-arrow{--fa:"\f124"}.fa-crop{--fa:"\f125"}.fa-code-branch{--fa:"\f126"}.fa-chain-broken,.fa-chain-slash,.fa-link-slash,.fa-unlink{--fa:"\f127"}.fa-info{--fa:"\f129"}.fa-superscript{--fa:"\f12b"}.fa-subscript{--fa:"\f12c"}.fa-eraser{--fa:"\f12d"}.fa-puzzle-piece{--fa:"\f12e"}.fa-microphone{--fa:"\f130"}.fa-microphone-slash{--fa:"\f131"}.fa-shield,.fa-shield-blank{--fa:"\f132"}.fa-calendar{--fa:"\f133"}.fa-fire-extinguisher{--fa:"\f134"}.fa-rocket{--fa:"\f135"}.fa-chevron-circle-left,.fa-circle-chevron-left{--fa:"\f137"}.fa-chevron-circle-right,.fa-circle-chevron-right{--fa:"\f138"}.fa-chevron-circle-up,.fa-circle-chevron-up{--fa:"\f139"}.fa-chevron-circle-down,.fa-circle-chevron-down{--fa:"\f13a"}.fa-anchor{--fa:"\f13d"}.fa-unlock-alt,.fa-unlock-keyhole{--fa:"\f13e"}.fa-bullseye{--fa:"\f140"}.fa-ellipsis,.fa-ellipsis-h{--fa:"\f141"}.fa-ellipsis-v,.fa-ellipsis-vertical{--fa:"\f142"}.fa-rss-square,.fa-square-rss{--fa:"\f143"}.fa-circle-play,.fa-play-circle{--fa:"\f144"}.fa-ticket{--fa:"\f145"}.fa-minus-square,.fa-square-minus{--fa:"\f146"}.fa-arrow-turn-up,.fa-level-up{--fa:"\f148"}.fa-arrow-turn-down,.fa-level-down{--fa:"\f149"}.fa-check-square,.fa-square-check{--fa:"\f14a"}.fa-pen-square,.fa-pencil-square,.fa-square-pen{--fa:"\f14b"}.fa-external-link-square,.fa-square-arrow-up-right{--fa:"\f14c"}.fa-share-from-square,.fa-share-square{--fa:"\f14d"}.fa-compass{--fa:"\f14e"}.fa-caret-square-down,.fa-square-caret-down{--fa:"\f150"}.fa-caret-square-up,.fa-square-caret-up{--fa:"\f151"}.fa-caret-square-right,.fa-square-caret-right{--fa:"\f152"}.fa-eur,.fa-euro,.fa-euro-sign{--fa:"\f153"}.fa-gbp,.fa-pound-sign,.fa-sterling-sign{--fa:"\f154"}.fa-rupee,.fa-rupee-sign{--fa:"\f156"}.fa-cny,.fa-jpy,.fa-rmb,.fa-yen,.fa-yen-sign{--fa:"\f157"}.fa-rouble,.fa-rub,.fa-ruble,.fa-ruble-sign{--fa:"\f158"}.fa-krw,.fa-won,.fa-won-sign{--fa:"\f159"}.fa-file{--fa:"\f15b"}.fa-file-alt,.fa-file-lines,.fa-file-text{--fa:"\f15c"}.fa-arrow-down-a-z,.fa-sort-alpha-asc,.fa-sort-alpha-down{--fa:"\f15d"}.fa-arrow-up-a-z,.fa-sort-alpha-up{--fa:"\f15e"}.fa-arrow-down-wide-short,.fa-sort-amount-asc,.fa-sort-amount-down{--fa:"\f160"}.fa-arrow-up-wide-short,.fa-sort-amount-up{--fa:"\f161"}.fa-arrow-down-1-9,.fa-sort-numeric-asc,.fa-sort-numeric-down{--fa:"\f162"}.fa-arrow-up-1-9,.fa-sort-numeric-up{--fa:"\f163"}.fa-thumbs-up{--fa:"\f164"}.fa-thumbs-down{--fa:"\f165"}.fa-arrow-down-long,.fa-long-arrow-down{--fa:"\f175"}.fa-arrow-up-long,.fa-long-arrow-up{--fa:"\f176"}.fa-arrow-left-long,.fa-long-arrow-left{--fa:"\f177"}.fa-arrow-right-long,.fa-long-arrow-right{--fa:"\f178"}.fa-female,.fa-person-dress{--fa:"\f182"}.fa-male,.fa-person{--fa:"\f183"}.fa-sun{--fa:"\f185"}.fa-moon{--fa:"\f186"}.fa-archive,.fa-box-archive{--fa:"\f187"}.fa-bug{--fa:"\f188"}.fa-caret-square-left,.fa-square-caret-left{--fa:"\f191"}.fa-circle-dot,.fa-dot-circle{--fa:"\f192"}.fa-wheelchair{--fa:"\f193"}.fa-lira-sign{--fa:"\f195"}.fa-shuttle-space,.fa-space-shuttle{--fa:"\f197"}.fa-envelope-square,.fa-square-envelope{--fa:"\f199"}.fa-bank,.fa-building-columns,.fa-institution,.fa-museum,.fa-university{--fa:"\f19c"}.fa-graduation-cap,.fa-mortar-board{--fa:"\f19d"}.fa-language{--fa:"\f1ab"}.fa-fax{--fa:"\f1ac"}.fa-building{--fa:"\f1ad"}.fa-child{--fa:"\f1ae"}.fa-paw{--fa:"\f1b0"}.fa-cube{--fa:"\f1b2"}.fa-cubes{--fa:"\f1b3"}.fa-recycle{--fa:"\f1b8"}.fa-automobile,.fa-car{--fa:"\f1b9"}.fa-cab,.fa-taxi{--fa:"\f1ba"}.fa-tree{--fa:"\f1bb"}.fa-database{--fa:"\f1c0"}.fa-file-pdf{--fa:"\f1c1"}.fa-file-word{--fa:"\f1c2"}.fa-file-excel{--fa:"\f1c3"}.fa-file-powerpoint{--fa:"\f1c4"}.fa-file-image{--fa:"\f1c5"}.fa-file-archive,.fa-file-zipper{--fa:"\f1c6"}.fa-file-audio{--fa:"\f1c7"}.fa-file-video{--fa:"\f1c8"}.fa-file-code{--fa:"\f1c9"}.fa-life-ring{--fa:"\f1cd"}.fa-circle-notch{--fa:"\f1ce"}.fa-paper-plane{--fa:"\f1d8"}.fa-clock-rotate-left,.fa-history{--fa:"\f1da"}.fa-header,.fa-heading{--fa:"\f1dc"}.fa-paragraph{--fa:"\f1dd"}.fa-sliders,.fa-sliders-h{--fa:"\f1de"}.fa-share-alt,.fa-share-nodes{--fa:"\f1e0"}.fa-share-alt-square,.fa-square-share-nodes{--fa:"\f1e1"}.fa-bomb{--fa:"\f1e2"}.fa-futbol,.fa-futbol-ball,.fa-soccer-ball{--fa:"\f1e3"}.fa-teletype,.fa-tty{--fa:"\f1e4"}.fa-binoculars{--fa:"\f1e5"}.fa-plug{--fa:"\f1e6"}.fa-newspaper{--fa:"\f1ea"}.fa-wifi,.fa-wifi-3,.fa-wifi-strong{--fa:"\f1eb"}.fa-calculator{--fa:"\f1ec"}.fa-bell-slash{--fa:"\f1f6"}.fa-trash{--fa:"\f1f8"}.fa-copyright{--fa:"\f1f9"}.fa-eye-dropper,.fa-eye-dropper-empty,.fa-eyedropper{--fa:"\f1fb"}.fa-paint-brush,.fa-paintbrush{--fa:"\f1fc"}.fa-birthday-cake,.fa-cake,.fa-cake-candles{--fa:"\f1fd"}.fa-area-chart,.fa-chart-area{--fa:"\f1fe"}.fa-chart-pie,.fa-pie-chart{--fa:"\f200"}.fa-chart-line,.fa-line-chart{--fa:"\f201"}.fa-toggle-off{--fa:"\f204"}.fa-toggle-on{--fa:"\f205"}.fa-bicycle{--fa:"\f206"}.fa-bus{--fa:"\f207"}.fa-closed-captioning{--fa:"\f20a"}.fa-ils,.fa-shekel,.fa-shekel-sign,.fa-sheqel,.fa-sheqel-sign{--fa:"\f20b"}.fa-cart-plus{--fa:"\f217"}.fa-cart-arrow-down{--fa:"\f218"}.fa-diamond{--fa:"\f219"}.fa-ship{--fa:"\f21a"}.fa-user-secret{--fa:"\f21b"}.fa-motorcycle{--fa:"\f21c"}.fa-street-view{--fa:"\f21d"}.fa-heart-pulse,.fa-heartbeat{--fa:"\f21e"}.fa-venus{--fa:"\f221"}.fa-mars{--fa:"\f222"}.fa-mercury{--fa:"\f223"}.fa-mars-and-venus{--fa:"\f224"}.fa-transgender,.fa-transgender-alt{--fa:"\f225"}.fa-venus-double{--fa:"\f226"}.fa-mars-double{--fa:"\f227"}.fa-venus-mars{--fa:"\f228"}.fa-mars-stroke{--fa:"\f229"}.fa-mars-stroke-up,.fa-mars-stroke-v{--fa:"\f22a"}.fa-mars-stroke-h,.fa-mars-stroke-right{--fa:"\f22b"}.fa-neuter{--fa:"\f22c"}.fa-genderless{--fa:"\f22d"}.fa-server{--fa:"\f233"}.fa-user-plus{--fa:"\f234"}.fa-user-times,.fa-user-xmark{--fa:"\f235"}.fa-bed{--fa:"\f236"}.fa-train{--fa:"\f238"}.fa-subway,.fa-train-subway{--fa:"\f239"}.fa-battery,.fa-battery-5,.fa-battery-full{--fa:"\f240"}.fa-battery-4,.fa-battery-three-quarters{--fa:"\f241"}.fa-battery-3,.fa-battery-half{--fa:"\f242"}.fa-battery-2,.fa-battery-quarter{--fa:"\f243"}.fa-battery-0,.fa-battery-empty{--fa:"\f244"}.fa-arrow-pointer,.fa-mouse-pointer{--fa:"\f245"}.fa-i-cursor{--fa:"\f246"}.fa-object-group{--fa:"\f247"}.fa-object-ungroup{--fa:"\f248"}.fa-note-sticky,.fa-sticky-note{--fa:"\f249"}.fa-clone{--fa:"\f24d"}.fa-balance-scale,.fa-scale-balanced{--fa:"\f24e"}.fa-hourglass-1,.fa-hourglass-start{--fa:"\f251"}.fa-hourglass-2,.fa-hourglass-half{--fa:"\f252"}.fa-hourglass-3,.fa-hourglass-end{--fa:"\f253"}.fa-hourglass,.fa-hourglass-empty{--fa:"\f254"}.fa-hand-back-fist,.fa-hand-rock{--fa:"\f255"}.fa-hand,.fa-hand-paper{--fa:"\f256"}.fa-hand-scissors{--fa:"\f257"}.fa-hand-lizard{--fa:"\f258"}.fa-hand-spock{--fa:"\f259"}.fa-hand-pointer{--fa:"\f25a"}.fa-hand-peace{--fa:"\f25b"}.fa-trademark{--fa:"\f25c"}.fa-registered{--fa:"\f25d"}.fa-television,.fa-tv,.fa-tv-alt{--fa:"\f26c"}.fa-calendar-plus{--fa:"\f271"}.fa-calendar-minus{--fa:"\f272"}.fa-calendar-times,.fa-calendar-xmark{--fa:"\f273"}.fa-calendar-check{--fa:"\f274"}.fa-industry{--fa:"\f275"}.fa-map-pin{--fa:"\f276"}.fa-map-signs,.fa-signs-post{--fa:"\f277"}.fa-map{--fa:"\f279"}.fa-comment-alt,.fa-message{--fa:"\f27a"}.fa-circle-pause,.fa-pause-circle{--fa:"\f28b"}.fa-circle-stop,.fa-stop-circle{--fa:"\f28d"}.fa-bag-shopping,.fa-shopping-bag{--fa:"\f290"}.fa-basket-shopping,.fa-shopping-basket{--fa:"\f291"}.fa-universal-access{--fa:"\f29a"}.fa-blind,.fa-person-walking-with-cane{--fa:"\f29d"}.fa-audio-description{--fa:"\f29e"}.fa-phone-volume,.fa-volume-control-phone{--fa:"\f2a0"}.fa-braille{--fa:"\f2a1"}.fa-assistive-listening-systems,.fa-ear-listen{--fa:"\f2a2"}.fa-american-sign-language-interpreting,.fa-asl-interpreting,.fa-hands-american-sign-language-interpreting,.fa-hands-asl-interpreting{--fa:"\f2a3"}.fa-deaf,.fa-deafness,.fa-ear-deaf,.fa-hard-of-hearing{--fa:"\f2a4"}.fa-hands,.fa-sign-language,.fa-signing{--fa:"\f2a7"}.fa-eye-low-vision,.fa-low-vision{--fa:"\f2a8"}.fa-font-awesome,.fa-font-awesome-flag,.fa-font-awesome-logo-full{--fa:"\f2b4"}.fa-handshake,.fa-handshake-alt,.fa-handshake-simple{--fa:"\f2b5"}.fa-envelope-open{--fa:"\f2b6"}.fa-address-book,.fa-contact-book{--fa:"\f2b9"}.fa-address-card,.fa-contact-card,.fa-vcard{--fa:"\f2bb"}.fa-circle-user,.fa-user-circle{--fa:"\f2bd"}.fa-id-badge{--fa:"\f2c1"}.fa-drivers-license,.fa-id-card{--fa:"\f2c2"}.fa-temperature-4,.fa-temperature-full,.fa-thermometer-4,.fa-thermometer-full{--fa:"\f2c7"}.fa-temperature-3,.fa-temperature-three-quarters,.fa-thermometer-3,.fa-thermometer-three-quarters{--fa:"\f2c8"}.fa-temperature-2,.fa-temperature-half,.fa-thermometer-2,.fa-thermometer-half{--fa:"\f2c9"}.fa-temperature-1,.fa-temperature-quarter,.fa-thermometer-1,.fa-thermometer-quarter{--fa:"\f2ca"}.fa-temperature-0,.fa-temperature-empty,.fa-thermometer-0,.fa-thermometer-empty{--fa:"\f2cb"}.fa-shower{--fa:"\f2cc"}.fa-bath,.fa-bathtub{--fa:"\f2cd"}.fa-podcast{--fa:"\f2ce"}.fa-window-maximize{--fa:"\f2d0"}.fa-window-minimize{--fa:"\f2d1"}.fa-window-restore{--fa:"\f2d2"}.fa-square-xmark,.fa-times-square,.fa-xmark-square{--fa:"\f2d3"}.fa-microchip{--fa:"\f2db"}.fa-snowflake{--fa:"\f2dc"}.fa-spoon,.fa-utensil-spoon{--fa:"\f2e5"}.fa-cutlery,.fa-utensils{--fa:"\f2e7"}.fa-rotate-back,.fa-rotate-backward,.fa-rotate-left,.fa-undo-alt{--fa:"\f2ea"}.fa-trash-alt,.fa-trash-can{--fa:"\f2ed"}.fa-rotate,.fa-sync-alt{--fa:"\f2f1"}.fa-stopwatch{--fa:"\f2f2"}.fa-right-from-bracket,.fa-sign-out-alt{--fa:"\f2f5"}.fa-right-to-bracket,.fa-sign-in-alt{--fa:"\f2f6"}.fa-redo-alt,.fa-rotate-forward,.fa-rotate-right{--fa:"\f2f9"}.fa-poo{--fa:"\f2fe"}.fa-images{--fa:"\f302"}.fa-pencil,.fa-pencil-alt{--fa:"\f303"}.fa-pen{--fa:"\f304"}.fa-pen-alt,.fa-pen-clip{--fa:"\f305"}.fa-octagon{--fa:"\f306"}.fa-down-long,.fa-long-arrow-alt-down{--fa:"\f309"}.fa-left-long,.fa-long-arrow-alt-left{--fa:"\f30a"}.fa-long-arrow-alt-right,.fa-right-long{--fa:"\f30b"}.fa-long-arrow-alt-up,.fa-up-long{--fa:"\f30c"}.fa-hexagon{--fa:"\f312"}.fa-file-edit,.fa-file-pen{--fa:"\f31c"}.fa-expand-arrows-alt,.fa-maximize{--fa:"\f31e"}.fa-clipboard{--fa:"\f328"}.fa-arrows-alt-h,.fa-left-right{--fa:"\f337"}.fa-arrows-alt-v,.fa-up-down{--fa:"\f338"}.fa-alarm-clock{--fa:"\f34e"}.fa-arrow-alt-circle-down,.fa-circle-down{--fa:"\f358"}.fa-arrow-alt-circle-left,.fa-circle-left{--fa:"\f359"}.fa-arrow-alt-circle-right,.fa-circle-right{--fa:"\f35a"}.fa-arrow-alt-circle-up,.fa-circle-up{--fa:"\f35b"}.fa-external-link-alt,.fa-up-right-from-square{--fa:"\f35d"}.fa-external-link-square-alt,.fa-square-up-right{--fa:"\f360"}.fa-exchange-alt,.fa-right-left{--fa:"\f362"}.fa-repeat{--fa:"\f363"}.fa-code-commit{--fa:"\f386"}.fa-code-merge{--fa:"\f387"}.fa-desktop,.fa-desktop-alt{--fa:"\f390"}.fa-gem{--fa:"\f3a5"}.fa-level-down-alt,.fa-turn-down{--fa:"\f3be"}.fa-level-up-alt,.fa-turn-up{--fa:"\f3bf"}.fa-lock-open{--fa:"\f3c1"}.fa-location-dot,.fa-map-marker-alt{--fa:"\f3c5"}.fa-microphone-alt,.fa-microphone-lines{--fa:"\f3c9"}.fa-mobile-alt,.fa-mobile-screen-button{--fa:"\f3cd"}.fa-mobile,.fa-mobile-android,.fa-mobile-phone{--fa:"\f3ce"}.fa-mobile-android-alt,.fa-mobile-screen{--fa:"\f3cf"}.fa-money-bill-1,.fa-money-bill-alt{--fa:"\f3d1"}.fa-phone-slash{--fa:"\f3dd"}.fa-image-portrait,.fa-portrait{--fa:"\f3e0"}.fa-mail-reply,.fa-reply{--fa:"\f3e5"}.fa-shield-alt,.fa-shield-halved{--fa:"\f3ed"}.fa-tablet-alt,.fa-tablet-screen-button{--fa:"\f3fa"}.fa-tablet,.fa-tablet-android{--fa:"\f3fb"}.fa-ticket-alt,.fa-ticket-simple{--fa:"\f3ff"}.fa-rectangle-times,.fa-rectangle-xmark,.fa-times-rectangle,.fa-window-close{--fa:"\f410"}.fa-compress-alt,.fa-down-left-and-up-right-to-center{--fa:"\f422"}.fa-expand-alt,.fa-up-right-and-down-left-from-center{--fa:"\f424"}.fa-baseball-bat-ball{--fa:"\f432"}.fa-baseball,.fa-baseball-ball{--fa:"\f433"}.fa-basketball,.fa-basketball-ball{--fa:"\f434"}.fa-bowling-ball{--fa:"\f436"}.fa-chess{--fa:"\f439"}.fa-chess-bishop{--fa:"\f43a"}.fa-chess-board{--fa:"\f43c"}.fa-chess-king{--fa:"\f43f"}.fa-chess-knight{--fa:"\f441"}.fa-chess-pawn{--fa:"\f443"}.fa-chess-queen{--fa:"\f445"}.fa-chess-rook{--fa:"\f447"}.fa-dumbbell{--fa:"\f44b"}.fa-football,.fa-football-ball{--fa:"\f44e"}.fa-golf-ball,.fa-golf-ball-tee{--fa:"\f450"}.fa-hockey-puck{--fa:"\f453"}.fa-broom-ball,.fa-quidditch,.fa-quidditch-broom-ball{--fa:"\f458"}.fa-square-full{--fa:"\f45c"}.fa-ping-pong-paddle-ball,.fa-table-tennis,.fa-table-tennis-paddle-ball{--fa:"\f45d"}.fa-volleyball,.fa-volleyball-ball{--fa:"\f45f"}.fa-allergies,.fa-hand-dots{--fa:"\f461"}.fa-band-aid,.fa-bandage{--fa:"\f462"}.fa-box{--fa:"\f466"}.fa-boxes,.fa-boxes-alt,.fa-boxes-stacked{--fa:"\f468"}.fa-briefcase-medical{--fa:"\f469"}.fa-burn,.fa-fire-flame-simple{--fa:"\f46a"}.fa-capsules{--fa:"\f46b"}.fa-clipboard-check{--fa:"\f46c"}.fa-clipboard-list{--fa:"\f46d"}.fa-diagnoses,.fa-person-dots-from-line{--fa:"\f470"}.fa-dna{--fa:"\f471"}.fa-dolly,.fa-dolly-box{--fa:"\f472"}.fa-cart-flatbed,.fa-dolly-flatbed{--fa:"\f474"}.fa-file-medical{--fa:"\f477"}.fa-file-medical-alt,.fa-file-waveform{--fa:"\f478"}.fa-first-aid,.fa-kit-medical{--fa:"\f479"}.fa-circle-h,.fa-hospital-symbol{--fa:"\f47e"}.fa-id-card-alt,.fa-id-card-clip{--fa:"\f47f"}.fa-notes-medical{--fa:"\f481"}.fa-pallet{--fa:"\f482"}.fa-pills{--fa:"\f484"}.fa-prescription-bottle{--fa:"\f485"}.fa-prescription-bottle-alt,.fa-prescription-bottle-medical{--fa:"\f486"}.fa-bed-pulse,.fa-procedures{--fa:"\f487"}.fa-shipping-fast,.fa-truck-fast{--fa:"\f48b"}.fa-smoking{--fa:"\f48d"}.fa-syringe{--fa:"\f48e"}.fa-tablets{--fa:"\f490"}.fa-thermometer{--fa:"\f491"}.fa-vial{--fa:"\f492"}.fa-vials{--fa:"\f493"}.fa-warehouse{--fa:"\f494"}.fa-weight,.fa-weight-scale{--fa:"\f496"}.fa-x-ray{--fa:"\f497"}.fa-box-open{--fa:"\f49e"}.fa-comment-dots,.fa-commenting{--fa:"\f4ad"}.fa-comment-slash{--fa:"\f4b3"}.fa-couch{--fa:"\f4b8"}.fa-circle-dollar-to-slot,.fa-donate{--fa:"\f4b9"}.fa-dove{--fa:"\f4ba"}.fa-hand-holding{--fa:"\f4bd"}.fa-hand-holding-heart{--fa:"\f4be"}.fa-hand-holding-dollar,.fa-hand-holding-usd{--fa:"\f4c0"}.fa-hand-holding-droplet,.fa-hand-holding-water{--fa:"\f4c1"}.fa-hands-holding{--fa:"\f4c2"}.fa-hands-helping,.fa-handshake-angle{--fa:"\f4c4"}.fa-parachute-box{--fa:"\f4cd"}.fa-people-carry,.fa-people-carry-box{--fa:"\f4ce"}.fa-piggy-bank{--fa:"\f4d3"}.fa-ribbon{--fa:"\f4d6"}.fa-route{--fa:"\f4d7"}.fa-seedling,.fa-sprout{--fa:"\f4d8"}.fa-sign,.fa-sign-hanging{--fa:"\f4d9"}.fa-face-smile-wink,.fa-smile-wink{--fa:"\f4da"}.fa-tape{--fa:"\f4db"}.fa-truck-loading,.fa-truck-ramp-box{--fa:"\f4de"}.fa-truck-moving{--fa:"\f4df"}.fa-video-slash{--fa:"\f4e2"}.fa-wine-glass{--fa:"\f4e3"}.fa-user-astronaut{--fa:"\f4fb"}.fa-user-check{--fa:"\f4fc"}.fa-user-clock{--fa:"\f4fd"}.fa-user-cog,.fa-user-gear{--fa:"\f4fe"}.fa-user-edit,.fa-user-pen{--fa:"\f4ff"}.fa-user-friends,.fa-user-group{--fa:"\f500"}.fa-user-graduate{--fa:"\f501"}.fa-user-lock{--fa:"\f502"}.fa-user-minus{--fa:"\f503"}.fa-user-ninja{--fa:"\f504"}.fa-user-shield{--fa:"\f505"}.fa-user-alt-slash,.fa-user-large-slash,.fa-user-slash{--fa:"\f506"}.fa-user-tag{--fa:"\f507"}.fa-user-tie{--fa:"\f508"}.fa-users-cog,.fa-users-gear{--fa:"\f509"}.fa-balance-scale-left,.fa-scale-unbalanced{--fa:"\f515"}.fa-balance-scale-right,.fa-scale-unbalanced-flip{--fa:"\f516"}.fa-blender{--fa:"\f517"}.fa-book-open{--fa:"\f518"}.fa-broadcast-tower,.fa-tower-broadcast{--fa:"\f519"}.fa-broom{--fa:"\f51a"}.fa-blackboard,.fa-chalkboard{--fa:"\f51b"}.fa-chalkboard-teacher,.fa-chalkboard-user{--fa:"\f51c"}.fa-church{--fa:"\f51d"}.fa-coins{--fa:"\f51e"}.fa-compact-disc{--fa:"\f51f"}.fa-crow{--fa:"\f520"}.fa-crown{--fa:"\f521"}.fa-dice{--fa:"\f522"}.fa-dice-five{--fa:"\f523"}.fa-dice-four{--fa:"\f524"}.fa-dice-one{--fa:"\f525"}.fa-dice-six{--fa:"\f526"}.fa-dice-three{--fa:"\f527"}.fa-dice-two{--fa:"\f528"}.fa-divide{--fa:"\f529"}.fa-door-closed{--fa:"\f52a"}.fa-door-open{--fa:"\f52b"}.fa-feather{--fa:"\f52d"}.fa-frog{--fa:"\f52e"}.fa-gas-pump{--fa:"\f52f"}.fa-glasses{--fa:"\f530"}.fa-greater-than-equal{--fa:"\f532"}.fa-helicopter{--fa:"\f533"}.fa-infinity{--fa:"\f534"}.fa-kiwi-bird{--fa:"\f535"}.fa-less-than-equal{--fa:"\f537"}.fa-memory{--fa:"\f538"}.fa-microphone-alt-slash,.fa-microphone-lines-slash{--fa:"\f539"}.fa-money-bill-wave{--fa:"\f53a"}.fa-money-bill-1-wave,.fa-money-bill-wave-alt{--fa:"\f53b"}.fa-money-check{--fa:"\f53c"}.fa-money-check-alt,.fa-money-check-dollar{--fa:"\f53d"}.fa-not-equal{--fa:"\f53e"}.fa-palette{--fa:"\f53f"}.fa-parking,.fa-square-parking{--fa:"\f540"}.fa-diagram-project,.fa-project-diagram{--fa:"\f542"}.fa-receipt{--fa:"\f543"}.fa-robot{--fa:"\f544"}.fa-ruler{--fa:"\f545"}.fa-ruler-combined{--fa:"\f546"}.fa-ruler-horizontal{--fa:"\f547"}.fa-ruler-vertical{--fa:"\f548"}.fa-school{--fa:"\f549"}.fa-screwdriver{--fa:"\f54a"}.fa-shoe-prints{--fa:"\f54b"}.fa-skull{--fa:"\f54c"}.fa-ban-smoking,.fa-smoking-ban{--fa:"\f54d"}.fa-store{--fa:"\f54e"}.fa-shop,.fa-store-alt{--fa:"\f54f"}.fa-bars-staggered,.fa-reorder,.fa-stream{--fa:"\f550"}.fa-stroopwafel{--fa:"\f551"}.fa-toolbox{--fa:"\f552"}.fa-shirt,.fa-t-shirt,.fa-tshirt{--fa:"\f553"}.fa-person-walking,.fa-walking{--fa:"\f554"}.fa-wallet{--fa:"\f555"}.fa-angry,.fa-face-angry{--fa:"\f556"}.fa-archway{--fa:"\f557"}.fa-atlas,.fa-book-atlas{--fa:"\f558"}.fa-award{--fa:"\f559"}.fa-backspace,.fa-delete-left{--fa:"\f55a"}.fa-bezier-curve{--fa:"\f55b"}.fa-bong{--fa:"\f55c"}.fa-brush{--fa:"\f55d"}.fa-bus-alt,.fa-bus-simple{--fa:"\f55e"}.fa-cannabis{--fa:"\f55f"}.fa-check-double{--fa:"\f560"}.fa-cocktail,.fa-martini-glass-citrus{--fa:"\f561"}.fa-bell-concierge,.fa-concierge-bell{--fa:"\f562"}.fa-cookie{--fa:"\f563"}.fa-cookie-bite{--fa:"\f564"}.fa-crop-alt,.fa-crop-simple{--fa:"\f565"}.fa-digital-tachograph,.fa-tachograph-digital{--fa:"\f566"}.fa-dizzy,.fa-face-dizzy{--fa:"\f567"}.fa-compass-drafting,.fa-drafting-compass{--fa:"\f568"}.fa-drum{--fa:"\f569"}.fa-drum-steelpan{--fa:"\f56a"}.fa-feather-alt,.fa-feather-pointed{--fa:"\f56b"}.fa-file-contract{--fa:"\f56c"}.fa-file-arrow-down,.fa-file-download{--fa:"\f56d"}.fa-arrow-right-from-file,.fa-file-export{--fa:"\f56e"}.fa-arrow-right-to-file,.fa-file-import{--fa:"\f56f"}.fa-file-invoice{--fa:"\f570"}.fa-file-invoice-dollar{--fa:"\f571"}.fa-file-prescription{--fa:"\f572"}.fa-file-signature{--fa:"\f573"}.fa-file-arrow-up,.fa-file-upload{--fa:"\f574"}.fa-fill{--fa:"\f575"}.fa-fill-drip{--fa:"\f576"}.fa-fingerprint{--fa:"\f577"}.fa-fish{--fa:"\f578"}.fa-face-flushed,.fa-flushed{--fa:"\f579"}.fa-face-frown-open,.fa-frown-open{--fa:"\f57a"}.fa-glass-martini-alt,.fa-martini-glass{--fa:"\f57b"}.fa-earth-africa,.fa-globe-africa{--fa:"\f57c"}.fa-earth,.fa-earth-america,.fa-earth-americas,.fa-globe-americas{--fa:"\f57d"}.fa-earth-asia,.fa-globe-asia{--fa:"\f57e"}.fa-face-grimace,.fa-grimace{--fa:"\f57f"}.fa-face-grin,.fa-grin{--fa:"\f580"}.fa-face-grin-wide,.fa-grin-alt{--fa:"\f581"}.fa-face-grin-beam,.fa-grin-beam{--fa:"\f582"}.fa-face-grin-beam-sweat,.fa-grin-beam-sweat{--fa:"\f583"}.fa-face-grin-hearts,.fa-grin-hearts{--fa:"\f584"}.fa-face-grin-squint,.fa-grin-squint{--fa:"\f585"}.fa-face-grin-squint-tears,.fa-grin-squint-tears{--fa:"\f586"}.fa-face-grin-stars,.fa-grin-stars{--fa:"\f587"}.fa-face-grin-tears,.fa-grin-tears{--fa:"\f588"}.fa-face-grin-tongue,.fa-grin-tongue{--fa:"\f589"}.fa-face-grin-tongue-squint,.fa-grin-tongue-squint{--fa:"\f58a"}.fa-face-grin-tongue-wink,.fa-grin-tongue-wink{--fa:"\f58b"}.fa-face-grin-wink,.fa-grin-wink{--fa:"\f58c"}.fa-grid-horizontal,.fa-grip,.fa-grip-horizontal{--fa:"\f58d"}.fa-grid-vertical,.fa-grip-vertical{--fa:"\f58e"}.fa-headset{--fa:"\f590"}.fa-highlighter{--fa:"\f591"}.fa-hot-tub,.fa-hot-tub-person{--fa:"\f593"}.fa-hotel{--fa:"\f594"}.fa-joint{--fa:"\f595"}.fa-face-kiss,.fa-kiss{--fa:"\f596"}.fa-face-kiss-beam,.fa-kiss-beam{--fa:"\f597"}.fa-face-kiss-wink-heart,.fa-kiss-wink-heart{--fa:"\f598"}.fa-face-laugh,.fa-laugh{--fa:"\f599"}.fa-face-laugh-beam,.fa-laugh-beam{--fa:"\f59a"}.fa-face-laugh-squint,.fa-laugh-squint{--fa:"\f59b"}.fa-face-laugh-wink,.fa-laugh-wink{--fa:"\f59c"}.fa-cart-flatbed-suitcase,.fa-luggage-cart{--fa:"\f59d"}.fa-map-location,.fa-map-marked{--fa:"\f59f"}.fa-map-location-dot,.fa-map-marked-alt{--fa:"\f5a0"}.fa-marker{--fa:"\f5a1"}.fa-medal{--fa:"\f5a2"}.fa-face-meh-blank,.fa-meh-blank{--fa:"\f5a4"}.fa-face-rolling-eyes,.fa-meh-rolling-eyes{--fa:"\f5a5"}.fa-monument{--fa:"\f5a6"}.fa-mortar-pestle{--fa:"\f5a7"}.fa-paint-roller{--fa:"\f5aa"}.fa-passport{--fa:"\f5ab"}.fa-pen-fancy{--fa:"\f5ac"}.fa-pen-nib{--fa:"\f5ad"}.fa-pen-ruler,.fa-pencil-ruler{--fa:"\f5ae"}.fa-plane-arrival{--fa:"\f5af"}.fa-plane-departure{--fa:"\f5b0"}.fa-prescription{--fa:"\f5b1"}.fa-face-sad-cry,.fa-sad-cry{--fa:"\f5b3"}.fa-face-sad-tear,.fa-sad-tear{--fa:"\f5b4"}.fa-shuttle-van,.fa-van-shuttle{--fa:"\f5b6"}.fa-signature{--fa:"\f5b7"}.fa-face-smile-beam,.fa-smile-beam{--fa:"\f5b8"}.fa-solar-panel{--fa:"\f5ba"}.fa-spa{--fa:"\f5bb"}.fa-splotch{--fa:"\f5bc"}.fa-spray-can{--fa:"\f5bd"}.fa-stamp{--fa:"\f5bf"}.fa-star-half-alt,.fa-star-half-stroke{--fa:"\f5c0"}.fa-suitcase-rolling{--fa:"\f5c1"}.fa-face-surprise,.fa-surprise{--fa:"\f5c2"}.fa-swatchbook{--fa:"\f5c3"}.fa-person-swimming,.fa-swimmer{--fa:"\f5c4"}.fa-ladder-water,.fa-swimming-pool,.fa-water-ladder{--fa:"\f5c5"}.fa-droplet-slash,.fa-tint-slash{--fa:"\f5c7"}.fa-face-tired,.fa-tired{--fa:"\f5c8"}.fa-tooth{--fa:"\f5c9"}.fa-umbrella-beach{--fa:"\f5ca"}.fa-weight-hanging{--fa:"\f5cd"}.fa-wine-glass-alt,.fa-wine-glass-empty{--fa:"\f5ce"}.fa-air-freshener,.fa-spray-can-sparkles{--fa:"\f5d0"}.fa-apple-alt,.fa-apple-whole{--fa:"\f5d1"}.fa-atom{--fa:"\f5d2"}.fa-bone{--fa:"\f5d7"}.fa-book-open-reader,.fa-book-reader{--fa:"\f5da"}.fa-brain{--fa:"\f5dc"}.fa-car-alt,.fa-car-rear{--fa:"\f5de"}.fa-battery-car,.fa-car-battery{--fa:"\f5df"}.fa-car-burst,.fa-car-crash{--fa:"\f5e1"}.fa-car-side{--fa:"\f5e4"}.fa-charging-station{--fa:"\f5e7"}.fa-diamond-turn-right,.fa-directions{--fa:"\f5eb"}.fa-draw-polygon,.fa-vector-polygon{--fa:"\f5ee"}.fa-laptop-code{--fa:"\f5fc"}.fa-layer-group{--fa:"\f5fd"}.fa-location,.fa-location-crosshairs{--fa:"\f601"}.fa-lungs{--fa:"\f604"}.fa-microscope{--fa:"\f610"}.fa-oil-can{--fa:"\f613"}.fa-poop{--fa:"\f619"}.fa-shapes,.fa-triangle-circle-square{--fa:"\f61f"}.fa-star-of-life{--fa:"\f621"}.fa-dashboard,.fa-gauge,.fa-gauge-med,.fa-tachometer-alt-average{--fa:"\f624"}.fa-gauge-high,.fa-tachometer-alt,.fa-tachometer-alt-fast{--fa:"\f625"}.fa-gauge-simple,.fa-gauge-simple-med,.fa-tachometer-average{--fa:"\f629"}.fa-gauge-simple-high,.fa-tachometer,.fa-tachometer-fast{--fa:"\f62a"}.fa-teeth{--fa:"\f62e"}.fa-teeth-open{--fa:"\f62f"}.fa-masks-theater,.fa-theater-masks{--fa:"\f630"}.fa-traffic-light{--fa:"\f637"}.fa-truck-monster{--fa:"\f63b"}.fa-truck-pickup{--fa:"\f63c"}.fa-ad,.fa-rectangle-ad{--fa:"\f641"}.fa-ankh{--fa:"\f644"}.fa-bible,.fa-book-bible{--fa:"\f647"}.fa-briefcase-clock,.fa-business-time{--fa:"\f64a"}.fa-city{--fa:"\f64f"}.fa-comment-dollar{--fa:"\f651"}.fa-comments-dollar{--fa:"\f653"}.fa-cross{--fa:"\f654"}.fa-dharmachakra{--fa:"\f655"}.fa-envelope-open-text{--fa:"\f658"}.fa-folder-minus{--fa:"\f65d"}.fa-folder-plus{--fa:"\f65e"}.fa-filter-circle-dollar,.fa-funnel-dollar{--fa:"\f662"}.fa-gopuram{--fa:"\f664"}.fa-hamsa{--fa:"\f665"}.fa-bahai,.fa-haykal{--fa:"\f666"}.fa-jedi{--fa:"\f669"}.fa-book-journal-whills,.fa-journal-whills{--fa:"\f66a"}.fa-kaaba{--fa:"\f66b"}.fa-khanda{--fa:"\f66d"}.fa-landmark{--fa:"\f66f"}.fa-envelopes-bulk,.fa-mail-bulk{--fa:"\f674"}.fa-menorah{--fa:"\f676"}.fa-mosque{--fa:"\f678"}.fa-om{--fa:"\f679"}.fa-pastafarianism,.fa-spaghetti-monster-flying{--fa:"\f67b"}.fa-peace{--fa:"\f67c"}.fa-place-of-worship{--fa:"\f67f"}.fa-poll,.fa-square-poll-vertical{--fa:"\f681"}.fa-poll-h,.fa-square-poll-horizontal{--fa:"\f682"}.fa-person-praying,.fa-pray{--fa:"\f683"}.fa-hands-praying,.fa-praying-hands{--fa:"\f684"}.fa-book-quran,.fa-quran{--fa:"\f687"}.fa-magnifying-glass-dollar,.fa-search-dollar{--fa:"\f688"}.fa-magnifying-glass-location,.fa-search-location{--fa:"\f689"}.fa-socks{--fa:"\f696"}.fa-square-root-alt,.fa-square-root-variable{--fa:"\f698"}.fa-star-and-crescent{--fa:"\f699"}.fa-star-of-david{--fa:"\f69a"}.fa-synagogue{--fa:"\f69b"}.fa-scroll-torah,.fa-torah{--fa:"\f6a0"}.fa-torii-gate{--fa:"\f6a1"}.fa-vihara{--fa:"\f6a7"}.fa-volume-mute,.fa-volume-times,.fa-volume-xmark{--fa:"\f6a9"}.fa-yin-yang{--fa:"\f6ad"}.fa-blender-phone{--fa:"\f6b6"}.fa-book-dead,.fa-book-skull{--fa:"\f6b7"}.fa-campground{--fa:"\f6bb"}.fa-cat{--fa:"\f6be"}.fa-chair{--fa:"\f6c0"}.fa-cloud-moon{--fa:"\f6c3"}.fa-cloud-sun{--fa:"\f6c4"}.fa-cow{--fa:"\f6c8"}.fa-dice-d20{--fa:"\f6cf"}.fa-dice-d6{--fa:"\f6d1"}.fa-dog{--fa:"\f6d3"}.fa-dragon{--fa:"\f6d5"}.fa-drumstick-bite{--fa:"\f6d7"}.fa-dungeon{--fa:"\f6d9"}.fa-file-csv{--fa:"\f6dd"}.fa-fist-raised,.fa-hand-fist{--fa:"\f6de"}.fa-ghost{--fa:"\f6e2"}.fa-hammer{--fa:"\f6e3"}.fa-hanukiah{--fa:"\f6e6"}.fa-hat-wizard{--fa:"\f6e8"}.fa-hiking,.fa-person-hiking{--fa:"\f6ec"}.fa-hippo{--fa:"\f6ed"}.fa-horse{--fa:"\f6f0"}.fa-house-chimney-crack,.fa-house-damage{--fa:"\f6f1"}.fa-hryvnia,.fa-hryvnia-sign{--fa:"\f6f2"}.fa-mask{--fa:"\f6fa"}.fa-mountain{--fa:"\f6fc"}.fa-network-wired{--fa:"\f6ff"}.fa-otter{--fa:"\f700"}.fa-ring{--fa:"\f70b"}.fa-person-running,.fa-running{--fa:"\f70c"}.fa-scroll{--fa:"\f70e"}.fa-skull-crossbones{--fa:"\f714"}.fa-slash{--fa:"\f715"}.fa-spider{--fa:"\f717"}.fa-toilet-paper,.fa-toilet-paper-alt,.fa-toilet-paper-blank{--fa:"\f71e"}.fa-tractor{--fa:"\f722"}.fa-user-injured{--fa:"\f728"}.fa-vr-cardboard{--fa:"\f729"}.fa-wand-sparkles{--fa:"\f72b"}.fa-wind{--fa:"\f72e"}.fa-wine-bottle{--fa:"\f72f"}.fa-cloud-meatball{--fa:"\f73b"}.fa-cloud-moon-rain{--fa:"\f73c"}.fa-cloud-rain{--fa:"\f73d"}.fa-cloud-showers-heavy{--fa:"\f740"}.fa-cloud-sun-rain{--fa:"\f743"}.fa-democrat{--fa:"\f747"}.fa-flag-usa{--fa:"\f74d"}.fa-hurricane{--fa:"\f751"}.fa-landmark-alt,.fa-landmark-dome{--fa:"\f752"}.fa-meteor{--fa:"\f753"}.fa-person-booth{--fa:"\f756"}.fa-poo-bolt,.fa-poo-storm{--fa:"\f75a"}.fa-rainbow{--fa:"\f75b"}.fa-republican{--fa:"\f75e"}.fa-smog{--fa:"\f75f"}.fa-temperature-high{--fa:"\f769"}.fa-temperature-low{--fa:"\f76b"}.fa-cloud-bolt,.fa-thunderstorm{--fa:"\f76c"}.fa-tornado{--fa:"\f76f"}.fa-volcano{--fa:"\f770"}.fa-check-to-slot,.fa-vote-yea{--fa:"\f772"}.fa-water{--fa:"\f773"}.fa-baby{--fa:"\f77c"}.fa-baby-carriage,.fa-carriage-baby{--fa:"\f77d"}.fa-biohazard{--fa:"\f780"}.fa-blog{--fa:"\f781"}.fa-calendar-day{--fa:"\f783"}.fa-calendar-week{--fa:"\f784"}.fa-candy-cane{--fa:"\f786"}.fa-carrot{--fa:"\f787"}.fa-cash-register{--fa:"\f788"}.fa-compress-arrows-alt,.fa-minimize{--fa:"\f78c"}.fa-dumpster{--fa:"\f793"}.fa-dumpster-fire{--fa:"\f794"}.fa-ethernet{--fa:"\f796"}.fa-gifts{--fa:"\f79c"}.fa-champagne-glasses,.fa-glass-cheers{--fa:"\f79f"}.fa-glass-whiskey,.fa-whiskey-glass{--fa:"\f7a0"}.fa-earth-europe,.fa-globe-europe{--fa:"\f7a2"}.fa-grip-lines{--fa:"\f7a4"}.fa-grip-lines-vertical{--fa:"\f7a5"}.fa-guitar{--fa:"\f7a6"}.fa-heart-broken,.fa-heart-crack{--fa:"\f7a9"}.fa-holly-berry{--fa:"\f7aa"}.fa-horse-head{--fa:"\f7ab"}.fa-icicles{--fa:"\f7ad"}.fa-igloo{--fa:"\f7ae"}.fa-mitten{--fa:"\f7b5"}.fa-mug-hot{--fa:"\f7b6"}.fa-radiation{--fa:"\f7b9"}.fa-circle-radiation,.fa-radiation-alt{--fa:"\f7ba"}.fa-restroom{--fa:"\f7bd"}.fa-satellite{--fa:"\f7bf"}.fa-satellite-dish{--fa:"\f7c0"}.fa-sd-card{--fa:"\f7c2"}.fa-sim-card{--fa:"\f7c4"}.fa-person-skating,.fa-skating{--fa:"\f7c5"}.fa-person-skiing,.fa-skiing{--fa:"\f7c9"}.fa-person-skiing-nordic,.fa-skiing-nordic{--fa:"\f7ca"}.fa-sleigh{--fa:"\f7cc"}.fa-comment-sms,.fa-sms{--fa:"\f7cd"}.fa-person-snowboarding,.fa-snowboarding{--fa:"\f7ce"}.fa-snowman{--fa:"\f7d0"}.fa-snowplow{--fa:"\f7d2"}.fa-tenge,.fa-tenge-sign{--fa:"\f7d7"}.fa-toilet{--fa:"\f7d8"}.fa-screwdriver-wrench,.fa-tools{--fa:"\f7d9"}.fa-cable-car,.fa-tram{--fa:"\f7da"}.fa-fire-alt,.fa-fire-flame-curved{--fa:"\f7e4"}.fa-bacon{--fa:"\f7e5"}.fa-book-medical{--fa:"\f7e6"}.fa-bread-slice{--fa:"\f7ec"}.fa-cheese{--fa:"\f7ef"}.fa-clinic-medical,.fa-house-chimney-medical{--fa:"\f7f2"}.fa-clipboard-user{--fa:"\f7f3"}.fa-comment-medical{--fa:"\f7f5"}.fa-crutch{--fa:"\f7f7"}.fa-disease{--fa:"\f7fa"}.fa-egg{--fa:"\f7fb"}.fa-folder-tree{--fa:"\f802"}.fa-burger,.fa-hamburger{--fa:"\f805"}.fa-hand-middle-finger{--fa:"\f806"}.fa-hard-hat,.fa-hat-hard,.fa-helmet-safety{--fa:"\f807"}.fa-hospital-user{--fa:"\f80d"}.fa-hotdog{--fa:"\f80f"}.fa-ice-cream{--fa:"\f810"}.fa-laptop-medical{--fa:"\f812"}.fa-pager{--fa:"\f815"}.fa-pepper-hot{--fa:"\f816"}.fa-pizza-slice{--fa:"\f818"}.fa-sack-dollar{--fa:"\f81d"}.fa-book-tanakh,.fa-tanakh{--fa:"\f827"}.fa-bars-progress,.fa-tasks-alt{--fa:"\f828"}.fa-trash-arrow-up,.fa-trash-restore{--fa:"\f829"}.fa-trash-can-arrow-up,.fa-trash-restore-alt{--fa:"\f82a"}.fa-user-nurse{--fa:"\f82f"}.fa-wave-square{--fa:"\f83e"}.fa-biking,.fa-person-biking{--fa:"\f84a"}.fa-border-all{--fa:"\f84c"}.fa-border-none{--fa:"\f850"}.fa-border-style,.fa-border-top-left{--fa:"\f853"}.fa-digging,.fa-person-digging{--fa:"\f85e"}.fa-fan{--fa:"\f863"}.fa-heart-music-camera-bolt,.fa-icons{--fa:"\f86d"}.fa-phone-alt,.fa-phone-flip{--fa:"\f879"}.fa-phone-square-alt,.fa-square-phone-flip{--fa:"\f87b"}.fa-photo-film,.fa-photo-video{--fa:"\f87c"}.fa-remove-format,.fa-text-slash{--fa:"\f87d"}.fa-arrow-down-z-a,.fa-sort-alpha-desc,.fa-sort-alpha-down-alt{--fa:"\f881"}.fa-arrow-up-z-a,.fa-sort-alpha-up-alt{--fa:"\f882"}.fa-arrow-down-short-wide,.fa-sort-amount-desc,.fa-sort-amount-down-alt{--fa:"\f884"}.fa-arrow-up-short-wide,.fa-sort-amount-up-alt{--fa:"\f885"}.fa-arrow-down-9-1,.fa-sort-numeric-desc,.fa-sort-numeric-down-alt{--fa:"\f886"}.fa-arrow-up-9-1,.fa-sort-numeric-up-alt{--fa:"\f887"}.fa-spell-check{--fa:"\f891"}.fa-voicemail{--fa:"\f897"}.fa-hat-cowboy{--fa:"\f8c0"}.fa-hat-cowboy-side{--fa:"\f8c1"}.fa-computer-mouse,.fa-mouse{--fa:"\f8cc"}.fa-radio{--fa:"\f8d7"}.fa-record-vinyl{--fa:"\f8d9"}.fa-walkie-talkie{--fa:"\f8ef"}.fa-caravan{--fa:"\f8ff"}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\regular.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:root, :host {
  --fa-family-classic: "Font Awesome 7 Free";
  --fa-font-regular: normal 400 1em/1 var(--fa-family-classic);
  /* deprecated: this older custom property will be removed next major release */
  --fa-style-family-classic: var(--fa-family-classic);
}

@font-face {
  font-family: "Font Awesome 7 Free";
  font-style: normal;
  font-weight: 400;
  font-display: block;
  src: url("../webfonts/fa-regular-400.woff2");
}
.far {
  --fa-family: var(--fa-family-classic);
  --fa-style: 400;
}

.fa-classic {
  --fa-family: var(--fa-family-classic);
}

.fa-regular {
  --fa-style: 400;
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\regular.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:host,:root{--fa-family-classic:"Font Awesome 7 Free";--fa-font-regular:normal 400 1em/1 var(--fa-family-classic);--fa-style-family-classic:var(--fa-family-classic)}@font-face{font-family:"Font Awesome 7 Free";font-style:normal;font-weight:400;font-display:block;src:url(../webfonts/fa-regular-400.woff2)}.far{--fa-style:400}.fa-classic,.far{--fa-family:var(--fa-family-classic)}.fa-regular{--fa-style:400}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\solid.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:root, :host {
  --fa-family-classic: "Font Awesome 7 Free";
  --fa-font-solid: normal 900 1em/1 var(--fa-family-classic);
  /* deprecated: this older custom property will be removed next major release */
  --fa-style-family-classic: var(--fa-family-classic);
}

@font-face {
  font-family: "Font Awesome 7 Free";
  font-style: normal;
  font-weight: 900;
  font-display: block;
  src: url("../webfonts/fa-solid-900.woff2");
}
.fas {
  --fa-family: var(--fa-family-classic);
  --fa-style: 900;
}

.fa-classic {
  --fa-family: var(--fa-family-classic);
}

.fa-solid {
  --fa-style: 900;
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\solid.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:host,:root{--fa-family-classic:"Font Awesome 7 Free";--fa-font-solid:normal 900 1em/1 var(--fa-family-classic);--fa-style-family-classic:var(--fa-family-classic)}@font-face{font-family:"Font Awesome 7 Free";font-style:normal;font-weight:900;font-display:block;src:url(../webfonts/fa-solid-900.woff2)}.fas{--fa-style:900}.fa-classic,.fas{--fa-family:var(--fa-family-classic)}.fa-solid{--fa-style:900}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\svg-with-js.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:root, :host {
  --fa-font-solid: normal 900 1em/1 "Font Awesome 7 Free";
  --fa-font-regular: normal 400 1em/1 "Font Awesome 7 Free";
  --fa-font-light: normal 300 1em/1 "Font Awesome 7 Pro";
  --fa-font-thin: normal 100 1em/1 "Font Awesome 7 Pro";
  --fa-font-duotone: normal 900 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-regular: normal 400 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-light: normal 300 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-thin: normal 100 1em/1 "Font Awesome 7 Duotone";
  --fa-font-brands: normal 400 1em/1 "Font Awesome 7 Brands";
  --fa-font-sharp-solid: normal 900 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-regular: normal 400 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-light: normal 300 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-thin: normal 100 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-duotone-solid: normal 900 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-regular: normal 400 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-light: normal 300 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-thin: normal 100 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-slab-regular: normal 400 1em/1 "Font Awesome 7 Slab";
  --fa-font-slab-press-regular: normal 400 1em/1 "Font Awesome 7 Slab Press";
  --fa-font-whiteboard-semibold: normal 600 1em/1 "Font Awesome 7 Whiteboard";
  --fa-font-thumbprint-light: normal 300 1em/1 "Font Awesome 7 Thumbprint";
  --fa-font-notdog-solid: normal 900 1em/1 "Font Awesome 7 Notdog";
  --fa-font-notdog-duo-solid: normal 900 1em/1 "Font Awesome 7 Notdog Duo";
  --fa-font-etch-solid: normal 900 1em/1 "Font Awesome 7 Etch";
  --fa-font-jelly-regular: normal 400 1em/1 "Font Awesome 7 Jelly";
  --fa-font-jelly-fill-regular: normal 400 1em/1 "Font Awesome 7 Jelly Fill";
  --fa-font-jelly-duo-regular: normal 400 1em/1 "Font Awesome 7 Jelly Duo";
  --fa-font-chisel-regular: normal 400 1em/1 "Font Awesome 7 Chisel";
  --fa-font-utility-semibold: normal 600 1em/1 "Font Awesome 7 Utility";
  --fa-font-utility-duo-semibold: normal 600 1em/1 "Font Awesome 7 Utility Duo";
  --fa-font-utility-fill-semibold: normal 600 1em/1 "Font Awesome 7 Utility Fill";
}

.svg-inline--fa {
  box-sizing: content-box;
  display: var(--fa-display, inline-block);
  height: 1em;
  overflow: visible;
  vertical-align: -0.125em;
  width: var(--fa-width, 1.25em);
}
.svg-inline--fa.fa-2xs {
  vertical-align: 0.1em;
}
.svg-inline--fa.fa-xs {
  vertical-align: 0em;
}
.svg-inline--fa.fa-sm {
  vertical-align: -0.0714285714em;
}
.svg-inline--fa.fa-lg {
  vertical-align: -0.2em;
}
.svg-inline--fa.fa-xl {
  vertical-align: -0.25em;
}
.svg-inline--fa.fa-2xl {
  vertical-align: -0.3125em;
}
.svg-inline--fa.fa-pull-left,
.svg-inline--fa .fa-pull-start {
  float: inline-start;
  margin-inline-end: var(--fa-pull-margin, 0.3em);
}
.svg-inline--fa.fa-pull-right,
.svg-inline--fa .fa-pull-end {
  float: inline-end;
  margin-inline-start: var(--fa-pull-margin, 0.3em);
}
.svg-inline--fa.fa-li {
  width: var(--fa-li-width, 2em);
  inset-inline-start: calc(-1 * var(--fa-li-width, 2em));
  inset-block-start: 0.25em; /* syncing vertical alignment with Web Font rendering */
}

.fa-layers-counter, .fa-layers-text {
  display: inline-block;
  position: absolute;
  text-align: center;
}

.fa-layers {
  display: inline-block;
  height: 1em;
  position: relative;
  text-align: center;
  vertical-align: -0.125em;
  width: var(--fa-width, 1.25em);
}
.fa-layers .svg-inline--fa {
  inset: 0;
  margin: auto;
  position: absolute;
  transform-origin: center center;
}

.fa-layers-text {
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  transform-origin: center center;
}

.fa-layers-counter {
  background-color: var(--fa-counter-background-color, #ff253a);
  border-radius: var(--fa-counter-border-radius, 1em);
  box-sizing: border-box;
  color: var(--fa-inverse, #fff);
  line-height: var(--fa-counter-line-height, 1);
  max-width: var(--fa-counter-max-width, 5em);
  min-width: var(--fa-counter-min-width, 1.5em);
  overflow: hidden;
  padding: var(--fa-counter-padding, 0.25em 0.5em);
  right: var(--fa-right, 0);
  text-overflow: ellipsis;
  top: var(--fa-top, 0);
  transform: scale(var(--fa-counter-scale, 0.25));
  transform-origin: top right;
}

.fa-layers-bottom-right {
  bottom: var(--fa-bottom, 0);
  right: var(--fa-right, 0);
  top: auto;
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: bottom right;
}

.fa-layers-bottom-left {
  bottom: var(--fa-bottom, 0);
  left: var(--fa-left, 0);
  right: auto;
  top: auto;
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: bottom left;
}

.fa-layers-top-right {
  top: var(--fa-top, 0);
  right: var(--fa-right, 0);
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: top right;
}

.fa-layers-top-left {
  left: var(--fa-left, 0);
  right: auto;
  top: var(--fa-top, 0);
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: top left;
}

.fa-1x {
  font-size: 1em;
}

.fa-2x {
  font-size: 2em;
}

.fa-3x {
  font-size: 3em;
}

.fa-4x {
  font-size: 4em;
}

.fa-5x {
  font-size: 5em;
}

.fa-6x {
  font-size: 6em;
}

.fa-7x {
  font-size: 7em;
}

.fa-8x {
  font-size: 8em;
}

.fa-9x {
  font-size: 9em;
}

.fa-10x {
  font-size: 10em;
}

.fa-2xs {
  font-size: calc(10 / 16 * 1em); /* converts a 10px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 10 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 10 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xs {
  font-size: calc(12 / 16 * 1em); /* converts a 12px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 12 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 12 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-sm {
  font-size: calc(14 / 16 * 1em); /* converts a 14px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 14 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 14 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-lg {
  font-size: calc(20 / 16 * 1em); /* converts a 20px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 20 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 20 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-xl {
  font-size: calc(24 / 16 * 1em); /* converts a 24px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 24 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 24 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-2xl {
  font-size: calc(32 / 16 * 1em); /* converts a 32px size into an em-based value that's relative to the scale's 16px base */
  line-height: calc(1 / 32 * 1em); /* sets the line-height of the icon back to that of it's parent */
  vertical-align: calc((6 / 32 - 0.375) * 1em); /* vertically centers the icon taking into account the surrounding text's descender */
}

.fa-width-auto {
  --fa-width: auto;
}

.fa-fw,
.fa-width-fixed {
  --fa-width: 1.25em;
}

.fa-ul {
  list-style-type: none;
  margin-inline-start: var(--fa-li-margin, 2.5em);
  padding-inline-start: 0;
}
.fa-ul > li {
  position: relative;
}

.fa-li {
  inset-inline-start: calc(-1 * var(--fa-li-width, 2em));
  position: absolute;
  text-align: center;
  width: var(--fa-li-width, 2em);
  line-height: inherit;
}

/* Heads Up: Bordered Icons will not be supported in the future!
  - This feature will be deprecated in the next major release of Font Awesome (v8)!
  - You may continue to use it in this version *v7), but it will not be supported in Font Awesome v8.
*/
/* Notes:
* --@{v.$css-prefix}-border-width = 1/16 by default (to render as ~1px based on a 16px default font-size)
* --@{v.$css-prefix}-border-padding =
  ** 3/16 for vertical padding (to give ~2px of vertical whitespace around an icon considering it's vertical alignment)
  ** 4/16 for horizontal padding (to give ~4px of horizontal whitespace around an icon)
*/
.fa-border {
  border-color: var(--fa-border-color, #eee);
  border-radius: var(--fa-border-radius, 0.1em);
  border-style: var(--fa-border-style, solid);
  border-width: var(--fa-border-width, 0.0625em);
  box-sizing: var(--fa-border-box-sizing, content-box);
  padding: var(--fa-border-padding, 0.1875em 0.25em);
}

.fa-pull-left,
.fa-pull-start {
  float: inline-start;
  margin-inline-end: var(--fa-pull-margin, 0.3em);
}

.fa-pull-right,
.fa-pull-end {
  float: inline-end;
  margin-inline-start: var(--fa-pull-margin, 0.3em);
}

.fa-beat {
  animation-name: fa-beat;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-bounce {
  animation-name: fa-bounce;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.28, 0.84, 0.42, 1));
}

.fa-fade {
  animation-name: fa-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-beat-fade {
  animation-name: fa-beat-fade;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, cubic-bezier(0.4, 0, 0.6, 1));
}

.fa-flip {
  animation-name: fa-flip;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, ease-in-out);
}

.fa-shake {
  animation-name: fa-shake;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin {
  animation-name: fa-spin;
  animation-delay: var(--fa-animation-delay, 0s);
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 2s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, linear);
}

.fa-spin-reverse {
  --fa-animation-direction: reverse;
}

.fa-pulse,
.fa-spin-pulse {
  animation-name: fa-spin;
  animation-direction: var(--fa-animation-direction, normal);
  animation-duration: var(--fa-animation-duration, 1s);
  animation-iteration-count: var(--fa-animation-iteration-count, infinite);
  animation-timing-function: var(--fa-animation-timing, steps(8));
}

@media (prefers-reduced-motion: reduce) {
  .fa-beat,
  .fa-bounce,
  .fa-fade,
  .fa-beat-fade,
  .fa-flip,
  .fa-pulse,
  .fa-shake,
  .fa-spin,
  .fa-spin-pulse {
    animation: none !important;
    transition: none !important;
  }
}
@keyframes fa-beat {
  0%, 90% {
    transform: scale(1);
  }
  45% {
    transform: scale(var(--fa-beat-scale, 1.25));
  }
}
@keyframes fa-bounce {
  0% {
    transform: scale(1, 1) translateY(0);
  }
  10% {
    transform: scale(var(--fa-bounce-start-scale-x, 1.1), var(--fa-bounce-start-scale-y, 0.9)) translateY(0);
  }
  30% {
    transform: scale(var(--fa-bounce-jump-scale-x, 0.9), var(--fa-bounce-jump-scale-y, 1.1)) translateY(var(--fa-bounce-height, -0.5em));
  }
  50% {
    transform: scale(var(--fa-bounce-land-scale-x, 1.05), var(--fa-bounce-land-scale-y, 0.95)) translateY(0);
  }
  57% {
    transform: scale(1, 1) translateY(var(--fa-bounce-rebound, -0.125em));
  }
  64% {
    transform: scale(1, 1) translateY(0);
  }
  100% {
    transform: scale(1, 1) translateY(0);
  }
}
@keyframes fa-fade {
  50% {
    opacity: var(--fa-fade-opacity, 0.4);
  }
}
@keyframes fa-beat-fade {
  0%, 100% {
    opacity: var(--fa-beat-fade-opacity, 0.4);
    transform: scale(1);
  }
  50% {
    opacity: 1;
    transform: scale(var(--fa-beat-fade-scale, 1.125));
  }
}
@keyframes fa-flip {
  50% {
    transform: rotate3d(var(--fa-flip-x, 0), var(--fa-flip-y, 1), var(--fa-flip-z, 0), var(--fa-flip-angle, -180deg));
  }
}
@keyframes fa-shake {
  0% {
    transform: rotate(-15deg);
  }
  4% {
    transform: rotate(15deg);
  }
  8%, 24% {
    transform: rotate(-18deg);
  }
  12%, 28% {
    transform: rotate(18deg);
  }
  16% {
    transform: rotate(-22deg);
  }
  20% {
    transform: rotate(22deg);
  }
  32% {
    transform: rotate(-12deg);
  }
  36% {
    transform: rotate(12deg);
  }
  40%, 100% {
    transform: rotate(0deg);
  }
}
@keyframes fa-spin {
  0% {
    transform: rotate(0deg);
  }
  100% {
    transform: rotate(360deg);
  }
}
.fa-rotate-90 {
  transform: rotate(90deg);
}

.fa-rotate-180 {
  transform: rotate(180deg);
}

.fa-rotate-270 {
  transform: rotate(270deg);
}

.fa-flip-horizontal {
  transform: scale(-1, 1);
}

.fa-flip-vertical {
  transform: scale(1, -1);
}

.fa-flip-both,
.fa-flip-horizontal.fa-flip-vertical {
  transform: scale(-1, -1);
}

.fa-rotate-by {
  transform: rotate(var(--fa-rotate-angle, 0));
}

.svg-inline--fa .fa-primary {
  fill: var(--fa-primary-color, currentColor);
  opacity: var(--fa-primary-opacity, 1);
}

.svg-inline--fa .fa-secondary {
  fill: var(--fa-secondary-color, currentColor);
  opacity: var(--fa-secondary-opacity, 0.4);
}

.svg-inline--fa.fa-swap-opacity .fa-primary {
  opacity: var(--fa-secondary-opacity, 0.4);
}

.svg-inline--fa.fa-swap-opacity .fa-secondary {
  opacity: var(--fa-primary-opacity, 1);
}

.svg-inline--fa mask .fa-primary,
.svg-inline--fa mask .fa-secondary {
  fill: black;
}

.svg-inline--fa.fa-inverse {
  fill: var(--fa-inverse, #fff);
}

.fa-stack {
  display: inline-block;
  height: 2em;
  line-height: 2em;
  position: relative;
  vertical-align: middle;
  width: 2.5em;
}

.fa-inverse {
  color: var(--fa-inverse, #fff);
}

.svg-inline--fa.fa-stack-1x {
  --fa-width: 1.25em;
  height: 1em;
  width: var(--fa-width);
}
.svg-inline--fa.fa-stack-2x {
  --fa-width: 2.5em;
  height: 2em;
  width: var(--fa-width);
}

.fa-stack-1x,
.fa-stack-2x {
  inset: 0;
  margin: auto;
  position: absolute;
  z-index: var(--fa-stack-z-index, auto);
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\svg-with-js.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:host,:root{--fa-font-solid:normal 900 1em/1 "Font Awesome 7 Free";--fa-font-regular:normal 400 1em/1 "Font Awesome 7 Free";--fa-font-light:normal 300 1em/1 "Font Awesome 7 Pro";--fa-font-thin:normal 100 1em/1 "Font Awesome 7 Pro";--fa-font-duotone:normal 900 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-regular:normal 400 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-light:normal 300 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-thin:normal 100 1em/1 "Font Awesome 7 Duotone";--fa-font-brands:normal 400 1em/1 "Font Awesome 7 Brands";--fa-font-sharp-solid:normal 900 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-regular:normal 400 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-light:normal 300 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-thin:normal 100 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-duotone-solid:normal 900 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-regular:normal 400 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-light:normal 300 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-thin:normal 100 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-slab-regular:normal 400 1em/1 "Font Awesome 7 Slab";--fa-font-slab-press-regular:normal 400 1em/1 "Font Awesome 7 Slab Press";--fa-font-whiteboard-semibold:normal 600 1em/1 "Font Awesome 7 Whiteboard";--fa-font-thumbprint-light:normal 300 1em/1 "Font Awesome 7 Thumbprint";--fa-font-notdog-solid:normal 900 1em/1 "Font Awesome 7 Notdog";--fa-font-notdog-duo-solid:normal 900 1em/1 "Font Awesome 7 Notdog Duo";--fa-font-etch-solid:normal 900 1em/1 "Font Awesome 7 Etch";--fa-font-jelly-regular:normal 400 1em/1 "Font Awesome 7 Jelly";--fa-font-jelly-fill-regular:normal 400 1em/1 "Font Awesome 7 Jelly Fill";--fa-font-jelly-duo-regular:normal 400 1em/1 "Font Awesome 7 Jelly Duo";--fa-font-chisel-regular:normal 400 1em/1 "Font Awesome 7 Chisel";--fa-font-utility-semibold:normal 600 1em/1 "Font Awesome 7 Utility";--fa-font-utility-duo-semibold:normal 600 1em/1 "Font Awesome 7 Utility Duo";--fa-font-utility-fill-semibold:normal 600 1em/1 "Font Awesome 7 Utility Fill"}.svg-inline--fa{box-sizing:content-box;display:var(--fa-display,inline-block);height:1em;overflow:visible;vertical-align:-.125em;width:var(--fa-width,1.25em)}.svg-inline--fa.fa-2xs{vertical-align:.1em}.svg-inline--fa.fa-xs{vertical-align:0}.svg-inline--fa.fa-sm{vertical-align:-.0714285714em}.svg-inline--fa.fa-lg{vertical-align:-.2em}.svg-inline--fa.fa-xl{vertical-align:-.25em}.svg-inline--fa.fa-2xl{vertical-align:-.3125em}.svg-inline--fa.fa-pull-left,.svg-inline--fa .fa-pull-start{float:inline-start;margin-inline-end:var(--fa-pull-margin,.3em)}.svg-inline--fa .fa-pull-end,.svg-inline--fa.fa-pull-right{float:inline-end;margin-inline-start:var(--fa-pull-margin,.3em)}.svg-inline--fa.fa-li{width:var(--fa-li-width,2em);inset-inline-start:calc(var(--fa-li-width, 2em)*-1);inset-block-start:.25em}.fa-layers-counter,.fa-layers-text{display:inline-block;position:absolute;text-align:center}.fa-layers{display:inline-block;height:1em;position:relative;text-align:center;vertical-align:-.125em;width:var(--fa-width,1.25em)}.fa-layers .svg-inline--fa{inset:0;margin:auto;position:absolute;transform-origin:center center}.fa-layers-text{left:50%;top:50%;transform:translate(-50%,-50%);transform-origin:center center}.fa-layers-counter{background-color:var(--fa-counter-background-color,#ff253a);border-radius:var(--fa-counter-border-radius,1em);box-sizing:border-box;color:var(--fa-inverse,#fff);line-height:var(--fa-counter-line-height,1);max-width:var(--fa-counter-max-width,5em);min-width:var(--fa-counter-min-width,1.5em);overflow:hidden;padding:var(--fa-counter-padding,.25em .5em);right:var(--fa-right,0);text-overflow:ellipsis;top:var(--fa-top,0);transform:scale(var(--fa-counter-scale,.25));transform-origin:top right}.fa-layers-bottom-right{bottom:var(--fa-bottom,0);right:var(--fa-right,0);top:auto;transform:scale(var(--fa-layers-scale,.25));transform-origin:bottom right}.fa-layers-bottom-left{bottom:var(--fa-bottom,0);left:var(--fa-left,0);right:auto;top:auto;transform:scale(var(--fa-layers-scale,.25));transform-origin:bottom left}.fa-layers-top-right{top:var(--fa-top,0);right:var(--fa-right,0);transform:scale(var(--fa-layers-scale,.25));transform-origin:top right}.fa-layers-top-left{left:var(--fa-left,0);right:auto;top:var(--fa-top,0);transform:scale(var(--fa-layers-scale,.25));transform-origin:top left}.fa-1x{font-size:1em}.fa-2x{font-size:2em}.fa-3x{font-size:3em}.fa-4x{font-size:4em}.fa-5x{font-size:5em}.fa-6x{font-size:6em}.fa-7x{font-size:7em}.fa-8x{font-size:8em}.fa-9x{font-size:9em}.fa-10x{font-size:10em}.fa-2xs{font-size:.625em;line-height:.1em;vertical-align:.225em}.fa-xs{font-size:.75em;line-height:.08333em;vertical-align:.125em}.fa-sm{font-size:.875em;line-height:.07143em;vertical-align:.05357em}.fa-lg{font-size:1.25em;line-height:.05em;vertical-align:-.075em}.fa-xl{font-size:1.5em;line-height:.04167em;vertical-align:-.125em}.fa-2xl{font-size:2em;line-height:.03125em;vertical-align:-.1875em}.fa-width-auto{--fa-width:auto}.fa-fw,.fa-width-fixed{--fa-width:1.25em}.fa-ul{list-style-type:none;margin-inline-start:var(--fa-li-margin,2.5em);padding-inline-start:0}.fa-ul>li{position:relative}.fa-li{inset-inline-start:calc(var(--fa-li-width, 2em)*-1);position:absolute;text-align:center;width:var(--fa-li-width,2em);line-height:inherit}.fa-border{border-radius:var(--fa-border-radius,.1em);border:var(--fa-border-width,.0625em) var(--fa-border-style,solid) var(--fa-border-color,#eee);box-sizing:var(--fa-border-box-sizing,content-box);padding:var(--fa-border-padding,.1875em .25em)}.fa-pull-left,.fa-pull-start{float:inline-start;margin-inline-end:var(--fa-pull-margin,.3em)}.fa-pull-end,.fa-pull-right{float:inline-end;margin-inline-start:var(--fa-pull-margin,.3em)}.fa-beat{animation-name:fa-beat;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-bounce{animation-name:fa-bounce;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.28,.84,.42,1))}.fa-fade{animation-name:fa-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-beat-fade,.fa-fade{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s)}.fa-beat-fade{animation-name:fa-beat-fade;animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,cubic-bezier(.4,0,.6,1))}.fa-flip{animation-name:fa-flip;animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,ease-in-out)}.fa-shake{animation-name:fa-shake;animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-shake,.fa-spin{animation-delay:var(--fa-animation-delay,0s);animation-direction:var(--fa-animation-direction,normal)}.fa-spin{animation-name:fa-spin;animation-duration:var(--fa-animation-duration,2s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,linear)}.fa-spin-reverse{--fa-animation-direction:reverse}.fa-pulse,.fa-spin-pulse{animation-name:fa-spin;animation-direction:var(--fa-animation-direction,normal);animation-duration:var(--fa-animation-duration,1s);animation-iteration-count:var(--fa-animation-iteration-count,infinite);animation-timing-function:var(--fa-animation-timing,steps(8))}@media (prefers-reduced-motion:reduce){.fa-beat,.fa-beat-fade,.fa-bounce,.fa-fade,.fa-flip,.fa-pulse,.fa-shake,.fa-spin,.fa-spin-pulse{animation:none!important;transition:none!important}}@keyframes fa-beat{0%,90%{transform:scale(1)}45%{transform:scale(var(--fa-beat-scale,1.25))}}@keyframes fa-bounce{0%{transform:scale(1) translateY(0)}10%{transform:scale(var(--fa-bounce-start-scale-x,1.1),var(--fa-bounce-start-scale-y,.9)) translateY(0)}30%{transform:scale(var(--fa-bounce-jump-scale-x,.9),var(--fa-bounce-jump-scale-y,1.1)) translateY(var(--fa-bounce-height,-.5em))}50%{transform:scale(var(--fa-bounce-land-scale-x,1.05),var(--fa-bounce-land-scale-y,.95)) translateY(0)}57%{transform:scale(1) translateY(var(--fa-bounce-rebound,-.125em))}64%{transform:scale(1) translateY(0)}to{transform:scale(1) translateY(0)}}@keyframes fa-fade{50%{opacity:var(--fa-fade-opacity,.4)}}@keyframes fa-beat-fade{0%,to{opacity:var(--fa-beat-fade-opacity,.4);transform:scale(1)}50%{opacity:1;transform:scale(var(--fa-beat-fade-scale,1.125))}}@keyframes fa-flip{50%{transform:rotate3d(var(--fa-flip-x,0),var(--fa-flip-y,1),var(--fa-flip-z,0),var(--fa-flip-angle,-180deg))}}@keyframes fa-shake{0%{transform:rotate(-15deg)}4%{transform:rotate(15deg)}8%,24%{transform:rotate(-18deg)}12%,28%{transform:rotate(18deg)}16%{transform:rotate(-22deg)}20%{transform:rotate(22deg)}32%{transform:rotate(-12deg)}36%{transform:rotate(12deg)}40%,to{transform:rotate(0deg)}}@keyframes fa-spin{0%{transform:rotate(0deg)}to{transform:rotate(1turn)}}.fa-rotate-90{transform:rotate(90deg)}.fa-rotate-180{transform:rotate(180deg)}.fa-rotate-270{transform:rotate(270deg)}.fa-flip-horizontal{transform:scaleX(-1)}.fa-flip-vertical{transform:scaleY(-1)}.fa-flip-both,.fa-flip-horizontal.fa-flip-vertical{transform:scale(-1)}.fa-rotate-by{transform:rotate(var(--fa-rotate-angle,0))}.svg-inline--fa .fa-primary{fill:var(--fa-primary-color,currentColor);opacity:var(--fa-primary-opacity,1)}.svg-inline--fa .fa-secondary{fill:var(--fa-secondary-color,currentColor)}.svg-inline--fa .fa-secondary,.svg-inline--fa.fa-swap-opacity .fa-primary{opacity:var(--fa-secondary-opacity,.4)}.svg-inline--fa.fa-swap-opacity .fa-secondary{opacity:var(--fa-primary-opacity,1)}.svg-inline--fa mask .fa-primary,.svg-inline--fa mask .fa-secondary{fill:#000}.svg-inline--fa.fa-inverse{fill:var(--fa-inverse,#fff)}.fa-stack{display:inline-block;height:2em;line-height:2em;position:relative;vertical-align:middle;width:2.5em}.fa-inverse{color:var(--fa-inverse,#fff)}.svg-inline--fa.fa-stack-1x{--fa-width:1.25em;height:1em;width:var(--fa-width)}.svg-inline--fa.fa-stack-2x{--fa-width:2.5em;height:2em;width:var(--fa-width)}.fa-stack-1x,.fa-stack-2x{inset:0;margin:auto;position:absolute;z-index:var(--fa-stack-z-index,auto)}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\svg.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:root, :host {
  --fa-font-solid: normal 900 1em/1 "Font Awesome 7 Free";
  --fa-font-regular: normal 400 1em/1 "Font Awesome 7 Free";
  --fa-font-light: normal 300 1em/1 "Font Awesome 7 Pro";
  --fa-font-thin: normal 100 1em/1 "Font Awesome 7 Pro";
  --fa-font-duotone: normal 900 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-regular: normal 400 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-light: normal 300 1em/1 "Font Awesome 7 Duotone";
  --fa-font-duotone-thin: normal 100 1em/1 "Font Awesome 7 Duotone";
  --fa-font-brands: normal 400 1em/1 "Font Awesome 7 Brands";
  --fa-font-sharp-solid: normal 900 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-regular: normal 400 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-light: normal 300 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-thin: normal 100 1em/1 "Font Awesome 7 Sharp";
  --fa-font-sharp-duotone-solid: normal 900 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-regular: normal 400 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-light: normal 300 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-sharp-duotone-thin: normal 100 1em/1 "Font Awesome 7 Sharp Duotone";
  --fa-font-slab-regular: normal 400 1em/1 "Font Awesome 7 Slab";
  --fa-font-slab-press-regular: normal 400 1em/1 "Font Awesome 7 Slab Press";
  --fa-font-whiteboard-semibold: normal 600 1em/1 "Font Awesome 7 Whiteboard";
  --fa-font-thumbprint-light: normal 300 1em/1 "Font Awesome 7 Thumbprint";
  --fa-font-notdog-solid: normal 900 1em/1 "Font Awesome 7 Notdog";
  --fa-font-notdog-duo-solid: normal 900 1em/1 "Font Awesome 7 Notdog Duo";
  --fa-font-etch-solid: normal 900 1em/1 "Font Awesome 7 Etch";
  --fa-font-jelly-regular: normal 400 1em/1 "Font Awesome 7 Jelly";
  --fa-font-jelly-fill-regular: normal 400 1em/1 "Font Awesome 7 Jelly Fill";
  --fa-font-jelly-duo-regular: normal 400 1em/1 "Font Awesome 7 Jelly Duo";
  --fa-font-chisel-regular: normal 400 1em/1 "Font Awesome 7 Chisel";
  --fa-font-utility-semibold: normal 600 1em/1 "Font Awesome 7 Utility";
  --fa-font-utility-duo-semibold: normal 600 1em/1 "Font Awesome 7 Utility Duo";
  --fa-font-utility-fill-semibold: normal 600 1em/1 "Font Awesome 7 Utility Fill";
}

.svg-inline--fa {
  box-sizing: content-box;
  display: var(--fa-display, inline-block);
  height: 1em;
  overflow: visible;
  vertical-align: -0.125em;
  width: var(--fa-width, 1.25em);
}
.svg-inline--fa.fa-2xs {
  vertical-align: 0.1em;
}
.svg-inline--fa.fa-xs {
  vertical-align: 0em;
}
.svg-inline--fa.fa-sm {
  vertical-align: -0.0714285714em;
}
.svg-inline--fa.fa-lg {
  vertical-align: -0.2em;
}
.svg-inline--fa.fa-xl {
  vertical-align: -0.25em;
}
.svg-inline--fa.fa-2xl {
  vertical-align: -0.3125em;
}
.svg-inline--fa.fa-li {
  width: var(--fa-li-width, 2em);
  inset-inline-start: calc(-1 * var(--fa-li-width, 2em));
  inset-block-start: 0.25em; /* syncing vertical alignment with Web Font rendering */
}

.fa-layers-counter, .fa-layers-text {
  display: inline-block;
  position: absolute;
  text-align: center;
}

.fa-layers {
  display: inline-block;
  height: 1em;
  position: relative;
  text-align: center;
  vertical-align: -0.125em;
  width: var(--fa-width, 1.25em);
}
.fa-layers .svg-inline--fa {
  inset: 0;
  margin: auto;
  position: absolute;
  transform-origin: center center;
}

.fa-layers-text {
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  transform-origin: center center;
}

.fa-layers-counter {
  background-color: var(--fa-counter-background-color, #ff253a);
  border-radius: var(--fa-counter-border-radius, 1em);
  box-sizing: border-box;
  color: var(--fa-inverse, #fff);
  line-height: var(--fa-counter-line-height, 1);
  max-width: var(--fa-counter-max-width, 5em);
  min-width: var(--fa-counter-min-width, 1.5em);
  overflow: hidden;
  padding: var(--fa-counter-padding, 0.25em 0.5em);
  right: var(--fa-right, 0);
  text-overflow: ellipsis;
  top: var(--fa-top, 0);
  transform: scale(var(--fa-counter-scale, 0.25));
  transform-origin: top right;
}

.fa-layers-bottom-right {
  bottom: var(--fa-bottom, 0);
  right: var(--fa-right, 0);
  top: auto;
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: bottom right;
}

.fa-layers-bottom-left {
  bottom: var(--fa-bottom, 0);
  left: var(--fa-left, 0);
  right: auto;
  top: auto;
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: bottom left;
}

.fa-layers-top-right {
  top: var(--fa-top, 0);
  right: var(--fa-right, 0);
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: top right;
}

.fa-layers-top-left {
  left: var(--fa-left, 0);
  right: auto;
  top: var(--fa-top, 0);
  transform: scale(var(--fa-layers-scale, 0.25));
  transform-origin: top left;
}

.svg-inline--fa .fa-primary {
  fill: var(--fa-primary-color, currentColor);
  opacity: var(--fa-primary-opacity, 1);
}

.svg-inline--fa .fa-secondary {
  fill: var(--fa-secondary-color, currentColor);
  opacity: var(--fa-secondary-opacity, 0.4);
}

.svg-inline--fa.fa-swap-opacity .fa-primary {
  opacity: var(--fa-secondary-opacity, 0.4);
}

.svg-inline--fa.fa-swap-opacity .fa-secondary {
  opacity: var(--fa-primary-opacity, 1);
}

.svg-inline--fa mask .fa-primary,
.svg-inline--fa mask .fa-secondary {
  fill: black;
}

.svg-inline--fa.fa-inverse {
  fill: var(--fa-inverse, #fff);
}

.fa-stack-1x,
.fa-stack-2x {
  inset: 0;
  margin: auto;
  position: absolute;
  z-index: var(--fa-stack-z-index, auto);
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\svg.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
:host,:root{--fa-font-solid:normal 900 1em/1 "Font Awesome 7 Free";--fa-font-regular:normal 400 1em/1 "Font Awesome 7 Free";--fa-font-light:normal 300 1em/1 "Font Awesome 7 Pro";--fa-font-thin:normal 100 1em/1 "Font Awesome 7 Pro";--fa-font-duotone:normal 900 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-regular:normal 400 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-light:normal 300 1em/1 "Font Awesome 7 Duotone";--fa-font-duotone-thin:normal 100 1em/1 "Font Awesome 7 Duotone";--fa-font-brands:normal 400 1em/1 "Font Awesome 7 Brands";--fa-font-sharp-solid:normal 900 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-regular:normal 400 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-light:normal 300 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-thin:normal 100 1em/1 "Font Awesome 7 Sharp";--fa-font-sharp-duotone-solid:normal 900 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-regular:normal 400 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-light:normal 300 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-sharp-duotone-thin:normal 100 1em/1 "Font Awesome 7 Sharp Duotone";--fa-font-slab-regular:normal 400 1em/1 "Font Awesome 7 Slab";--fa-font-slab-press-regular:normal 400 1em/1 "Font Awesome 7 Slab Press";--fa-font-whiteboard-semibold:normal 600 1em/1 "Font Awesome 7 Whiteboard";--fa-font-thumbprint-light:normal 300 1em/1 "Font Awesome 7 Thumbprint";--fa-font-notdog-solid:normal 900 1em/1 "Font Awesome 7 Notdog";--fa-font-notdog-duo-solid:normal 900 1em/1 "Font Awesome 7 Notdog Duo";--fa-font-etch-solid:normal 900 1em/1 "Font Awesome 7 Etch";--fa-font-jelly-regular:normal 400 1em/1 "Font Awesome 7 Jelly";--fa-font-jelly-fill-regular:normal 400 1em/1 "Font Awesome 7 Jelly Fill";--fa-font-jelly-duo-regular:normal 400 1em/1 "Font Awesome 7 Jelly Duo";--fa-font-chisel-regular:normal 400 1em/1 "Font Awesome 7 Chisel";--fa-font-utility-semibold:normal 600 1em/1 "Font Awesome 7 Utility";--fa-font-utility-duo-semibold:normal 600 1em/1 "Font Awesome 7 Utility Duo";--fa-font-utility-fill-semibold:normal 600 1em/1 "Font Awesome 7 Utility Fill"}.svg-inline--fa{box-sizing:content-box;display:var(--fa-display,inline-block);height:1em;overflow:visible;vertical-align:-.125em;width:var(--fa-width,1.25em)}.svg-inline--fa.fa-2xs{vertical-align:.1em}.svg-inline--fa.fa-xs{vertical-align:0}.svg-inline--fa.fa-sm{vertical-align:-.0714285714em}.svg-inline--fa.fa-lg{vertical-align:-.2em}.svg-inline--fa.fa-xl{vertical-align:-.25em}.svg-inline--fa.fa-2xl{vertical-align:-.3125em}.svg-inline--fa.fa-li{width:var(--fa-li-width,2em);inset-inline-start:calc(var(--fa-li-width, 2em)*-1);inset-block-start:.25em}.fa-layers-counter,.fa-layers-text{display:inline-block;position:absolute;text-align:center}.fa-layers{display:inline-block;height:1em;position:relative;text-align:center;vertical-align:-.125em;width:var(--fa-width,1.25em)}.fa-layers .svg-inline--fa{inset:0;margin:auto;position:absolute;transform-origin:center center}.fa-layers-text{left:50%;top:50%;transform:translate(-50%,-50%);transform-origin:center center}.fa-layers-counter{background-color:var(--fa-counter-background-color,#ff253a);border-radius:var(--fa-counter-border-radius,1em);box-sizing:border-box;color:var(--fa-inverse,#fff);line-height:var(--fa-counter-line-height,1);max-width:var(--fa-counter-max-width,5em);min-width:var(--fa-counter-min-width,1.5em);overflow:hidden;padding:var(--fa-counter-padding,.25em .5em);right:var(--fa-right,0);text-overflow:ellipsis;top:var(--fa-top,0);transform:scale(var(--fa-counter-scale,.25));transform-origin:top right}.fa-layers-bottom-right{bottom:var(--fa-bottom,0);right:var(--fa-right,0);top:auto;transform:scale(var(--fa-layers-scale,.25));transform-origin:bottom right}.fa-layers-bottom-left{bottom:var(--fa-bottom,0);left:var(--fa-left,0);right:auto;top:auto;transform:scale(var(--fa-layers-scale,.25));transform-origin:bottom left}.fa-layers-top-right{top:var(--fa-top,0);right:var(--fa-right,0);transform:scale(var(--fa-layers-scale,.25));transform-origin:top right}.fa-layers-top-left{left:var(--fa-left,0);right:auto;top:var(--fa-top,0);transform:scale(var(--fa-layers-scale,.25));transform-origin:top left}.svg-inline--fa .fa-primary{fill:var(--fa-primary-color,currentColor);opacity:var(--fa-primary-opacity,1)}.svg-inline--fa .fa-secondary{fill:var(--fa-secondary-color,currentColor)}.svg-inline--fa .fa-secondary,.svg-inline--fa.fa-swap-opacity .fa-primary{opacity:var(--fa-secondary-opacity,.4)}.svg-inline--fa.fa-swap-opacity .fa-secondary{opacity:var(--fa-primary-opacity,1)}.svg-inline--fa mask .fa-primary,.svg-inline--fa mask .fa-secondary{fill:#000}.svg-inline--fa.fa-inverse{fill:var(--fa-inverse,#fff)}.fa-stack-1x,.fa-stack-2x{inset:0;margin:auto;position:absolute;z-index:var(--fa-stack-z-index,auto)}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v4-font-face.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-solid-900.woff2") format("woff2");
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-brands-400.woff2") format("woff2");
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-regular-400.woff2") format("woff2");
  unicode-range: U+F003, U+F006, U+F014, U+F016-F017, U+F01A-F01B, U+F01D, U+F022, U+F03E, U+F044, U+F046, U+F05C-F05D, U+F06E, U+F070, U+F087-F088, U+F08A, U+F094, U+F096-F097, U+F09D, U+F0A0, U+F0A2, U+F0A4-F0A7, U+F0C5, U+F0C7, U+F0E5-F0E6, U+F0EB, U+F0F6-F0F8, U+F10C, U+F114-F115, U+F118-F11A, U+F11C-F11D, U+F133, U+F147, U+F14E, U+F150-F152, U+F185-F186, U+F18E, U+F190-F192, U+F196, U+F1C1-F1C9, U+F1D9, U+F1DB, U+F1E3, U+F1EA, U+F1F7, U+F1F9, U+F20A, U+F247-F248, U+F24A, U+F24D, U+F255-F25B, U+F25D, U+F271-F274, U+F278, U+F27B, U+F28C, U+F28E, U+F29C, U+F2B5, U+F2B7, U+F2BA, U+F2BC, U+F2BE, U+F2C0-F2C1, U+F2C3, U+F2D0, U+F2D2, U+F2D4, U+F2DC;
}
@font-face {
  font-family: "FontAwesome";
  font-display: block;
  src: url("../webfonts/fa-v4compatibility.woff2") format("woff2");
  unicode-range: U+F041, U+F047, U+F065-F066, U+F07D-F07E, U+F080, U+F08B, U+F08E, U+F090, U+F09A, U+F0AC, U+F0AE, U+F0B2, U+F0D0, U+F0D6, U+F0E4, U+F0EC, U+F10A-F10B, U+F123, U+F13E, U+F148-F149, U+F14C, U+F156, U+F15E, U+F160-F161, U+F163, U+F175-F178, U+F195, U+F1F8, U+F219, U+F27A;
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v4-font-face.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-solid-900.woff2) format("woff2")}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-brands-400.woff2) format("woff2")}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-regular-400.woff2) format("woff2");unicode-range:u+f003,u+f006,u+f014,u+f016-f017,u+f01a-f01b,u+f01d,u+f022,u+f03e,u+f044,u+f046,u+f05c-f05d,u+f06e,u+f070,u+f087-f088,u+f08a,u+f094,u+f096-f097,u+f09d,u+f0a0,u+f0a2,u+f0a4-f0a7,u+f0c5,u+f0c7,u+f0e5-f0e6,u+f0eb,u+f0f6-f0f8,u+f10c,u+f114-f115,u+f118-f11a,u+f11c-f11d,u+f133,u+f147,u+f14e,u+f150-f152,u+f185-f186,u+f18e,u+f190-f192,u+f196,u+f1c1-f1c9,u+f1d9,u+f1db,u+f1e3,u+f1ea,u+f1f7,u+f1f9,u+f20a,u+f247-f248,u+f24a,u+f24d,u+f255-f25b,u+f25d,u+f271-f274,u+f278,u+f27b,u+f28c,u+f28e,u+f29c,u+f2b5,u+f2b7,u+f2ba,u+f2bc,u+f2be,u+f2c0-f2c1,u+f2c3,u+f2d0,u+f2d2,u+f2d4,u+f2dc}@font-face{font-family:"FontAwesome";font-display:block;src:url(../webfonts/fa-v4compatibility.woff2) format("woff2");unicode-range:u+f041,u+f047,u+f065-f066,u+f07d-f07e,u+f080,u+f08b,u+f08e,u+f090,u+f09a,u+f0ac,u+f0ae,u+f0b2,u+f0d0,u+f0d6,u+f0e4,u+f0ec,u+f10a-f10b,u+f123,u+f13e,u+f148-f149,u+f14c,u+f156,u+f15e,u+f160-f161,u+f163,u+f175-f178,u+f195,u+f1f8,u+f219,u+f27a}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v4-shims.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa.fa-glass {
  --fa: "\f000";
}

.fa.fa-envelope-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-envelope-o {
  --fa: "\f0e0";
}

.fa.fa-star-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-star-o {
  --fa: "\f005";
}

.fa.fa-remove {
  --fa: "\f00d";
}

.fa.fa-close {
  --fa: "\f00d";
}

.fa.fa-gear {
  --fa: "\f013";
}

.fa.fa-trash-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-trash-o {
  --fa: "\f2ed";
}

.fa.fa-home {
  --fa: "\f015";
}

.fa.fa-file-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-o {
  --fa: "\f15b";
}

.fa.fa-clock-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-clock-o {
  --fa: "\f017";
}

.fa.fa-arrow-circle-o-down {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-arrow-circle-o-down {
  --fa: "\f358";
}

.fa.fa-arrow-circle-o-up {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-arrow-circle-o-up {
  --fa: "\f35b";
}

.fa.fa-play-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-play-circle-o {
  --fa: "\f144";
}

.fa.fa-repeat {
  --fa: "\f01e";
}

.fa.fa-rotate-right {
  --fa: "\f01e";
}

.fa.fa-refresh {
  --fa: "\f021";
}

.fa.fa-list-alt {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-list-alt {
  --fa: "\f022";
}

.fa.fa-dedent {
  --fa: "\f03b";
}

.fa.fa-video-camera {
  --fa: "\f03d";
}

.fa.fa-picture-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-picture-o {
  --fa: "\f03e";
}

.fa.fa-photo {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-photo {
  --fa: "\f03e";
}

.fa.fa-image {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-image {
  --fa: "\f03e";
}

.fa.fa-map-marker {
  --fa: "\f3c5";
}

.fa.fa-pencil-square-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-pencil-square-o {
  --fa: "\f044";
}

.fa.fa-edit {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-edit {
  --fa: "\f044";
}

.fa.fa-share-square-o {
  --fa: "\f14d";
}

.fa.fa-check-square-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-check-square-o {
  --fa: "\f14a";
}

.fa.fa-arrows {
  --fa: "\f0b2";
}

.fa.fa-times-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-times-circle-o {
  --fa: "\f057";
}

.fa.fa-check-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-check-circle-o {
  --fa: "\f058";
}

.fa.fa-mail-forward {
  --fa: "\f064";
}

.fa.fa-expand {
  --fa: "\f424";
}

.fa.fa-compress {
  --fa: "\f422";
}

.fa.fa-eye {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-eye-slash {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-warning {
  --fa: "\f071";
}

.fa.fa-calendar {
  --fa: "\f073";
}

.fa.fa-arrows-v {
  --fa: "\f338";
}

.fa.fa-arrows-h {
  --fa: "\f337";
}

.fa.fa-bar-chart {
  --fa: "\e0e3";
}

.fa.fa-bar-chart-o {
  --fa: "\e0e3";
}

.fa.fa-twitter-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-twitter-square {
  --fa: "\f081";
}

.fa.fa-facebook-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-facebook-square {
  --fa: "\f082";
}

.fa.fa-gears {
  --fa: "\f085";
}

.fa.fa-thumbs-o-up {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-thumbs-o-up {
  --fa: "\f164";
}

.fa.fa-thumbs-o-down {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-thumbs-o-down {
  --fa: "\f165";
}

.fa.fa-heart-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-heart-o {
  --fa: "\f004";
}

.fa.fa-sign-out {
  --fa: "\f2f5";
}

.fa.fa-linkedin-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-linkedin-square {
  --fa: "\f08c";
}

.fa.fa-thumb-tack {
  --fa: "\f08d";
}

.fa.fa-external-link {
  --fa: "\f35d";
}

.fa.fa-sign-in {
  --fa: "\f2f6";
}

.fa.fa-github-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-github-square {
  --fa: "\f092";
}

.fa.fa-lemon-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-lemon-o {
  --fa: "\f094";
}

.fa.fa-square-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-square-o {
  --fa: "\f0c8";
}

.fa.fa-bookmark-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-bookmark-o {
  --fa: "\f02e";
}

.fa.fa-twitter {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-facebook {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-facebook {
  --fa: "\f39e";
}

.fa.fa-facebook-f {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-facebook-f {
  --fa: "\f39e";
}

.fa.fa-github {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-credit-card {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-feed {
  --fa: "\f09e";
}

.fa.fa-hdd-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hdd-o {
  --fa: "\f0a0";
}

.fa.fa-hand-o-right {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-o-right {
  --fa: "\f0a4";
}

.fa.fa-hand-o-left {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-o-left {
  --fa: "\f0a5";
}

.fa.fa-hand-o-up {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-o-up {
  --fa: "\f0a6";
}

.fa.fa-hand-o-down {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-o-down {
  --fa: "\f0a7";
}

.fa.fa-globe {
  --fa: "\f57d";
}

.fa.fa-tasks {
  --fa: "\f828";
}

.fa.fa-arrows-alt {
  --fa: "\f31e";
}

.fa.fa-group {
  --fa: "\f0c0";
}

.fa.fa-chain {
  --fa: "\f0c1";
}

.fa.fa-cut {
  --fa: "\f0c4";
}

.fa.fa-files-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-files-o {
  --fa: "\f0c5";
}

.fa.fa-floppy-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-floppy-o {
  --fa: "\f0c7";
}

.fa.fa-save {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-save {
  --fa: "\f0c7";
}

.fa.fa-navicon {
  --fa: "\f0c9";
}

.fa.fa-reorder {
  --fa: "\f0c9";
}

.fa.fa-magic {
  --fa: "\e2ca";
}

.fa.fa-pinterest {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pinterest-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pinterest-square {
  --fa: "\f0d3";
}

.fa.fa-google-plus-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-plus-square {
  --fa: "\f0d4";
}

.fa.fa-google-plus {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-plus {
  --fa: "\f0d5";
}

.fa.fa-money {
  --fa: "\f3d1";
}

.fa.fa-unsorted {
  --fa: "\f0dc";
}

.fa.fa-sort-desc {
  --fa: "\f0dd";
}

.fa.fa-sort-asc {
  --fa: "\f0de";
}

.fa.fa-linkedin {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-linkedin {
  --fa: "\f0e1";
}

.fa.fa-rotate-left {
  --fa: "\f0e2";
}

.fa.fa-legal {
  --fa: "\f0e3";
}

.fa.fa-tachometer {
  --fa: "\f625";
}

.fa.fa-dashboard {
  --fa: "\f625";
}

.fa.fa-comment-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-comment-o {
  --fa: "\f075";
}

.fa.fa-comments-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-comments-o {
  --fa: "\f086";
}

.fa.fa-flash {
  --fa: "\f0e7";
}

.fa.fa-clipboard {
  --fa: "\f0ea";
}

.fa.fa-lightbulb-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-lightbulb-o {
  --fa: "\f0eb";
}

.fa.fa-exchange {
  --fa: "\f362";
}

.fa.fa-cloud-download {
  --fa: "\f0ed";
}

.fa.fa-cloud-upload {
  --fa: "\f0ee";
}

.fa.fa-bell-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-bell-o {
  --fa: "\f0f3";
}

.fa.fa-cutlery {
  --fa: "\f2e7";
}

.fa.fa-file-text-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-text-o {
  --fa: "\f15c";
}

.fa.fa-building-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-building-o {
  --fa: "\f1ad";
}

.fa.fa-hospital-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hospital-o {
  --fa: "\f0f8";
}

.fa.fa-tablet {
  --fa: "\f3fa";
}

.fa.fa-mobile {
  --fa: "\f3cd";
}

.fa.fa-mobile-phone {
  --fa: "\f3cd";
}

.fa.fa-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-circle-o {
  --fa: "\f111";
}

.fa.fa-mail-reply {
  --fa: "\f3e5";
}

.fa.fa-github-alt {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-folder-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-folder-o {
  --fa: "\f07b";
}

.fa.fa-folder-open-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-folder-open-o {
  --fa: "\f07c";
}

.fa.fa-smile-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-smile-o {
  --fa: "\f118";
}

.fa.fa-frown-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-frown-o {
  --fa: "\f119";
}

.fa.fa-meh-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-meh-o {
  --fa: "\f11a";
}

.fa.fa-keyboard-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-keyboard-o {
  --fa: "\f11c";
}

.fa.fa-flag-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-flag-o {
  --fa: "\f024";
}

.fa.fa-mail-reply-all {
  --fa: "\f122";
}

.fa.fa-star-half-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-star-half-o {
  --fa: "\f5c0";
}

.fa.fa-star-half-empty {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-star-half-empty {
  --fa: "\f5c0";
}

.fa.fa-star-half-full {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-star-half-full {
  --fa: "\f5c0";
}

.fa.fa-code-fork {
  --fa: "\f126";
}

.fa.fa-chain-broken {
  --fa: "\f127";
}

.fa.fa-unlink {
  --fa: "\f127";
}

.fa.fa-calendar-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-calendar-o {
  --fa: "\f133";
}

.fa.fa-maxcdn {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-html5 {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-css3 {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-unlock-alt {
  --fa: "\f09c";
}

.fa.fa-minus-square-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-minus-square-o {
  --fa: "\f146";
}

.fa.fa-level-up {
  --fa: "\f3bf";
}

.fa.fa-level-down {
  --fa: "\f3be";
}

.fa.fa-pencil-square {
  --fa: "\f14b";
}

.fa.fa-external-link-square {
  --fa: "\f360";
}

.fa.fa-compass {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-caret-square-o-down {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-caret-square-o-down {
  --fa: "\f150";
}

.fa.fa-toggle-down {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-toggle-down {
  --fa: "\f150";
}

.fa.fa-caret-square-o-up {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-caret-square-o-up {
  --fa: "\f151";
}

.fa.fa-toggle-up {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-toggle-up {
  --fa: "\f151";
}

.fa.fa-caret-square-o-right {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-caret-square-o-right {
  --fa: "\f152";
}

.fa.fa-toggle-right {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-toggle-right {
  --fa: "\f152";
}

.fa.fa-eur {
  --fa: "\f153";
}

.fa.fa-euro {
  --fa: "\f153";
}

.fa.fa-gbp {
  --fa: "\f154";
}

.fa.fa-usd {
  --fa: "\$";
}

.fa.fa-dollar {
  --fa: "\$";
}

.fa.fa-inr {
  --fa: "\e1bc";
}

.fa.fa-rupee {
  --fa: "\e1bc";
}

.fa.fa-jpy {
  --fa: "\f157";
}

.fa.fa-cny {
  --fa: "\f157";
}

.fa.fa-rmb {
  --fa: "\f157";
}

.fa.fa-yen {
  --fa: "\f157";
}

.fa.fa-rub {
  --fa: "\f158";
}

.fa.fa-ruble {
  --fa: "\f158";
}

.fa.fa-rouble {
  --fa: "\f158";
}

.fa.fa-krw {
  --fa: "\f159";
}

.fa.fa-won {
  --fa: "\f159";
}

.fa.fa-btc {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bitcoin {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bitcoin {
  --fa: "\f15a";
}

.fa.fa-file-text {
  --fa: "\f15c";
}

.fa.fa-sort-alpha-asc {
  --fa: "\f15d";
}

.fa.fa-sort-alpha-desc {
  --fa: "\f881";
}

.fa.fa-sort-amount-asc {
  --fa: "\f884";
}

.fa.fa-sort-amount-desc {
  --fa: "\f160";
}

.fa.fa-sort-numeric-asc {
  --fa: "\f162";
}

.fa.fa-sort-numeric-desc {
  --fa: "\f886";
}

.fa.fa-youtube-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-youtube-square {
  --fa: "\f431";
}

.fa.fa-youtube {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-xing {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-xing-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-xing-square {
  --fa: "\f169";
}

.fa.fa-youtube-play {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-youtube-play {
  --fa: "\f167";
}

.fa.fa-dropbox {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-stack-overflow {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-instagram {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-flickr {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-adn {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bitbucket {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bitbucket-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bitbucket-square {
  --fa: "\f171";
}

.fa.fa-tumblr {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-tumblr-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-tumblr-square {
  --fa: "\f174";
}

.fa.fa-long-arrow-down {
  --fa: "\f309";
}

.fa.fa-long-arrow-up {
  --fa: "\f30c";
}

.fa.fa-long-arrow-left {
  --fa: "\f30a";
}

.fa.fa-long-arrow-right {
  --fa: "\f30b";
}

.fa.fa-apple {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-windows {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-android {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-linux {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-dribbble {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-skype {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-foursquare {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-trello {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gratipay {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gittip {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gittip {
  --fa: "\f184";
}

.fa.fa-sun-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-sun-o {
  --fa: "\f185";
}

.fa.fa-moon-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-moon-o {
  --fa: "\f186";
}

.fa.fa-vk {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-weibo {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-renren {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pagelines {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-stack-exchange {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-arrow-circle-o-right {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-arrow-circle-o-right {
  --fa: "\f35a";
}

.fa.fa-arrow-circle-o-left {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-arrow-circle-o-left {
  --fa: "\f359";
}

.fa.fa-caret-square-o-left {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-caret-square-o-left {
  --fa: "\f191";
}

.fa.fa-toggle-left {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-toggle-left {
  --fa: "\f191";
}

.fa.fa-dot-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-dot-circle-o {
  --fa: "\f192";
}

.fa.fa-vimeo-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-vimeo-square {
  --fa: "\f194";
}

.fa.fa-try {
  --fa: "\e2bb";
}

.fa.fa-turkish-lira {
  --fa: "\e2bb";
}

.fa.fa-plus-square-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-plus-square-o {
  --fa: "\f0fe";
}

.fa.fa-slack {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wordpress {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-openid {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-institution {
  --fa: "\f19c";
}

.fa.fa-bank {
  --fa: "\f19c";
}

.fa.fa-mortar-board {
  --fa: "\f19d";
}

.fa.fa-yahoo {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-reddit {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-reddit-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-reddit-square {
  --fa: "\f1a2";
}

.fa.fa-stumbleupon-circle {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-stumbleupon {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-delicious {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-digg {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pied-piper-pp {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pied-piper-alt {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-drupal {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-joomla {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-behance {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-behance-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-behance-square {
  --fa: "\f1b5";
}

.fa.fa-steam {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-steam-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-steam-square {
  --fa: "\f1b7";
}

.fa.fa-automobile {
  --fa: "\f1b9";
}

.fa.fa-cab {
  --fa: "\f1ba";
}

.fa.fa-spotify {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-deviantart {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-soundcloud {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-file-pdf-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-pdf-o {
  --fa: "\f1c1";
}

.fa.fa-file-word-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-word-o {
  --fa: "\f1c2";
}

.fa.fa-file-excel-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-excel-o {
  --fa: "\f1c3";
}

.fa.fa-file-powerpoint-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-powerpoint-o {
  --fa: "\f1c4";
}

.fa.fa-file-image-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-image-o {
  --fa: "\f1c5";
}

.fa.fa-file-photo-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-photo-o {
  --fa: "\f1c5";
}

.fa.fa-file-picture-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-picture-o {
  --fa: "\f1c5";
}

.fa.fa-file-archive-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-archive-o {
  --fa: "\f1c6";
}

.fa.fa-file-zip-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-zip-o {
  --fa: "\f1c6";
}

.fa.fa-file-audio-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-audio-o {
  --fa: "\f1c7";
}

.fa.fa-file-sound-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-sound-o {
  --fa: "\f1c7";
}

.fa.fa-file-video-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-video-o {
  --fa: "\f1c8";
}

.fa.fa-file-movie-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-movie-o {
  --fa: "\f1c8";
}

.fa.fa-file-code-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-file-code-o {
  --fa: "\f1c9";
}

.fa.fa-vine {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-codepen {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-jsfiddle {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-life-bouy {
  --fa: "\f1cd";
}

.fa.fa-life-buoy {
  --fa: "\f1cd";
}

.fa.fa-life-saver {
  --fa: "\f1cd";
}

.fa.fa-support {
  --fa: "\f1cd";
}

.fa.fa-circle-o-notch {
  --fa: "\f1ce";
}

.fa.fa-rebel {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-ra {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-ra {
  --fa: "\f1d0";
}

.fa.fa-resistance {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-resistance {
  --fa: "\f1d0";
}

.fa.fa-empire {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-ge {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-ge {
  --fa: "\f1d1";
}

.fa.fa-git-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-git-square {
  --fa: "\f1d2";
}

.fa.fa-git {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-hacker-news {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-y-combinator-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-y-combinator-square {
  --fa: "\f1d4";
}

.fa.fa-yc-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-yc-square {
  --fa: "\f1d4";
}

.fa.fa-tencent-weibo {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-qq {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-weixin {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wechat {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wechat {
  --fa: "\f1d7";
}

.fa.fa-send {
  --fa: "\f1d8";
}

.fa.fa-paper-plane-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-paper-plane-o {
  --fa: "\f1d8";
}

.fa.fa-send-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-send-o {
  --fa: "\f1d8";
}

.fa.fa-circle-thin {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-circle-thin {
  --fa: "\f111";
}

.fa.fa-header {
  --fa: "\f1dc";
}

.fa.fa-futbol-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-futbol-o {
  --fa: "\f1e3";
}

.fa.fa-soccer-ball-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-soccer-ball-o {
  --fa: "\f1e3";
}

.fa.fa-slideshare {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-twitch {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-yelp {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-newspaper-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-newspaper-o {
  --fa: "\f1ea";
}

.fa.fa-paypal {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-wallet {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-visa {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-mastercard {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-discover {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-amex {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-paypal {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-stripe {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bell-slash-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-bell-slash-o {
  --fa: "\f1f6";
}

.fa.fa-trash {
  --fa: "\f2ed";
}

.fa.fa-copyright {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-eyedropper {
  --fa: "\f1fb";
}

.fa.fa-area-chart {
  --fa: "\f1fe";
}

.fa.fa-pie-chart {
  --fa: "\f200";
}

.fa.fa-line-chart {
  --fa: "\f201";
}

.fa.fa-lastfm {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-lastfm-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-lastfm-square {
  --fa: "\f203";
}

.fa.fa-ioxhost {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-angellist {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-cc {
  --fa: "\f20a";
}

.fa.fa-ils {
  --fa: "\f20b";
}

.fa.fa-shekel {
  --fa: "\f20b";
}

.fa.fa-sheqel {
  --fa: "\f20b";
}

.fa.fa-buysellads {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-connectdevelop {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-dashcube {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-forumbee {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-leanpub {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-sellsy {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-shirtsinbulk {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-simplybuilt {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-skyatlas {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-diamond {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-diamond {
  --fa: "\f3a5";
}

.fa.fa-transgender {
  --fa: "\f224";
}

.fa.fa-intersex {
  --fa: "\f224";
}

.fa.fa-transgender-alt {
  --fa: "\f225";
}

.fa.fa-facebook-official {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-facebook-official {
  --fa: "\f09a";
}

.fa.fa-pinterest-p {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-whatsapp {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-hotel {
  --fa: "\f236";
}

.fa.fa-viacoin {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-medium {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-y-combinator {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-yc {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-yc {
  --fa: "\f23b";
}

.fa.fa-optin-monster {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-opencart {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-expeditedssl {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-battery-4 {
  --fa: "\f240";
}

.fa.fa-battery {
  --fa: "\f240";
}

.fa.fa-battery-3 {
  --fa: "\f241";
}

.fa.fa-battery-2 {
  --fa: "\f242";
}

.fa.fa-battery-1 {
  --fa: "\f243";
}

.fa.fa-battery-0 {
  --fa: "\f244";
}

.fa.fa-object-group {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-object-ungroup {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-sticky-note-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-sticky-note-o {
  --fa: "\f249";
}

.fa.fa-cc-jcb {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-cc-diners-club {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-clone {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hourglass-o {
  --fa: "\f254";
}

.fa.fa-hourglass-1 {
  --fa: "\f251";
}

.fa.fa-hourglass-2 {
  --fa: "\f252";
}

.fa.fa-hourglass-3 {
  --fa: "\f253";
}

.fa.fa-hand-rock-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-rock-o {
  --fa: "\f255";
}

.fa.fa-hand-grab-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-grab-o {
  --fa: "\f255";
}

.fa.fa-hand-paper-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-paper-o {
  --fa: "\f256";
}

.fa.fa-hand-stop-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-stop-o {
  --fa: "\f256";
}

.fa.fa-hand-scissors-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-scissors-o {
  --fa: "\f257";
}

.fa.fa-hand-lizard-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-lizard-o {
  --fa: "\f258";
}

.fa.fa-hand-spock-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-spock-o {
  --fa: "\f259";
}

.fa.fa-hand-pointer-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-pointer-o {
  --fa: "\f25a";
}

.fa.fa-hand-peace-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-hand-peace-o {
  --fa: "\f25b";
}

.fa.fa-registered {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-creative-commons {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gg {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gg-circle {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-odnoklassniki {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-odnoklassniki-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-odnoklassniki-square {
  --fa: "\f264";
}

.fa.fa-get-pocket {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wikipedia-w {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-safari {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-chrome {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-firefox {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-opera {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-internet-explorer {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-television {
  --fa: "\f26c";
}

.fa.fa-contao {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-500px {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-amazon {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-calendar-plus-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-calendar-plus-o {
  --fa: "\f271";
}

.fa.fa-calendar-minus-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-calendar-minus-o {
  --fa: "\f272";
}

.fa.fa-calendar-times-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-calendar-times-o {
  --fa: "\f273";
}

.fa.fa-calendar-check-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-calendar-check-o {
  --fa: "\f274";
}

.fa.fa-map-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-map-o {
  --fa: "\f279";
}

.fa.fa-commenting {
  --fa: "\f4ad";
}

.fa.fa-commenting-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-commenting-o {
  --fa: "\f4ad";
}

.fa.fa-houzz {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-vimeo {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-vimeo {
  --fa: "\f27d";
}

.fa.fa-black-tie {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-fonticons {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-reddit-alien {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-edge {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-credit-card-alt {
  --fa: "\f09d";
}

.fa.fa-codiepie {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-modx {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-fort-awesome {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-usb {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-product-hunt {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-mixcloud {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-scribd {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-pause-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-pause-circle-o {
  --fa: "\f28b";
}

.fa.fa-stop-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-stop-circle-o {
  --fa: "\f28d";
}

.fa.fa-bluetooth {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-bluetooth-b {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-gitlab {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wpbeginner {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wpforms {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-envira {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wheelchair-alt {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wheelchair-alt {
  --fa: "\f368";
}

.fa.fa-question-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-question-circle-o {
  --fa: "\f059";
}

.fa.fa-volume-control-phone {
  --fa: "\f2a0";
}

.fa.fa-asl-interpreting {
  --fa: "\f2a3";
}

.fa.fa-deafness {
  --fa: "\f2a4";
}

.fa.fa-hard-of-hearing {
  --fa: "\f2a4";
}

.fa.fa-glide {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-glide-g {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-signing {
  --fa: "\f2a7";
}

.fa.fa-viadeo {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-viadeo-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-viadeo-square {
  --fa: "\f2aa";
}

.fa.fa-snapchat {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-snapchat-ghost {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-snapchat-ghost {
  --fa: "\f2ab";
}

.fa.fa-snapchat-square {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-snapchat-square {
  --fa: "\f2ad";
}

.fa.fa-pied-piper {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-first-order {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-yoast {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-themeisle {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-plus-official {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-plus-official {
  --fa: "\f2b3";
}

.fa.fa-google-plus-circle {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-google-plus-circle {
  --fa: "\f2b3";
}

.fa.fa-font-awesome {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-fa {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-fa {
  --fa: "\f2b4";
}

.fa.fa-handshake-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-handshake-o {
  --fa: "\f2b5";
}

.fa.fa-envelope-open-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-envelope-open-o {
  --fa: "\f2b6";
}

.fa.fa-linode {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-address-book-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-address-book-o {
  --fa: "\f2b9";
}

.fa.fa-vcard {
  --fa: "\f2bb";
}

.fa.fa-address-card-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-address-card-o {
  --fa: "\f2bb";
}

.fa.fa-vcard-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-vcard-o {
  --fa: "\f2bb";
}

.fa.fa-user-circle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-user-circle-o {
  --fa: "\f2bd";
}

.fa.fa-user-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-user-o {
  --fa: "\f007";
}

.fa.fa-id-badge {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-drivers-license {
  --fa: "\f2c2";
}

.fa.fa-id-card-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-id-card-o {
  --fa: "\f2c2";
}

.fa.fa-drivers-license-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-drivers-license-o {
  --fa: "\f2c2";
}

.fa.fa-quora {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-free-code-camp {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-telegram {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-thermometer-4 {
  --fa: "\f2c7";
}

.fa.fa-thermometer {
  --fa: "\f2c7";
}

.fa.fa-thermometer-3 {
  --fa: "\f2c8";
}

.fa.fa-thermometer-2 {
  --fa: "\f2c9";
}

.fa.fa-thermometer-1 {
  --fa: "\f2ca";
}

.fa.fa-thermometer-0 {
  --fa: "\f2cb";
}

.fa.fa-bathtub {
  --fa: "\f2cd";
}

.fa.fa-s15 {
  --fa: "\f2cd";
}

.fa.fa-window-maximize {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-window-restore {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-times-rectangle {
  --fa: "\f410";
}

.fa.fa-window-close-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-window-close-o {
  --fa: "\f410";
}

.fa.fa-times-rectangle-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-times-rectangle-o {
  --fa: "\f410";
}

.fa.fa-bandcamp {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-grav {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-etsy {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-imdb {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-ravelry {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-eercast {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-eercast {
  --fa: "\f2da";
}

.fa.fa-snowflake-o {
  font-family: "Font Awesome 7 Free";
  font-weight: 400;
}

.fa.fa-snowflake-o {
  --fa: "\f2dc";
}

.fa.fa-superpowers {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-wpexplorer {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}

.fa.fa-meetup {
  font-family: "Font Awesome 7 Brands";
  font-weight: 400;
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v4-shims.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
.fa.fa-glass{--fa:"\f000"}.fa.fa-envelope-o{--fa:"\f0e0"}.fa.fa-envelope-o,.fa.fa-star-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-star-o{--fa:"\f005"}.fa.fa-close,.fa.fa-remove{--fa:"\f00d"}.fa.fa-gear{--fa:"\f013"}.fa.fa-trash-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f2ed"}.fa.fa-home{--fa:"\f015"}.fa.fa-file-o{--fa:"\f15b"}.fa.fa-clock-o,.fa.fa-file-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-clock-o{--fa:"\f017"}.fa.fa-arrow-circle-o-down{--fa:"\f358"}.fa.fa-arrow-circle-o-down,.fa.fa-arrow-circle-o-up{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-arrow-circle-o-up{--fa:"\f35b"}.fa.fa-play-circle-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f144"}.fa.fa-repeat,.fa.fa-rotate-right{--fa:"\f01e"}.fa.fa-refresh{--fa:"\f021"}.fa.fa-list-alt{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f022"}.fa.fa-dedent{--fa:"\f03b"}.fa.fa-video-camera{--fa:"\f03d"}.fa.fa-picture-o{--fa:"\f03e"}.fa.fa-photo,.fa.fa-picture-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-photo{--fa:"\f03e"}.fa.fa-image{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f03e"}.fa.fa-map-marker{--fa:"\f3c5"}.fa.fa-pencil-square-o{--fa:"\f044"}.fa.fa-edit,.fa.fa-pencil-square-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-edit{--fa:"\f044"}.fa.fa-share-square-o{--fa:"\f14d"}.fa.fa-check-square-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f14a"}.fa.fa-arrows{--fa:"\f0b2"}.fa.fa-times-circle-o{--fa:"\f057"}.fa.fa-check-circle-o,.fa.fa-times-circle-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-check-circle-o{--fa:"\f058"}.fa.fa-mail-forward{--fa:"\f064"}.fa.fa-expand{--fa:"\f424"}.fa.fa-compress{--fa:"\f422"}.fa.fa-eye,.fa.fa-eye-slash{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-warning{--fa:"\f071"}.fa.fa-calendar{--fa:"\f073"}.fa.fa-arrows-v{--fa:"\f338"}.fa.fa-arrows-h{--fa:"\f337"}.fa.fa-bar-chart,.fa.fa-bar-chart-o{--fa:"\e0e3"}.fa.fa-twitter-square{--fa:"\f081"}.fa.fa-facebook-square,.fa.fa-twitter-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-facebook-square{--fa:"\f082"}.fa.fa-gears{--fa:"\f085"}.fa.fa-thumbs-o-up{--fa:"\f164"}.fa.fa-thumbs-o-down,.fa.fa-thumbs-o-up{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-thumbs-o-down{--fa:"\f165"}.fa.fa-heart-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f004"}.fa.fa-sign-out{--fa:"\f2f5"}.fa.fa-linkedin-square{font-family:"Font Awesome 7 Brands";font-weight:400;--fa:"\f08c"}.fa.fa-thumb-tack{--fa:"\f08d"}.fa.fa-external-link{--fa:"\f35d"}.fa.fa-sign-in{--fa:"\f2f6"}.fa.fa-github-square{font-family:"Font Awesome 7 Brands";font-weight:400;--fa:"\f092"}.fa.fa-lemon-o{--fa:"\f094"}.fa.fa-lemon-o,.fa.fa-square-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-square-o{--fa:"\f0c8"}.fa.fa-bookmark-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f02e"}.fa.fa-facebook,.fa.fa-twitter{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-facebook{--fa:"\f39e"}.fa.fa-facebook-f{--fa:"\f39e"}.fa.fa-facebook-f,.fa.fa-github{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-credit-card{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-feed{--fa:"\f09e"}.fa.fa-hdd-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0a0"}.fa.fa-hand-o-right{--fa:"\f0a4"}.fa.fa-hand-o-left,.fa.fa-hand-o-right{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-o-left{--fa:"\f0a5"}.fa.fa-hand-o-up{--fa:"\f0a6"}.fa.fa-hand-o-down,.fa.fa-hand-o-up{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-o-down{--fa:"\f0a7"}.fa.fa-globe{--fa:"\f57d"}.fa.fa-tasks{--fa:"\f828"}.fa.fa-arrows-alt{--fa:"\f31e"}.fa.fa-group{--fa:"\f0c0"}.fa.fa-chain{--fa:"\f0c1"}.fa.fa-cut{--fa:"\f0c4"}.fa.fa-files-o{--fa:"\f0c5"}.fa.fa-files-o,.fa.fa-floppy-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-floppy-o{--fa:"\f0c7"}.fa.fa-save{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0c7"}.fa.fa-navicon,.fa.fa-reorder{--fa:"\f0c9"}.fa.fa-magic{--fa:"\e2ca"}.fa.fa-pinterest,.fa.fa-pinterest-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-pinterest-square{--fa:"\f0d3"}.fa.fa-google-plus-square{--fa:"\f0d4"}.fa.fa-google-plus,.fa.fa-google-plus-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-google-plus{--fa:"\f0d5"}.fa.fa-money{--fa:"\f3d1"}.fa.fa-unsorted{--fa:"\f0dc"}.fa.fa-sort-desc{--fa:"\f0dd"}.fa.fa-sort-asc{--fa:"\f0de"}.fa.fa-linkedin{font-family:"Font Awesome 7 Brands";font-weight:400;--fa:"\f0e1"}.fa.fa-rotate-left{--fa:"\f0e2"}.fa.fa-legal{--fa:"\f0e3"}.fa.fa-dashboard,.fa.fa-tachometer{--fa:"\f625"}.fa.fa-comment-o{--fa:"\f075"}.fa.fa-comment-o,.fa.fa-comments-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-comments-o{--fa:"\f086"}.fa.fa-flash{--fa:"\f0e7"}.fa.fa-clipboard{--fa:"\f0ea"}.fa.fa-lightbulb-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0eb"}.fa.fa-exchange{--fa:"\f362"}.fa.fa-cloud-download{--fa:"\f0ed"}.fa.fa-cloud-upload{--fa:"\f0ee"}.fa.fa-bell-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0f3"}.fa.fa-cutlery{--fa:"\f2e7"}.fa.fa-file-text-o{--fa:"\f15c"}.fa.fa-building-o,.fa.fa-file-text-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-building-o{--fa:"\f1ad"}.fa.fa-hospital-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0f8"}.fa.fa-tablet{--fa:"\f3fa"}.fa.fa-mobile,.fa.fa-mobile-phone{--fa:"\f3cd"}.fa.fa-circle-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f111"}.fa.fa-mail-reply{--fa:"\f3e5"}.fa.fa-github-alt{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-folder-o{--fa:"\f07b"}.fa.fa-folder-o,.fa.fa-folder-open-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-folder-open-o{--fa:"\f07c"}.fa.fa-smile-o{--fa:"\f118"}.fa.fa-frown-o,.fa.fa-smile-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-frown-o{--fa:"\f119"}.fa.fa-meh-o{--fa:"\f11a"}.fa.fa-keyboard-o,.fa.fa-meh-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-keyboard-o{--fa:"\f11c"}.fa.fa-flag-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f024"}.fa.fa-mail-reply-all{--fa:"\f122"}.fa.fa-star-half-o{--fa:"\f5c0"}.fa.fa-star-half-empty,.fa.fa-star-half-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-star-half-empty{--fa:"\f5c0"}.fa.fa-star-half-full{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f5c0"}.fa.fa-code-fork{--fa:"\f126"}.fa.fa-chain-broken,.fa.fa-unlink{--fa:"\f127"}.fa.fa-calendar-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f133"}.fa.fa-css3,.fa.fa-html5,.fa.fa-maxcdn{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-unlock-alt{--fa:"\f09c"}.fa.fa-minus-square-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f146"}.fa.fa-level-up{--fa:"\f3bf"}.fa.fa-level-down{--fa:"\f3be"}.fa.fa-pencil-square{--fa:"\f14b"}.fa.fa-external-link-square{--fa:"\f360"}.fa.fa-compass{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-caret-square-o-down{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f150"}.fa.fa-toggle-down{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f150"}.fa.fa-caret-square-o-up{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f151"}.fa.fa-toggle-up{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f151"}.fa.fa-caret-square-o-right{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f152"}.fa.fa-toggle-right{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f152"}.fa.fa-eur,.fa.fa-euro{--fa:"\f153"}.fa.fa-gbp{--fa:"\f154"}.fa.fa-dollar,.fa.fa-usd{--fa:"\$"}.fa.fa-inr,.fa.fa-rupee{--fa:"\e1bc"}.fa.fa-cny,.fa.fa-jpy,.fa.fa-rmb,.fa.fa-yen{--fa:"\f157"}.fa.fa-rouble,.fa.fa-rub,.fa.fa-ruble{--fa:"\f158"}.fa.fa-krw,.fa.fa-won{--fa:"\f159"}.fa.fa-bitcoin,.fa.fa-btc{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-bitcoin{--fa:"\f15a"}.fa.fa-file-text{--fa:"\f15c"}.fa.fa-sort-alpha-asc{--fa:"\f15d"}.fa.fa-sort-alpha-desc{--fa:"\f881"}.fa.fa-sort-amount-asc{--fa:"\f884"}.fa.fa-sort-amount-desc{--fa:"\f160"}.fa.fa-sort-numeric-asc{--fa:"\f162"}.fa.fa-sort-numeric-desc{--fa:"\f886"}.fa.fa-youtube-square{--fa:"\f431"}.fa.fa-xing,.fa.fa-xing-square,.fa.fa-youtube,.fa.fa-youtube-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-xing-square{--fa:"\f169"}.fa.fa-youtube-play{--fa:"\f167"}.fa.fa-adn,.fa.fa-bitbucket,.fa.fa-bitbucket-square,.fa.fa-dropbox,.fa.fa-flickr,.fa.fa-instagram,.fa.fa-stack-overflow,.fa.fa-youtube-play{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-bitbucket-square{--fa:"\f171"}.fa.fa-tumblr,.fa.fa-tumblr-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-tumblr-square{--fa:"\f174"}.fa.fa-long-arrow-down{--fa:"\f309"}.fa.fa-long-arrow-up{--fa:"\f30c"}.fa.fa-long-arrow-left{--fa:"\f30a"}.fa.fa-long-arrow-right{--fa:"\f30b"}.fa.fa-android,.fa.fa-apple,.fa.fa-dribbble,.fa.fa-foursquare,.fa.fa-gittip,.fa.fa-gratipay,.fa.fa-linux,.fa.fa-skype,.fa.fa-trello,.fa.fa-windows{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-gittip{--fa:"\f184"}.fa.fa-sun-o{--fa:"\f185"}.fa.fa-moon-o,.fa.fa-sun-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-moon-o{--fa:"\f186"}.fa.fa-pagelines,.fa.fa-renren,.fa.fa-stack-exchange,.fa.fa-vk,.fa.fa-weibo{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-arrow-circle-o-right{--fa:"\f35a"}.fa.fa-arrow-circle-o-left,.fa.fa-arrow-circle-o-right{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-arrow-circle-o-left{--fa:"\f359"}.fa.fa-caret-square-o-left{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f191"}.fa.fa-toggle-left{--fa:"\f191"}.fa.fa-dot-circle-o,.fa.fa-toggle-left{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-dot-circle-o{--fa:"\f192"}.fa.fa-vimeo-square{font-family:"Font Awesome 7 Brands";font-weight:400;--fa:"\f194"}.fa.fa-try,.fa.fa-turkish-lira{--fa:"\e2bb"}.fa.fa-plus-square-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f0fe"}.fa.fa-openid,.fa.fa-slack,.fa.fa-wordpress{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-bank,.fa.fa-institution{--fa:"\f19c"}.fa.fa-mortar-board{--fa:"\f19d"}.fa.fa-google,.fa.fa-reddit,.fa.fa-reddit-square,.fa.fa-yahoo{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-reddit-square{--fa:"\f1a2"}.fa.fa-behance,.fa.fa-behance-square,.fa.fa-delicious,.fa.fa-digg,.fa.fa-drupal,.fa.fa-joomla,.fa.fa-pied-piper-alt,.fa.fa-pied-piper-pp,.fa.fa-stumbleupon,.fa.fa-stumbleupon-circle{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-behance-square{--fa:"\f1b5"}.fa.fa-steam,.fa.fa-steam-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-steam-square{--fa:"\f1b7"}.fa.fa-automobile{--fa:"\f1b9"}.fa.fa-cab{--fa:"\f1ba"}.fa.fa-deviantart,.fa.fa-soundcloud,.fa.fa-spotify{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-file-pdf-o{--fa:"\f1c1"}.fa.fa-file-pdf-o,.fa.fa-file-word-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-word-o{--fa:"\f1c2"}.fa.fa-file-excel-o{--fa:"\f1c3"}.fa.fa-file-excel-o,.fa.fa-file-powerpoint-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-powerpoint-o{--fa:"\f1c4"}.fa.fa-file-image-o{--fa:"\f1c5"}.fa.fa-file-image-o,.fa.fa-file-photo-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-photo-o{--fa:"\f1c5"}.fa.fa-file-picture-o{--fa:"\f1c5"}.fa.fa-file-archive-o,.fa.fa-file-picture-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-archive-o{--fa:"\f1c6"}.fa.fa-file-zip-o{--fa:"\f1c6"}.fa.fa-file-audio-o,.fa.fa-file-zip-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-audio-o{--fa:"\f1c7"}.fa.fa-file-sound-o{--fa:"\f1c7"}.fa.fa-file-sound-o,.fa.fa-file-video-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-video-o{--fa:"\f1c8"}.fa.fa-file-movie-o{--fa:"\f1c8"}.fa.fa-file-code-o,.fa.fa-file-movie-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-file-code-o{--fa:"\f1c9"}.fa.fa-codepen,.fa.fa-jsfiddle,.fa.fa-vine{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-life-bouy,.fa.fa-life-buoy,.fa.fa-life-saver,.fa.fa-support{--fa:"\f1cd"}.fa.fa-circle-o-notch{--fa:"\f1ce"}.fa.fa-ra,.fa.fa-rebel{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-ra{--fa:"\f1d0"}.fa.fa-resistance{--fa:"\f1d0"}.fa.fa-empire,.fa.fa-ge,.fa.fa-resistance{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-ge{--fa:"\f1d1"}.fa.fa-git-square{--fa:"\f1d2"}.fa.fa-git,.fa.fa-git-square,.fa.fa-hacker-news,.fa.fa-y-combinator-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-y-combinator-square{--fa:"\f1d4"}.fa.fa-yc-square{--fa:"\f1d4"}.fa.fa-qq,.fa.fa-tencent-weibo,.fa.fa-wechat,.fa.fa-weixin,.fa.fa-yc-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-wechat{--fa:"\f1d7"}.fa.fa-send{--fa:"\f1d8"}.fa.fa-paper-plane-o{--fa:"\f1d8"}.fa.fa-paper-plane-o,.fa.fa-send-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-send-o{--fa:"\f1d8"}.fa.fa-circle-thin{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f111"}.fa.fa-header{--fa:"\f1dc"}.fa.fa-futbol-o{--fa:"\f1e3"}.fa.fa-futbol-o,.fa.fa-soccer-ball-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-soccer-ball-o{--fa:"\f1e3"}.fa.fa-slideshare,.fa.fa-twitch,.fa.fa-yelp{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-newspaper-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f1ea"}.fa.fa-cc-amex,.fa.fa-cc-discover,.fa.fa-cc-mastercard,.fa.fa-cc-paypal,.fa.fa-cc-stripe,.fa.fa-cc-visa,.fa.fa-google-wallet,.fa.fa-paypal{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-bell-slash-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f1f6"}.fa.fa-trash{--fa:"\f2ed"}.fa.fa-copyright{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-eyedropper{--fa:"\f1fb"}.fa.fa-area-chart{--fa:"\f1fe"}.fa.fa-pie-chart{--fa:"\f200"}.fa.fa-line-chart{--fa:"\f201"}.fa.fa-lastfm,.fa.fa-lastfm-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-lastfm-square{--fa:"\f203"}.fa.fa-angellist,.fa.fa-ioxhost{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-cc{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f20a"}.fa.fa-ils,.fa.fa-shekel,.fa.fa-sheqel{--fa:"\f20b"}.fa.fa-buysellads,.fa.fa-connectdevelop,.fa.fa-dashcube,.fa.fa-forumbee,.fa.fa-leanpub,.fa.fa-sellsy,.fa.fa-shirtsinbulk,.fa.fa-simplybuilt,.fa.fa-skyatlas{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-diamond{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f3a5"}.fa.fa-intersex,.fa.fa-transgender{--fa:"\f224"}.fa.fa-transgender-alt{--fa:"\f225"}.fa.fa-facebook-official{--fa:"\f09a"}.fa.fa-facebook-official,.fa.fa-pinterest-p,.fa.fa-whatsapp{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-hotel{--fa:"\f236"}.fa.fa-medium,.fa.fa-viacoin,.fa.fa-y-combinator,.fa.fa-yc{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-yc{--fa:"\f23b"}.fa.fa-expeditedssl,.fa.fa-opencart,.fa.fa-optin-monster{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-battery,.fa.fa-battery-4{--fa:"\f240"}.fa.fa-battery-3{--fa:"\f241"}.fa.fa-battery-2{--fa:"\f242"}.fa.fa-battery-1{--fa:"\f243"}.fa.fa-battery-0{--fa:"\f244"}.fa.fa-object-group,.fa.fa-object-ungroup,.fa.fa-sticky-note-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-sticky-note-o{--fa:"\f249"}.fa.fa-cc-diners-club,.fa.fa-cc-jcb{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-clone{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hourglass-o{--fa:"\f254"}.fa.fa-hourglass-1{--fa:"\f251"}.fa.fa-hourglass-2{--fa:"\f252"}.fa.fa-hourglass-3{--fa:"\f253"}.fa.fa-hand-rock-o{--fa:"\f255"}.fa.fa-hand-grab-o,.fa.fa-hand-rock-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-grab-o{--fa:"\f255"}.fa.fa-hand-paper-o{--fa:"\f256"}.fa.fa-hand-paper-o,.fa.fa-hand-stop-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-stop-o{--fa:"\f256"}.fa.fa-hand-scissors-o{--fa:"\f257"}.fa.fa-hand-lizard-o,.fa.fa-hand-scissors-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-lizard-o{--fa:"\f258"}.fa.fa-hand-spock-o{--fa:"\f259"}.fa.fa-hand-pointer-o,.fa.fa-hand-spock-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-hand-pointer-o{--fa:"\f25a"}.fa.fa-hand-peace-o{--fa:"\f25b"}.fa.fa-hand-peace-o,.fa.fa-registered{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-creative-commons,.fa.fa-gg,.fa.fa-gg-circle,.fa.fa-odnoklassniki,.fa.fa-odnoklassniki-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-odnoklassniki-square{--fa:"\f264"}.fa.fa-chrome,.fa.fa-firefox,.fa.fa-get-pocket,.fa.fa-internet-explorer,.fa.fa-opera,.fa.fa-safari,.fa.fa-wikipedia-w{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-television{--fa:"\f26c"}.fa.fa-500px,.fa.fa-amazon,.fa.fa-contao{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-calendar-plus-o{--fa:"\f271"}.fa.fa-calendar-minus-o,.fa.fa-calendar-plus-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-calendar-minus-o{--fa:"\f272"}.fa.fa-calendar-times-o{--fa:"\f273"}.fa.fa-calendar-check-o,.fa.fa-calendar-times-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-calendar-check-o{--fa:"\f274"}.fa.fa-map-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f279"}.fa.fa-commenting{--fa:"\f4ad"}.fa.fa-commenting-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f4ad"}.fa.fa-houzz,.fa.fa-vimeo{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-vimeo{--fa:"\f27d"}.fa.fa-black-tie,.fa.fa-edge,.fa.fa-fonticons,.fa.fa-reddit-alien{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-credit-card-alt{--fa:"\f09d"}.fa.fa-codiepie,.fa.fa-fort-awesome,.fa.fa-mixcloud,.fa.fa-modx,.fa.fa-product-hunt,.fa.fa-scribd,.fa.fa-usb{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-pause-circle-o{--fa:"\f28b"}.fa.fa-pause-circle-o,.fa.fa-stop-circle-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-stop-circle-o{--fa:"\f28d"}.fa.fa-bluetooth,.fa.fa-bluetooth-b,.fa.fa-envira,.fa.fa-gitlab,.fa.fa-wheelchair-alt,.fa.fa-wpbeginner,.fa.fa-wpforms{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-wheelchair-alt{--fa:"\f368"}.fa.fa-question-circle-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f059"}.fa.fa-volume-control-phone{--fa:"\f2a0"}.fa.fa-asl-interpreting{--fa:"\f2a3"}.fa.fa-deafness,.fa.fa-hard-of-hearing{--fa:"\f2a4"}.fa.fa-glide,.fa.fa-glide-g{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-signing{--fa:"\f2a7"}.fa.fa-viadeo,.fa.fa-viadeo-square{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-viadeo-square{--fa:"\f2aa"}.fa.fa-snapchat,.fa.fa-snapchat-ghost{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-snapchat-ghost{--fa:"\f2ab"}.fa.fa-snapchat-square{--fa:"\f2ad"}.fa.fa-first-order,.fa.fa-google-plus-official,.fa.fa-pied-piper,.fa.fa-snapchat-square,.fa.fa-themeisle,.fa.fa-yoast{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-google-plus-official{--fa:"\f2b3"}.fa.fa-google-plus-circle{--fa:"\f2b3"}.fa.fa-fa,.fa.fa-font-awesome,.fa.fa-google-plus-circle{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-fa{--fa:"\f2b4"}.fa.fa-handshake-o{--fa:"\f2b5"}.fa.fa-envelope-open-o,.fa.fa-handshake-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-envelope-open-o{--fa:"\f2b6"}.fa.fa-linode{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-address-book-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f2b9"}.fa.fa-vcard{--fa:"\f2bb"}.fa.fa-address-card-o{--fa:"\f2bb"}.fa.fa-address-card-o,.fa.fa-vcard-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-vcard-o{--fa:"\f2bb"}.fa.fa-user-circle-o{--fa:"\f2bd"}.fa.fa-user-circle-o,.fa.fa-user-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-user-o{--fa:"\f007"}.fa.fa-id-badge{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-drivers-license{--fa:"\f2c2"}.fa.fa-id-card-o{--fa:"\f2c2"}.fa.fa-drivers-license-o,.fa.fa-id-card-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-drivers-license-o{--fa:"\f2c2"}.fa.fa-free-code-camp,.fa.fa-quora,.fa.fa-telegram{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-thermometer,.fa.fa-thermometer-4{--fa:"\f2c7"}.fa.fa-thermometer-3{--fa:"\f2c8"}.fa.fa-thermometer-2{--fa:"\f2c9"}.fa.fa-thermometer-1{--fa:"\f2ca"}.fa.fa-thermometer-0{--fa:"\f2cb"}.fa.fa-bathtub,.fa.fa-s15{--fa:"\f2cd"}.fa.fa-window-maximize,.fa.fa-window-restore{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-times-rectangle{--fa:"\f410"}.fa.fa-window-close-o{--fa:"\f410"}.fa.fa-times-rectangle-o,.fa.fa-window-close-o{font-family:"Font Awesome 7 Free";font-weight:400}.fa.fa-times-rectangle-o{--fa:"\f410"}.fa.fa-bandcamp,.fa.fa-eercast,.fa.fa-etsy,.fa.fa-grav,.fa.fa-imdb,.fa.fa-ravelry{font-family:"Font Awesome 7 Brands";font-weight:400}.fa.fa-eercast{--fa:"\f2da"}.fa.fa-snowflake-o{font-family:"Font Awesome 7 Free";font-weight:400;--fa:"\f2dc"}.fa.fa-meetup,.fa.fa-superpowers,.fa.fa-wpexplorer{font-family:"Font Awesome 7 Brands";font-weight:400}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v5-font-face.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
@font-face {
  font-family: "Font Awesome 5 Brands";
  font-display: block;
  font-weight: 400;
  src: url("../webfonts/fa-brands-400.woff2") format("woff2");
}
@font-face {
  font-family: "Font Awesome 5 Free";
  font-display: block;
  font-weight: 900;
  src: url("../webfonts/fa-solid-900.woff2") format("woff2");
}
@font-face {
  font-family: "Font Awesome 5 Free";
  font-display: block;
  font-weight: 400;
  src: url("../webfonts/fa-regular-400.woff2") format("woff2");
}
```

---

### `Tagium.Photino.Blazor\wwwroot\vendor\fontawesome\css\v5-font-face.min.css`

```css
/*!
 * Font Awesome Free 7.1.0 by @fontawesome - https://fontawesome.com
 * License - https://fontawesome.com/license/free (Icons: CC BY 4.0, Fonts: SIL OFL 1.1, Code: MIT License)
 * Copyright 2025 Fonticons, Inc.
 */
@font-face{font-family:"Font Awesome 5 Brands";font-display:block;font-weight:400;src:url(../webfonts/fa-brands-400.woff2) format("woff2")}@font-face{font-family:"Font Awesome 5 Free";font-display:block;font-weight:900;src:url(../webfonts/fa-solid-900.woff2) format("woff2")}@font-face{font-family:"Font Awesome 5 Free";font-display:block;font-weight:400;src:url(../webfonts/fa-regular-400.woff2) format("woff2")}
```

---

