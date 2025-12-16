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
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.1" />
    <PackageReference Include="SkiaSharp" Version="3.119.1" />
  </ItemGroup>

</Project>
```

---

### `Tagium.Core\Data\DatabaseManager.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Helpers;

namespace Tagium.Core.Data;

public class DatabaseManager
{
    public string ConnectionString { get; }

    public DatabaseManager()
    {
        string dbPath = AppDataHelper.GetDatabasePath();
        ConnectionString = $"Data Source={dbPath}";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using SqliteConnection connection = new(ConnectionString);
        connection.Open();

        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                ColorHex TEXT NOT NULL DEFAULT '#757575',
                Emoji TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Path TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL DEFAULT '',
                Hash TEXT NULL,
                DateAdded INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
                IsMissing INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId INTEGER NOT NULL,
                TagId INTEGER NOT NULL,
                FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (FileId, TagId)
            );
        ";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        _ = command.ExecuteNonQuery();
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

public enum AddFileResult { Success, Duplicate, Error }

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
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        string orderBy = GetOrderByClause(sortBy, direction);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files {orderBy}";

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(ReadFile(reader));
        }
        return files;
    }

    public FileEntry? GetById(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public FileEntry? GetByHash(string hash)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files WHERE Hash = @Hash";
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFile(reader) : null;
    }

    public AddFileResult Add(string path, string hash)
    {
        if (GetByHash(hash) != null)
        {
            return AddFileResult.Duplicate;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using (SqliteCommand checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = "SELECT COUNT(1) FROM Files WHERE Path = @Path";
            _ = checkCmd.Parameters.AddWithValue("@Path", path);
            if (Convert.ToInt64(checkCmd.ExecuteScalar()) > 0)
            {
                return AddFileResult.Duplicate;
            }
        }

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Files (Path, Name, Hash, DateAdded) 
            VALUES (@Path, @Name, @Hash, strftime('%s','now'))";

        _ = cmd.Parameters.AddWithValue("@Path", path);
        _ = cmd.Parameters.AddWithValue("@Name", Path.GetFileName(path));
        _ = cmd.Parameters.AddWithValue("@Hash", hash);

        try
        {
            _ = cmd.ExecuteNonQuery();
            return AddFileResult.Success;
        }
        catch
        {
            return AddFileResult.Error;
        }
    }

    public void Remove(long id)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE Id = @Id";
        _ = cmd.Parameters.AddWithValue("@Id", id);
        _ = cmd.ExecuteNonQuery();
    }

    public void SetMissingStatus(IEnumerable<long> fileIds, bool isMissing)
    {
        if (!fileIds.Any())
        {
            return;
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteTransaction trans = conn.BeginTransaction();

        foreach (long id in fileIds)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = trans;
            cmd.CommandText = "UPDATE Files SET IsMissing = @IsMissing WHERE Id = @Id";
            _ = cmd.Parameters.AddWithValue("@IsMissing", isMissing ? 1 : 0);
            _ = cmd.Parameters.AddWithValue("@Id", id);
            _ = cmd.ExecuteNonQuery();
        }
        trans.Commit();
    }

    public void RemoveMissingFiles()
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Files WHERE IsMissing = 1";
        _ = cmd.ExecuteNonQuery();
    }

    private static FileEntry ReadFile(SqliteDataReader reader)
    {
        return new FileEntry
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Name = reader.GetString(2),
            Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
            DateAdded = reader.GetInt64(4),
            IsMissing = reader.GetBoolean(5)
        };
    }

    private static string GetOrderByClause(SortBy sortBy, SortDirection direction)
    {
        string col = sortBy switch
        {
            SortBy.Name => "Name",
            SortBy.DateAdded => "DateAdded",
            _ => "Path"
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
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Data;

public class FileSearchRepository
{
    private readonly DatabaseManager _db;
    private static readonly HashSet<string> _searchStopWords = new(StringComparer.OrdinalIgnoreCase) { "and", "or", "not" };

    public FileSearchRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<FileEntry> Search(string searchText, SortBy sortBy, SortDirection sortDirection)
    {
        string[] tokens = searchText.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        List<string> includedTags = tokens.Where(t => !t.StartsWith("-") && !_searchStopWords.Contains(t)).Distinct().ToList();
        List<string> excludedTags = tokens.Where(t => t.StartsWith("-") && t.Length > 1).Select(t => t[1..]).Distinct().ToList();

        if (!includedTags.Any() && !excludedTags.Any())
        {
            return [];
        }

        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();

        string orderBy = GetOrderByClause(sortBy, sortDirection);
        string query;

        // Dynamic SQL construction is necessary for variable tag counts.
        // Parameters are safely parameterized.

        if (includedTags.Any() && excludedTags.Any())
        {
            AddParams(cmd, "@in", includedTags);
            AddParams(cmd, "@ex", excludedTags);

            string inParams = string.Join(",", includedTags.Select((_, i) => $"@in{i}"));
            string exParams = string.Join(",", excludedTags.Select((_, i) => $"@ex{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing FROM Files F 
                WHERE F.Id IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({inParams})
                    GROUP BY FT.FileId
                    HAVING COUNT(DISTINCT T.Id) = {includedTags.Count}
                ) 
                AND F.Id NOT IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({exParams})
                )
                {orderBy}";
        }
        else if (includedTags.Any())
        {
            AddParams(cmd, "@tag", includedTags);
            string tagParams = string.Join(",", includedTags.Select((_, i) => $"@tag{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing
                FROM Files F
                INNER JOIN FileTags FT ON F.Id = FT.FileId
                INNER JOIN Tags T ON FT.TagId = T.Id
                WHERE T.Name IN ({tagParams})
                GROUP BY F.Id
                HAVING COUNT(DISTINCT T.Id) = {includedTags.Count}
                {orderBy}";
        }
        else // Only excluded tags
        {
            AddParams(cmd, "@tag", excludedTags);
            string tagParams = string.Join(",", excludedTags.Select((_, i) => $"@tag{i}"));

            query = $@"
                SELECT F.Id, F.Path, F.Name, F.Hash, F.DateAdded, F.IsMissing
                FROM Files F
                WHERE F.Id NOT IN (
                    SELECT FT.FileId FROM FileTags FT
                    INNER JOIN Tags T ON FT.TagId = T.Id
                    WHERE T.Name IN ({tagParams})
                )
                {orderBy}";
        }

        cmd.CommandText = query;
        List<FileEntry> files = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files.Add(new FileEntry
            {
                Id = reader.GetInt64(0),
                Path = reader.GetString(1),
                Name = reader.GetString(2),
                Hash = reader.IsDBNull(3) ? null : reader.GetString(3),
                DateAdded = reader.GetInt64(4),
                IsMissing = reader.GetBoolean(5)
            });
        }
        return files;
    }

    private static void AddParams(SqliteCommand cmd, string prefix, List<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            _ = cmd.Parameters.AddWithValue($"{prefix}{i}", values[i]);
        }
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
        // Secondary sort by path for stability
        return $"ORDER BY {col} {dir}, F.Path ASC";
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

    public void AddTagToFile(long fileId, long tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO FileTags (FileId, TagId) VALUES (@FileId, @TagId)";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);
        _ = cmd.Parameters.AddWithValue("@TagId", tagId);
        _ = cmd.ExecuteNonQuery();
    }

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM FileTags WHERE FileId = @FileId AND TagId = @TagId";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);
        _ = cmd.Parameters.AddWithValue("@TagId", tagId);
        _ = cmd.ExecuteNonQuery();
    }

    public List<long> GetTagIdsForFile(long fileId)
    {
        List<long> ids = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TagId FROM FileTags WHERE FileId = @FileId";
        _ = cmd.Parameters.AddWithValue("@FileId", fileId);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }
}
```

---

### `Tagium.Core\Data\TagRepository.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Models;

namespace Tagium.Core.Data;

public class TagRepository
{
    private readonly DatabaseManager _db;

    public TagRepository(DatabaseManager db)
    {
        _db = db;
    }

    public List<Tag> GetAll()
    {
        List<Tag> tags = [];
        using SqliteConnection conn = new(_db.ConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT T.Id, T.Name, T.ColorHex, T.Emoji, COUNT(FT.FileId) as FileCount
            FROM Tags T
            LEFT JOIN FileTags FT ON T.Id = FT.TagId
            GROUP BY T.Id
            ORDER BY T.Name";

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
}
```

---

### `Tagium.Core\Helpers\FileHasher.cs`

```csharp
using System.Security.Cryptography;

namespace Tagium.Core.Helpers;

public static class FileHasher
{
    public static string? ComputeSha256(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FileHasher] Error: {ex.Message}");
            return null;
        }
    }
}
```

---

### `Tagium.Core\Models\FileEntry.cs`

```csharp
namespace Tagium.Core.Models;

public class FileEntry
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }

    // Loaded on demand
    public List<Tag> Tags { get; set; } = [];
}
```

---

### `Tagium.Core\Models\Tag.cs`

```csharp
namespace Tagium.Core.Models;

public class Tag
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public string ColorHex { get; set; } = "#757575";
    public string? Emoji { get; set; }

    // Computed property for UI convenience, populated by join queries
    public int FileCount { get; set; }

    public override string ToString()
    {
        return Name;
    }
}
```

---

### `Tagium.Core\Services\DataExportService.cs`

```csharp
using System.Text;
using System.Text.Json;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class DatabaseExport
{
    public List<Tag> Tags { get; set; } = [];
    public List<FileExportData> Files { get; set; } = [];
}

public class FileExportData
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public required string Name { get; set; }
    public string? Hash { get; set; }
    public long DateAdded { get; set; }
    public bool IsMissing { get; set; }
    public List<long> TagIds { get; set; } = [];
}

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
        Dictionary<long, string> tagLookup = data.Tags.ToDictionary(t => t.Id, t => t.Name);
        StringBuilder sb = new();

        _ = sb.AppendLine("Id,Path,Name,Hash,DateAdded,Tags");

        foreach (var file in data.Files)
        {
            string tags = string.Join(";", file.TagIds.Select(id => tagLookup.GetValueOrDefault(id, "unknown")));
            string line = $"{file.Id},\"{Escape(file.Path)}\",\"{Escape(file.Name)}\",{file.Hash},{file.DateAdded},\"{Escape(tags)}\"";
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
            List<long> tagIds = _fileTagRepo.GetTagIdsForFile(f.Id);
            export.Files.Add(new FileExportData
            {
                Id = f.Id,
                Path = f.Path,
                Name = f.Name,
                Hash = f.Hash,
                DateAdded = f.DateAdded,
                IsMissing = f.IsMissing,
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

### `Tagium.Core\Services\DataMergeService.cs`

```csharp
using Microsoft.Data.Sqlite;
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class MergeResult
{
    public int NewFiles { get; set; }
    public int NewTags { get; set; }
    public int NewAssociations { get; set; }
}

public class DatabaseMergeService
{
    private readonly DatabaseManager _targetDb;
    private readonly FileRepository _targetFileRepo;
    private readonly TagRepository _targetTagRepo;
    private readonly FileTagRepository _targetFileTagRepo;

    public DatabaseMergeService(
        DatabaseManager targetDb,
        FileRepository targetFileRepo,
        TagRepository targetTagRepo,
        FileTagRepository targetFileTagRepo)
    {
        _targetDb = targetDb;
        _targetFileRepo = targetFileRepo;
        _targetTagRepo = targetTagRepo;
        _targetFileTagRepo = targetFileTagRepo;
    }

    public MergeResult Merge(string sourceDbPath)
    {
        MergeResult result = new();

        // Connect to source DB manually
        string sourceConnString = $"Data Source={sourceDbPath}";
        using SqliteConnection sourceConn = new(sourceConnString);
        sourceConn.Open();

        // 1. Load Source Data
        List<Tag> sourceTags = LoadTags(sourceConn);
        List<FileEntry> sourceFiles = LoadFiles(sourceConn);
        Dictionary<long, List<long>> sourceFileTagMap = LoadFileTags(sourceConn);

        // 2. Load Target Data
        Dictionary<string, Tag> targetTags = _targetTagRepo.GetAll().ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FileEntry> targetFiles = _targetFileRepo.GetAll(SortBy.Path, SortDirection.Asc)
            .Where(f => f.Hash != null)
            .ToDictionary(f => f.Hash!);

        // 3. Merge Tags
        Dictionary<long, long> tagIdMap = []; // SourceId -> TargetId

        foreach (Tag sTag in sourceTags)
        {
            if (targetTags.TryGetValue(sTag.Name, out Tag? existing))
            {
                tagIdMap[sTag.Id] = existing.Id;
            }
            else
            {
                _targetTagRepo.Add(sTag.Name, sTag.ColorHex, sTag.Emoji);
                // Need to fetch the new ID. For simplicity, we reload tags or assume Add returns ID.
                // Current repo doesn't return ID. Let's re-fetch.
                Tag newTag = _targetTagRepo.GetAll().First(t => t.Name == sTag.Name);
                targetTags[newTag.Name] = newTag;
                tagIdMap[sTag.Id] = newTag.Id;
                result.NewTags++;
            }
        }

        // 4. Merge Files & Associations
        foreach (FileEntry sFile in sourceFiles)
        {
            if (sFile.Hash == null)
            {
                continue;
            }

            long targetFileId;

            if (targetFiles.TryGetValue(sFile.Hash, out FileEntry? existingFile))
            {
                targetFileId = existingFile.Id;
            }
            else
            {
                // Add File
                _ = _targetFileRepo.Add(sFile.Path, sFile.Hash);
                // Re-fetch to get ID. Optimization: Repo.Add should return ID/Entry.
                // Fallback:
                FileEntry? added = _targetFileRepo.GetByHash(sFile.Hash);
                if (added == null)
                {
                    continue; // Failed to add
                }

                targetFileId = added.Id;
                result.NewFiles++;
            }

            // Merge Associations
            if (sourceFileTagMap.TryGetValue(sFile.Id, out List<long>? sTagIds))
            {
                foreach (long sTagId in sTagIds)
                {
                    if (tagIdMap.TryGetValue(sTagId, out long targetTagId))
                    {
                        // Repository handles "INSERT OR IGNORE"
                        _targetFileTagRepo.AddTagToFile(targetFileId, targetTagId);
                        // We assume success, hard to count actual "new" without checking first.
                        // We'll just increment. Real logic would check existence.
                        result.NewAssociations++;
                    }
                }
            }
        }

        return result;
    }

    // Mini-loaders for the external DB connection
    private static List<Tag> LoadTags(SqliteConnection conn)
    {
        List<Tag> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, ColorHex, Emoji FROM Tags";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new Tag { Id = r.GetInt64(0), Name = r.GetString(1), ColorHex = r.GetString(2), Emoji = r.IsDBNull(3) ? null : r.GetString(3) });
        }

        return list;
    }

    private static List<FileEntry> LoadFiles(SqliteConnection conn)
    {
        List<FileEntry> list = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new FileEntry
            {
                Id = r.GetInt64(0),
                Path = r.GetString(1),
                Name = r.GetString(2),
                Hash = r.IsDBNull(3) ? null : r.GetString(3),
                DateAdded = r.GetInt64(4),
                IsMissing = r.GetBoolean(5)
            });
        }

        return list;
    }

    private static Dictionary<long, List<long>> LoadFileTags(SqliteConnection conn)
    {
        Dictionary<long, List<long>> map = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT FileId, TagId FROM FileTags";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long fid = r.GetInt64(0);
            long tid = r.GetInt64(1);
            if (!map.ContainsKey(fid))
            {
                map[fid] = [];
            }

            map[fid].Add(tid);
        }
        return map;
    }
}
```

---

### `Tagium.Core\Services\LibraryIntegrityService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class IntegrityScanResult
{
    public int FilesScanned { get; set; }
    public int NewlyMissing { get; set; }
    public int NewlyFound { get; set; }
}

public class LibraryIntegrityService
{
    private readonly FileRepository _fileRepo;

    public LibraryIntegrityService(FileRepository fileRepo)
    {
        _fileRepo = fileRepo;
    }

    public async Task<IntegrityScanResult> ScanAsync()
    {
        // Get all files sorted by path for potential optimization, though mostly IO bound
        List<FileEntry> allFiles = _fileRepo.GetAll(SortBy.Path, SortDirection.Asc);
        IntegrityScanResult result = new() { FilesScanned = allFiles.Count };

        List<long> toMarkMissing = [];
        List<long> toMarkFound = [];

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

        if (toMarkMissing.Any())
        {
            _fileRepo.SetMissingStatus(toMarkMissing, true);
            result.NewlyMissing = toMarkMissing.Count;
        }

        if (toMarkFound.Any())
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

### `Tagium.Core\Services\LibraryService.cs`

```csharp
using Tagium.Core.Data;
using Tagium.Core.Helpers;
using Tagium.Core.Models;
using Tagium.Core.Settings;

namespace Tagium.Core.Services;

public class LibraryService
{
    private readonly FileRepository _fileRepo;
    private readonly TagRepository _tagRepo;
    private readonly FileTagRepository _fileTagRepo;
    private readonly FileSearchRepository _searchRepo;
    private readonly ThumbnailService _thumbnailService;
    private readonly SettingsManager _settingsManager;

    public event Action? LibraryChanged;
    public event Action<int, int>? ImportCompleted; // added, duplicates

    public LibraryService(
        FileRepository fileRepo,
        TagRepository tagRepo,
        FileTagRepository fileTagRepo,
        FileSearchRepository searchRepo,
        ThumbnailService thumbnailService,
        SettingsManager settingsManager)
    {
        _fileRepo = fileRepo;
        _tagRepo = tagRepo;
        _fileTagRepo = fileTagRepo;
        _searchRepo = searchRepo;
        _thumbnailService = thumbnailService;
        _settingsManager = settingsManager;
    }

    public void Refresh()
    {
        LibraryChanged?.Invoke();
    }

    // --- Files ---

    public List<FileEntry> GetFiles(string? searchText = null, bool missingOnly = false)
    {
        SortBy sort = _settingsManager.Current.FileSortBy;
        SortDirection dir = _settingsManager.Current.FileSortDirection;

        if (missingOnly)
        {
            List<FileEntry> all = _fileRepo.GetAll(sort, dir);
            return all.Where(f => f.IsMissing).ToList();
        }

        return string.IsNullOrWhiteSpace(searchText) ? _fileRepo.GetAll(sort, dir) : _searchRepo.Search(searchText, sort, dir);
    }

    public bool AddFile(string path)
    {
        string? hash = FileHasher.ComputeSha256(path);
        if (hash == null)
        {
            return false; // File read error
        }

        AddFileResult result = _fileRepo.Add(path, hash);

        if (result == AddFileResult.Success)
        {
            LibraryChanged?.Invoke();
            return true;
        }

        return false; // Duplicate or Error
    }

    /// <summary>
    /// Imports multiple files and fires the ImportCompleted event with statistics.
    /// </summary>
    public void ImportBatch(string[] paths)
    {
        int added = 0;
        int duplicates = 0;

        foreach (string path in paths)
        {
            // Basic validation to ensure it's a file
            if (File.Exists(path))
            {
                if (AddFile(path))
                {
                    added++;
                }
                else
                {
                    duplicates++;
                }
            }
        }

        ImportCompleted?.Invoke(added, duplicates);
    }

    public void RemoveFile(long fileId)
    {
        _fileRepo.Remove(fileId);
        LibraryChanged?.Invoke();
    }

    public void AddTagToFile(long fileId, long tagId)
    {
        _fileTagRepo.AddTagToFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    public void RemoveTagFromFile(long fileId, long tagId)
    {
        _fileTagRepo.RemoveTagFromFile(fileId, tagId);
        LibraryChanged?.Invoke();
    }

    // --- Tags ---

    public List<Tag> GetTags()
    {
        return _tagRepo.GetAll();
    }

    public List<Tag> GetTagsForFile(long fileId)
    {
        List<long> tagIds = _fileTagRepo.GetTagIdsForFile(fileId);
        List<Tag> allTags = _tagRepo.GetAll();
        return allTags.Where(t => tagIds.Contains(t.Id)).ToList();
    }

    public void AddTag(string name)
    {
        _tagRepo.Add(name, "#757575", null);
        LibraryChanged?.Invoke();
    }

    public void UpdateTag(Tag tag)
    {
        _tagRepo.Update(tag);
        LibraryChanged?.Invoke();
    }

    public void DeleteTag(long id)
    {
        _tagRepo.Delete(id);
        LibraryChanged?.Invoke();
    }

    // --- Thumbnails ---

    public void EnsureThumbnails(IEnumerable<FileEntry> files)
    {
        foreach (FileEntry file in files)
        {
            if (!_thumbnailService.HasThumbnail(file.Id))
            {
                _ = _thumbnailService.GenerateThumbnailAsync(file);
            }
        }
    }
}
```

---

### `Tagium.Core\Services\ThumbnailService.cs`

```csharp
using FFMpegCore;
using SkiaSharp;
using Tagium.Core.Helpers;
using Tagium.Core.Models;

namespace Tagium.Core.Services;

public class ThumbnailService
{
    private readonly string _cacheDir;
    private readonly bool _hasFFmpeg;
    private readonly HashSet<string> _imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };
    private readonly HashSet<string> _videoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm" };

    public ThumbnailService()
    {
        _cacheDir = AppDataHelper.GetThumbnailCacheDirectory();

        // Check for FFmpeg binaries
        string ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        string ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe");

        _hasFFmpeg = File.Exists(ffmpegPath) && File.Exists(ffprobePath);

        if (_hasFFmpeg)
        {
            GlobalFFOptions.Configure(new FFOptions { BinaryFolder = AppContext.BaseDirectory });
        }
        else
        {
            Console.WriteLine("[ThumbnailService] WARNING: ffmpeg.exe or ffprobe.exe not found. Video thumbnails will use placeholders.");
        }
    }

    public string GetThumbnailPath(long fileId)
    {
        return Path.Combine(_cacheDir, $"{fileId}.png");
    }

    public bool HasThumbnail(long fileId)
    {
        return File.Exists(GetThumbnailPath(fileId));
    }

    public async Task GenerateThumbnailAsync(FileEntry file, int size = 128)
    {
        string destPath = GetThumbnailPath(file.Id);
        if (File.Exists(destPath))
        {
            return;
        }

        if (!File.Exists(file.Path))
        {
            return;
        }

        string ext = Path.GetExtension(file.Path);

        await Task.Run(async () =>
        {
            try
            {
                if (_imageExtensions.Contains(ext))
                {
                    GenerateImageThumbnail(file.Path, destPath, size);
                }
                else if (_videoExtensions.Contains(ext))
                {
                    if (_hasFFmpeg)
                    {
                        await GenerateVideoThumbnailAsync(file.Path, destPath, size);
                    }
                    else
                    {
                        GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
                    }
                }
                else
                {
                    GeneratePlaceholder(destPath, size, ext.TrimStart('.').ToUpper(), SKColors.Gray);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Thumbnail] Error for {file.Path}: {ex.Message}");
                GeneratePlaceholder(destPath, size, "ERR", SKColors.DarkRed);
            }
        });
    }

    private static void GenerateImageThumbnail(string sourcePath, string destPath, int size)
    {
        using SKBitmap? original = SKBitmap.Decode(sourcePath);
        if (original is null)
        {
            return;
        }

        SaveResized(original, destPath, size);
    }

    private static async Task GenerateVideoThumbnailAsync(string sourcePath, string destPath, int size)
    {
        try
        {
            var analysis = await FFProbe.AnalyseAsync(sourcePath);
            var duration = analysis.Duration;
            var captureTime = duration.TotalSeconds > 10 ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(duration.TotalSeconds * 0.1);

            string tempFrame = Path.GetTempFileName() + ".png";

            _ = await FFMpeg.SnapshotAsync(sourcePath, tempFrame, new System.Drawing.Size(size, size), captureTime);

            if (File.Exists(tempFrame))
            {
                using SKBitmap? original = SKBitmap.Decode(tempFrame);
                if (original != null)
                {
                    SaveResized(original, destPath, size);
                }
                File.Delete(tempFrame);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Thumbnail] FFMpeg error: {ex.Message}");
            GeneratePlaceholder(destPath, size, "VIDEO", SKColors.DarkSlateBlue);
        }
    }

    [Obsolete]
    private static void GeneratePlaceholder(string destPath, int size, string text, SKColor color)
    {
        using SKBitmap bitmap = new(size, size);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(color);

        using SKPaint textPaint = new()
        {
            Color = SKColors.White,
            TextSize = size / 4,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };

        float x = size / 2;
        float y = (size / 2) + (textPaint.TextSize / 2) - 5;

        canvas.DrawText(text, x, y, textPaint);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }

    [Obsolete]
    private static void SaveResized(SKBitmap original, string destPath, int size)
    {
        float ratio = (float)Math.Min((double)size / original.Width, (double)size / original.Height);
        int w = (int)(original.Width * ratio);
        int h = (int)(original.Height * ratio);

        SKImageInfo info = new(w, h);
        using SKBitmap? resized = original.Resize(info, SKFilterQuality.Medium);
        if (resized is null)
        {
            return;
        }

        using SKImage image = SKImage.FromBitmap(resized);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 85);
        using FileStream stream = File.OpenWrite(destPath);
        data.SaveTo(stream);
    }
}
```

---

### `Tagium.Core\Settings\EmojiSet.cs`

```csharp
namespace Tagium.Core.Settings;

public enum EmojiSet { SystemDefault, Apple }
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
    private readonly string _settingsPath;

    public event Action? SettingsChanged;

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public SettingsManager()
    {
        string baseDir = AppDataHelper.GetBaseDirectory();
        _settingsPath = Path.Combine(baseDir, "settings.json");
        Current = Load();
    }

    public TagiumSettings Current { get; }

    public void Update(Action<TagiumSettings> updateAction)
    {
        updateAction(Current);
        Save();
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

    private TagiumSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new TagiumSettings();
        }

        try
        {
            string json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<TagiumSettings>(json, _serializerOptions) ?? new TagiumSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Error loading: {ex.Message}. Using defaults.");
            return new TagiumSettings();
        }
    }
}
```

---

### `Tagium.Core\Settings\SortBy.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortBy { Path, Name, DateAdded }
```

---

### `Tagium.Core\Settings\SortDirection.cs`

```csharp
namespace Tagium.Core.Settings;

public enum SortDirection { Asc, Desc }
```

---

### `Tagium.Core\Settings\TagDisplayMode.cs`

```csharp
namespace Tagium.Core.Settings;

public enum TagDisplayMode { ColorCircle, Emoji }
```

---

### `Tagium.Core\Settings\TagiumSettings.cs`

```csharp
namespace Tagium.Core.Settings;

public class TagiumSettings
{
    public TagDisplayMode TagDisplay { get; set; } = TagDisplayMode.ColorCircle;
    public EmojiSet EmojiPreference { get; set; } = EmojiSet.SystemDefault;
    public int ThumbnailSize { get; set; } = 120;
    public SortBy FileSortBy { get; set; } = SortBy.DateAdded;
    public SortDirection FileSortDirection { get; set; } = SortDirection.Desc;
}
```

---

### `Tagium.Photino.Blazor\Program.cs`

```csharp
using Photino.Blazor;
using Tagium.Photino.Blazor.Setup;

namespace Tagium.Photino.Blazor;

public class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var appBuilder = PhotinoBlazorAppBuilder.CreateDefault(args);

        // Register Services
        ServiceRegistrar.ConfigureServices(appBuilder.Services);

        // Register Root Component
        appBuilder.RootComponents.Add<App>("#app");

        var app = appBuilder.Build();

        // Custom Scheme for Thumbnails
        _ = app.MainWindow.RegisterCustomSchemeHandler("tagium", SchemeHandlers.HandleTagiumScheme);

        _ = app.MainWindow
            .SetTitle("Tagium")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center();

        app.Run();
    }
}
```

---

### `Tagium.Photino.Blazor\Tagium.Photino.Blazor.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>Tagium</AssemblyName>
    <UseWindowsForms>False</UseWindowsForms>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.1" />
    <PackageReference Include="NativeFileDialogs.Net" Version="1.2.1" />
    <!-- Downgraded to 3.2.3 to restore RegisterFileDropHandler support -->
    <PackageReference Include="Photino.Blazor" Version="3.2.3" />
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

### `Tagium.Photino.Blazor\Components\DataManagementModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Data
@using Tagium.Core.Services
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject LibraryIntegrityService IntegrityService
@inject DataExportService ExportService
@inject DatabaseMergeService MergeService
@inject DatabaseManager DbManager
@inject FileDialogService FileDialog
@inject ModalService ModalService

<div class="data-tools">
    <div class="section">
        <h4>Integrity</h4>
        <p class="desc">Scan for files missing from disk.</p>
        <button class="btn" @onclick="ScanIntegrity">Scan Now</button>
        @if (ScanResult != null)
        {
            <div class="result-box">
                Scanned: @ScanResult.FilesScanned <br />
                Missing: @ScanResult.NewlyMissing <br />
                Found: @ScanResult.NewlyFound
            </div>
        }
        <button class="btn btn-danger" @onclick="PurgeMissing">Purge Missing Files</button>
    </div>

    <hr />

    <div class="section">
        <h4>Export</h4>
        <div class="btn-row">
            <button class="btn" @onclick="ExportJson">Export JSON</button>
            <button class="btn" @onclick="ExportCsv">Export CSV</button>
            <button class="btn" @onclick="BackupDb">Backup DB</button>
        </div>
    </div>

    <hr />

    <div class="section">
        <h4>Merge Library</h4>
        <p class="desc">Import data from another Tagium/Tagra DB.</p>
        <button class="btn" @onclick="MergeDb">Select Database...</button>
        @if (MergeResult != null)
        {
            <div class="result-box">
                New Files: @MergeResult.NewFiles <br />
                New Tags: @MergeResult.NewTags <br />
                Associations: @MergeResult.NewAssociations
            </div>
        }
    </div>
</div>

<style>
    .data-tools {
        padding: 5px;
    }

    .section {
        margin-bottom: 15px;
    }

    h4 {
        margin: 0 0 5px 0;
        color: #eee;
    }

    .desc {
        font-size: 0.9em;
        color: #aaa;
        margin: 0 0 10px 0;
    }

    .btn {
        background: #444;
        border: 1px solid #555;
        color: white;
        padding: 6px 12px;
        border-radius: 4px;
        cursor: pointer;
    }

        .btn:hover {
            background: #555;
        }

    .btn-danger {
        background: #c62828;
        border-color: #b71c1c;
    }

        .btn-danger:hover {
            background: #d32f2f;
        }

    .btn-row {
        display: flex;
        gap: 10px;
    }

    .result-box {
        margin-top: 10px;
        padding: 8px;
        background: #222;
        border: 1px solid #333;
        border-radius: 4px;
        font-size: 0.9em;
        color: #ccc;
    }

    hr {
        border: 0;
        border-top: 1px solid #444;
        margin: 15px 0;
    }
</style>

@code {
    private IntegrityScanResult? ScanResult;
    private MergeResult? MergeResult;

    private async Task ScanIntegrity()
    {
        ScanResult = null; // reset
        StateHasChanged(); // show loading if we had a spinner
        ScanResult = await IntegrityService.ScanAsync();
        Library.Refresh();
    }

    private void PurgeMissing()
    {
        IntegrityService.PurgeMissing();
        Library.Refresh();
        ScanResult = null;
    }

    private async Task ExportJson()
    {
        // Simple file save dialog simulation: We just save to Downloads or fixed name for now
        // since we don't have a "Save File Dialog" wrapper yet (NFD only does Open by default in some wrappers).
        // Actually NFD supports Save. Let's assume a fixed export for simplicity or add Save to FileDialogService later.
        // For MVP: Save to Desktop.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.json");
        await ExportService.ExportJsonAsync(path);
        // Alert?
    }

    private async Task ExportCsv()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "tagium_export.csv");
        await ExportService.ExportCsvAsync(path);
    }

    private void BackupDb()
    {
        string source = DbManager.ConnectionString.Replace("Data Source=", "");
        string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"tagium_backup_{DateTime.Now:yyyyMMdd}.db");
        File.Copy(source, dest, true);
    }

    private void MergeDb()
    {
        var files = FileDialog.OpenFiles();
        if (files != null && files.Length > 0)
        {
            MergeResult = MergeService.Merge(files[0]);
            Library.Refresh();
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileGrid.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject AppState AppState
@inject SettingsManager Settings

<div class="grid-container" @onclick="ClearSelection">
    @foreach (var file in Files)
    {
        bool isSelected = AppState.SelectedFileIds.Contains(file.Id);
        <div class="file-card @(isSelected ? "selected" : "")"
             style="width: @(Settings.Current.ThumbnailSize)px; height: @(Settings.Current.ThumbnailSize + 30)px"
             @onclick="e => OnCardClick(e, file.Id)"
             @onclick:stopPropagation="true">

            <div class="thumbnail">
                <img src="tagium://thumbnail/@file.Id"
                     alt="@file.Name"
                     onerror="this.style.display='none'; this.parentElement.classList.add('broken');" />
                <div class="placeholder-icon">📷</div>
            </div>
            <div class="filename" title="@file.Name">
                @file.Name
            </div>
        </div>
    }
</div>

<style>
    .grid-container {
        display: flex;
        flex-wrap: wrap;
        gap: 10px;
        align-content: flex-start;
        height: 100%;
        overflow-y: auto;
        padding-right: 5px;
    }

    .file-card {
        background: #313131;
        border: 1px solid #444;
        border-radius: 4px;
        display: flex;
        flex-direction: column;
        cursor: pointer;
        user-select: none;
        transition: width 0.1s, height 0.1s;
    }

        .file-card:hover {
            border-color: #666;
        }

        .file-card.selected {
            border-color: #42a5f5;
            box-shadow: 0 0 0 2px rgba(66, 165, 245, 0.3);
        }

    .thumbnail {
        flex: 1;
        background: #222;
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
            z-index: 2; /* Sit above placeholder */
        }

    /* Fallback Icon behind the image */
    .placeholder-icon {
        position: absolute;
        font-size: 2em;
        color: #444;
        z-index: 1;
    }

    .thumbnail.broken .placeholder-icon {
        color: #666; /* Make it visible if image breaks */
    }

    .filename {
        height: 30px;
        line-height: 30px;
        padding: 0 5px;
        font-size: 12px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        background: #2a2a2a;
    }
</style>

@code {
    private List<FileEntry> Files = [];

    protected override void OnInitialized()
    {
        LoadFiles();
        AppState.OnChange += RefreshView;
        Library.LibraryChanged += LoadFiles;
        Settings.SettingsChanged += RefreshView;
    }

    private void RefreshView()
    {
        LoadFiles();
        InvokeAsync(StateHasChanged);
    }

    private void LoadFiles()
    {
        Files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
        Library.EnsureThumbnails(Files);
        InvokeAsync(StateHasChanged);
    }

    private void OnCardClick(MouseEventArgs e, long id)
    {
        AppState.SelectFile(id, e.CtrlKey, e.ShiftKey);
    }

    private void ClearSelection()
    {
        AppState.ClearSelection();
    }

    public void Dispose()
    {
        AppState.OnChange -= RefreshView;
        Library.LibraryChanged -= LoadFiles;
        Settings.SettingsChanged -= RefreshView;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\FileInspector.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@inject LibraryService Library
@inject AppState AppState
@inject SystemLauncherService Launcher

<div class="inspector">
    @if (SelectedFile == null)
    {
        <div class="empty-state">Select a file to view details.</div>
    }
    else
    {
        <h3>File Details</h3>

        <div class="actions-row">
            <button class="btn" @onclick="OpenFile">Open</button>
            <button class="btn" @onclick="ShowInExplorer">Folder</button>
            <button class="btn btn-danger" @onclick="RemoveFile">Remove</button>
        </div>

        <div class="prop-row">
            <label>Name</label>
            <span>@SelectedFile.Name</span>
        </div>
        <div class="prop-row">
            <label>Path</label>
            <span class="path">@SelectedFile.Path</span>
        </div>

        <hr />

        <h4>Tags</h4>
        <div class="tags-container">
            @foreach (var tag in AssignedTags)
            {
                <span class="tag-pill" @onclick="() => RemoveTag(tag.Id)">
                    @tag.Name <span class="remove">&times;</span>
                </span>
            }
        </div>

        <hr />

        <h4>Add Tag</h4>
        <div class="tags-container available">
            @foreach (var tag in AvailableTags)
            {
                <span class="tag-pill add" @onclick="() => AddTag(tag.Id)">
                    + @tag.Name
                </span>
            }
        </div>
    }
</div>

<style>
    .inspector {
        padding: 10px;
        height: 100%;
        overflow-y: auto;
    }

    .empty-state {
        color: #888;
        text-align: center;
        margin-top: 50px;
    }

    .prop-row {
        margin-bottom: 10px;
    }

        .prop-row label {
            display: block;
            font-size: 0.8em;
            color: #aaa;
        }

    .path {
        word-break: break-all;
        font-size: 0.9em;
        font-family: monospace;
    }

    .actions-row {
        display: flex;
        gap: 5px;
        margin-bottom: 15px;
    }

    .btn {
        background: #444;
        color: white;
        border: none;
        padding: 5px 10px;
        border-radius: 4px;
        cursor: pointer;
        font-size: 0.9em;
        flex: 1;
    }

        .btn:hover {
            background: #555;
        }

    .btn-danger {
        background: #c62828;
    }

        .btn-danger:hover {
            background: #d32f2f;
        }

    .tags-container {
        display: flex;
        flex-wrap: wrap;
        gap: 5px;
    }

    .tag-pill {
        background: #444;
        padding: 4px 8px;
        border-radius: 12px;
        font-size: 0.9em;
        cursor: pointer;
        display: flex;
        align-items: center;
    }

        .tag-pill:hover {
            background: #555;
        }

        .tag-pill .remove {
            margin-left: 5px;
            color: #ef5350;
            font-weight: bold;
        }

        .tag-pill.add {
            background: #2a2a2a;
            border: 1px dashed #555;
        }

            .tag-pill.add:hover {
                background: #42a5f5;
                border-color: transparent;
                color: white;
            }
</style>

@code {
    private FileEntry? SelectedFile;
    private List<Tag> AssignedTags = [];
    private List<Tag> AvailableTags = [];

    protected override void OnInitialized()
    {
        AppState.OnSelectionChange += UpdateView;
        Library.LibraryChanged += UpdateView;
    }

    private void UpdateView()
    {
        if (AppState.SelectedFileIds.Count == 1)
        {
            long id = AppState.SelectedFileIds.First();
            var files = Library.GetFiles(AppState.SearchText, AppState.IsMissingOnly);
            SelectedFile = files.FirstOrDefault(f => f.Id == id);

            if (SelectedFile != null)
            {
                AssignedTags = Library.GetTagsForFile(id);
                var allTags = Library.GetTags();
                AvailableTags = allTags.Where(t => !AssignedTags.Any(at => at.Id == t.Id)).ToList();
            }
        }
        else
        {
            SelectedFile = null;
        }

        InvokeAsync(StateHasChanged);
    }

    private void AddTag(long tagId)
    {
        if (SelectedFile != null) Library.AddTagToFile(SelectedFile.Id, tagId);
    }

    private void RemoveTag(long tagId)
    {
        if (SelectedFile != null) Library.RemoveTagFromFile(SelectedFile.Id, tagId);
    }

    private void OpenFile()
    {
        if (SelectedFile != null) Launcher.OpenFile(SelectedFile.Path);
    }

    private void ShowInExplorer()
    {
        if (SelectedFile != null) Launcher.ShowInExplorer(SelectedFile.Path);
    }

    private void RemoveFile()
    {
        if (SelectedFile != null)
        {
            Library.RemoveFile(SelectedFile.Id);
            AppState.ClearSelection();
        }
    }

    public void Dispose()
    {
        AppState.OnSelectionChange -= UpdateView;
        Library.LibraryChanged -= UpdateView;
    }
}
```

---

### `Tagium.Photino.Blazor\Components\MainLayout.razor`

```
@inherits LayoutComponentBase
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Settings
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Modals
@using System.IO
@inject FileDialogService FileDialog
@inject LibraryService Library
@inject SettingsManager Settings
@inject ModalService ModalService
@inject IJSRuntime JS

<div class="app-layout" @ondragenter="OnDragEnter" @ondragleave="OnDragLeave">
    
    <!-- Drag Overlay -->
    <div class="drag-overlay @(_isDragging ? "active" : "")">
        <div class="drag-message">
            <div style="font-size: 3em; color: #42a5f5; margin-bottom: 20px;">
                📂
            </div>
            <h2>Drop files to import</h2>
        </div>
        @if (_isDragging)
        {
            <!-- InputFile captures the drop event -->
            <InputFile OnChange="HandleFileDrop" multiple class="drag-input" />
        }
    </div>

    <aside class="left-panel">
        <TagSidebar />
    </aside>
    
    <main class="center-panel">
        <div class="toolbar">
            <button class="btn btn-primary" @onclick="ImportFiles">Add Files...</button>
            <button class="btn" @onclick="OpenDataTools">Data Tools</button>
            
            <div class="spacer"></div>
            
            <!-- Status Message Area -->
            <span class="status-msg">@StatusMessage</span>
            
            <div class="control-group">
                <label>Sort:</label>
                <select @onchange="OnSortByChange" value="@Settings.Current.FileSortBy">
                    <option value="@SortBy.DateAdded">Date Added</option>
                    <option value="@SortBy.Name">Name</option>
                    <option value="@SortBy.Path">Path</option>
                </select>
                <select @onchange="OnSortDirChange" value="@Settings.Current.FileSortDirection">
                    <option value="@SortDirection.Asc">Asc</option>
                    <option value="@SortDirection.Desc">Desc</option>
                </select>
            </div>
            
            <div class="control-group">
                <label>Size:</label>
                <input type="range" min="64" max="256" step="16" 
                       value="@Settings.Current.ThumbnailSize" 
                       @onchange="OnThumbSizeChange" />
            </div>
            
            <button class="btn" @onclick="() => Library.Refresh()" title="Reload Data">↻</button>
        </div>
        <div class="content-area">
            <FileGrid />
        </div>
    </main>
    
    <aside class="right-panel">
        <FileInspector />
    </aside>
    
    <ModalHost />
</div>

<style>
    :root {
        --bg-color: #252525;
        --panel-bg: #313131;
        --border-color: #444;
        --text-color: #e0e0e0;
        --primary-color: #42a5f5;
    }

    body, html {
        margin: 0; padding: 0; height: 100%;
        background-color: var(--bg-color);
        color: var(--text-color);
        font-family: 'Segoe UI', sans-serif;
    }

    .app-layout {
        display: flex;
        height: 100vh;
        width: 100vw;
        overflow: hidden;
        position: relative;
    }

    /* Drag Overlay Styles */
    .drag-overlay {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0, 0, 0, 0.85);
        z-index: 9999;
        display: flex;
        justify-content: center;
        align-items: center;
        opacity: 0;
        pointer-events: none;
        transition: opacity 0.2s;
    }

    .drag-overlay.active {
        opacity: 1;
        pointer-events: auto;
    }

    .drag-input {
        position: absolute;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        opacity: 0;
        cursor: copy;
    }

    .drag-message {
        text-align: center;
        color: white;
    }

    .left-panel {
        width: 250px;
        background: var(--panel-bg);
        border-right: 1px solid var(--border-color);
        display: flex;
        flex-direction: column;
        padding: 10px;
        flex-shrink: 0;
    }

    .center-panel {
        flex: 1;
        display: flex;
        flex-direction: column;
        background: var(--bg-color);
        min-width: 0;
    }

    .right-panel {
        width: 300px;
        background: var(--panel-bg);
        border-left: 1px solid var(--border-color);
        padding: 0;
        flex-shrink: 0;
    }
    
    .toolbar {
        height: 50px;
        border-bottom: 1px solid var(--border-color);
        display: flex;
        align-items: center;
        padding: 0 15px;
        background: #2a2a2a;
        gap: 15px;
    }
    
    .status-msg {
        font-size: 0.85em;
        color: #888;
        margin-right: 15px;
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
        max-width: 200px;
    }
    
    .spacer { flex: 1; }
    
    .control-group {
        display: flex;
        align-items: center;
        gap: 5px;
        font-size: 0.9em;
        color: #bbb;
    }
    
    .control-group select {
        background: #444; border: 1px solid #555; color: white; padding: 4px; border-radius: 4px;
    }
    
    .content-area {
        flex: 1;
        overflow: hidden;
        padding: 10px;
    }
    
    .btn {
        background: #444; border: none; color: white; padding: 6px 12px; border-radius: 4px; cursor: pointer;
    }
    .btn:hover { background: #555; }
    
    .btn-primary { background: var(--primary-color); color: white; font-weight: 500; }
    .btn-primary:hover { background: #1e88e5; }
</style>

@code {
    private string StatusMessage = "";
    private bool _isDragging = false;
    private int _dragCounter = 0;

    protected override void OnInitialized()
    {
        Settings.SettingsChanged += StateHasChanged;
        Library.ImportCompleted += ShowImportStatus;
    }

    private void ImportFiles()
    {
        var paths = FileDialog.OpenFiles();
        if (paths != null && paths.Length > 0)
        {
            Library.ImportBatch(paths);
        }
    }

    private void OnDragEnter(DragEventArgs e)
    {
        _dragCounter++;
        if (_dragCounter == 1) _isDragging = true;
    }

    private void OnDragLeave(DragEventArgs e)
    {
        _dragCounter--;
        if (_dragCounter <= 0)
        {
            _dragCounter = 0;
            _isDragging = false;
        }
    }

    private async Task HandleFileDrop(InputFileChangeEventArgs e)
    {
        _dragCounter = 0;
        _isDragging = false;

        var files = e.GetMultipleFiles(100);
        int count = 0;
        
        // Tagium indexes file paths. Since browser drag-and-drop only gives streams,
        // we must save the file to a local "Imports" folder to index it.
        string importDir = Path.Combine(Tagium.Core.Helpers.AppDataHelper.GetBaseDirectory(), "Imports");
        if (!Directory.Exists(importDir)) Directory.CreateDirectory(importDir);

        List<string> importedPaths = [];

        foreach (var file in files)
        {
            try 
            {
                // Create unique filename to prevent collisions
                string safeName = Path.GetFileNameWithoutExtension(file.Name) 
                                  + "_" + Guid.NewGuid().ToString().Substring(0, 8) 
                                  + Path.GetExtension(file.Name);
                
                string localPath = Path.Combine(importDir, safeName);

                // Stream max size: 500MB
                using var stream = file.OpenReadStream(500 * 1024 * 1024); 
                using var fs = File.Create(localPath);
                await stream.CopyToAsync(fs);
                
                importedPaths.Add(localPath);
                count++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Import failed: {ex.Message}");
            }
        }

        if (importedPaths.Count > 0)
        {
            Library.ImportBatch(importedPaths.ToArray());
        }
    }
    
    private void ShowImportStatus(int added, int duplicates)
    {
        InvokeAsync(async () =>
        {
            StatusMessage = $"Added: {added}, Dupes: {duplicates}";
            StateHasChanged();
            
            await Task.Delay(3000);
            StatusMessage = "";
            StateHasChanged();
        });
    }
    
    private void OpenDataTools()
    {
        ModalService.Show("Data Management", @<DataManagementModal />);
    }
    
    private void OnSortByChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortBy>(e.Value?.ToString(), out var result))
        {
            Settings.Update(s => s.FileSortBy = result);
        }
    }
    
    private void OnSortDirChange(ChangeEventArgs e)
    {
        if (Enum.TryParse<SortDirection>(e.Value?.ToString(), out var result))
        {
            Settings.Update(s => s.FileSortDirection = result);
        }
    }
    
    private void OnThumbSizeChange(ChangeEventArgs e)
    {
        if (int.TryParse(e.Value?.ToString(), out int val))
        {
            Settings.Update(s => s.ThumbnailSize = val);
        }
    }
    
    public void Dispose()
    {
        Settings.SettingsChanged -= StateHasChanged;
        Library.ImportCompleted -= ShowImportStatus;
    }
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
                <button class="close-btn" @onclick="Close">&times;</button>
            </div>
            <div class="modal-body">
                @ModalService.Content
            </div>
        </div>
    </div>
}

<style>
    .modal-backdrop {
        position: fixed;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0,0,0,0.7);
        display: flex;
        align-items: center;
        justify-content: center;
        z-index: 1000;
    }

    .modal-container {
        background: #313131;
        border-radius: 8px;
        width: 500px;
        max-width: 90%;
        max-height: 80vh;
        display: flex;
        flex-direction: column;
        box-shadow: 0 4px 10px rgba(0,0,0,0.5);
    }

    .modal-header {
        padding: 15px;
        border-bottom: 1px solid #444;
        display: flex;
        justify-content: space-between;
        align-items: center;
    }

        .modal-header h3 {
            margin: 0;
            font-size: 1.2em;
            color: #eee;
        }

    .close-btn {
        background: none;
        border: none;
        color: #aaa;
        font-size: 1.5em;
        cursor: pointer;
    }

        .close-btn:hover {
            color: #fff;
        }

    .modal-body {
        padding: 15px;
        overflow-y: auto;
    }
</style>

@code {
    private bool IsVisible => ModalService.Content != null;

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

### `Tagium.Photino.Blazor\Components\TagManagerModal.razor`

```
@namespace Tagium.Photino.Blazor.Components.Modals
@using Tagium.Core.Services
@using Tagium.Core.Models
@inject LibraryService Library

<div class="tag-manager">
    <div class="add-row">
        <input type="text" @bind="NewTagName" placeholder="New tag name..." @onkeyup="HandleKeyUp" />
        <button class="btn-primary" @onclick="AddTag">Add</button>
    </div>

    <hr />

    <div class="tag-list">
        @foreach (var tag in Tags)
        {
            <div class="tag-row">
                <!-- Color Picker Dropdown Trigger -->
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
                        </div>
                    }
                </div>

                <!-- Emoji Input -->
                <input type="text" class="emoji-input"
                       value="@tag.Emoji"
                       @onchange="@(e => UpdateTagEmoji(tag, e.Value?.ToString()))"
                       maxlength="2" placeholder="😀" />

                <!-- Name Input -->
                <input type="text" class="name-input"
                       value="@tag.Name"
                       @onchange="@(e => UpdateTagName(tag, e.Value?.ToString()))" />

                <span class="count">(@tag.FileCount)</span>

                <button class="btn-icon delete" @onclick="() => DeleteTag(tag.Id)" title="Delete">&times;</button>
            </div>
        }
    </div>
</div>

<style>
    .add-row {
        display: flex;
        gap: 10px;
        margin-bottom: 15px;
    }

        .add-row input {
            flex: 1;
            padding: 8px;
            background: #444;
            border: 1px solid #555;
            color: white;
            border-radius: 4px;
        }

    .btn-primary {
        background: #42a5f5;
        border: none;
        color: white;
        padding: 0 15px;
        border-radius: 4px;
        cursor: pointer;
    }

        .btn-primary:hover {
            background: #1e88e5;
        }

    .tag-list {
        max-height: 400px;
        overflow-y: auto;
        padding-bottom: 50px;
    }
    /* Padding for popup space */
    .tag-row {
        display: flex;
        align-items: center;
        padding: 5px 8px;
        border-bottom: 1px solid #444;
        gap: 8px;
    }

        .tag-row:hover {
            background: #3a3a3a;
        }

    .color-picker-wrapper {
        position: relative;
    }

    .color-circle {
        width: 16px;
        height: 16px;
        border-radius: 50%;
        border: 1px solid #555;
        cursor: pointer;
        display: block;
    }

    .color-popup {
        position: absolute;
        top: 20px;
        left: 0;
        background: #252525;
        border: 1px solid #555;
        padding: 5px;
        z-index: 100;
        border-radius: 4px;
        width: 140px;
    }

    .color-grid {
        display: flex;
        flex-wrap: wrap;
        gap: 3px;
    }

    .swatch {
        width: 15px;
        height: 15px;
        border-radius: 2px;
        cursor: pointer;
        border: 1px solid rgba(255,255,255,0.1);
    }

        .swatch:hover {
            border-color: white;
            transform: scale(1.1);
        }

    .emoji-input {
        width: 30px;
        text-align: center;
        background: #444;
        border: 1px solid #555;
        color: white;
        border-radius: 4px;
        padding: 4px;
    }

    .name-input {
        flex: 1;
        background: transparent;
        border: 1px solid transparent;
        color: white;
        padding: 4px;
        border-radius: 4px;
    }

        .name-input:focus {
            background: #444;
            border-color: #555;
        }

    .count {
        color: #888;
        font-size: 0.9em;
    }

    .btn-icon.delete {
        background: none;
        border: none;
        color: #e57373;
        font-size: 1.2em;
        cursor: pointer;
    }

        .btn-icon.delete:hover {
            color: #d32f2f;
        }

    hr {
        border: 0;
        border-top: 1px solid #444;
        margin: 10px 0;
    }
</style>

@code {
    private List<Tag> Tags = [];
    private string NewTagName = "";
    private long? ActiveColorPickerId = null;

    private static readonly List<string> Palette = new()
    {
        "#e53935", "#d81b60", "#8e24aa", "#5e35b1", "#3949ab", "#1e88e5",
        "#039be5", "#00acc1", "#00897b", "#43a047", "#7cb342", "#c0ca33",
        "#fdd835", "#ffb300", "#fb8c00", "#f4511e", "#6d4c41", "#757575",
        "#546e7a", "#FFFFFF", "#000000"
    };

    protected override void OnInitialized()
    {
        RefreshTags();
        // Close color picker if clicking elsewhere (handled by ModalHost click propagation ideally,
        // but for now we rely on explicit toggles or clicking another picker)
    }

    private void RefreshTags()
    {
        Tags = Library.GetTags();
    }

    private void HandleKeyUp(KeyboardEventArgs e)
    {
        if (e.Key == "Enter") AddTag();
    }

    private void AddTag()
    {
        if (!string.IsNullOrWhiteSpace(NewTagName))
        {
            Library.AddTag(NewTagName);
            NewTagName = "";
            RefreshTags();
        }
    }

    private void DeleteTag(long id)
    {
        Library.DeleteTag(id);
        RefreshTags();
    }

    private void UpdateTagName(Tag tag, string? newName)
    {
        if (!string.IsNullOrWhiteSpace(newName) && newName != tag.Name)
        {
            tag.Name = newName;
            Library.UpdateTag(tag);
        }
    }

    private void UpdateTagEmoji(Tag tag, string? newEmoji)
    {
        tag.Emoji = newEmoji;
        Library.UpdateTag(tag);
    }

    private void ToggleColorPicker(long id)
    {
        if (ActiveColorPickerId == id)
            ActiveColorPickerId = null;
        else
            ActiveColorPickerId = id;
    }

    private void UpdateTagColor(Tag tag, string color)
    {
        tag.ColorHex = color;
        Library.UpdateTag(tag);
        ActiveColorPickerId = null; // Close picker
    }
}
```

---

### `Tagium.Photino.Blazor\Components\TagSideBar.razor`

```
@namespace Tagium.Photino.Blazor.Components
@using Tagium.Core.Services
@using Tagium.Core.Models
@using Tagium.Photino.Blazor.Services
@using Tagium.Photino.Blazor.Components.Modals
@inject LibraryService Library
@inject AppState AppState
@inject ModalService ModalService

<div class="sidebar-container">
    <div class="search-box">
        <input type="text" placeholder="Search..." @onchange="OnSearchInput" value="@AppState.SearchText" />
    </div>

    <div class="actions">
        <button class="btn-manage" @onclick="OpenTagManager">Manage Tags</button>
    </div>

    <div class="tag-list">
        <div class="tag-item @(string.IsNullOrEmpty(AppState.SearchText) && !AppState.IsMissingOnly ? "active" : "")"
             @onclick="() => SetSearch(string.Empty)">
            <span class="icon">📁</span> All Files
        </div>

        <div class="tag-item @(AppState.IsMissingOnly ? "active" : "")"
             @onclick="() => AppState.SetMissingOnly(true)">
            <span class="icon">⚠️</span> Missing Files
        </div>

        <hr />

        @foreach (var tag in Tags)
        {
            bool isActive = AppState.SearchText.Equals(tag.Name, StringComparison.OrdinalIgnoreCase);
            <div class="tag-item @(isActive ? "active" : "")" @onclick="() => SetSearch(tag.Name)">
                <span class="tag-color" style="background-color: @tag.ColorHex"></span>
                <span class="name">@tag.Name</span>
                <span class="count">@tag.FileCount</span>
            </div>
        }
    </div>
</div>

<style>
    .sidebar-container {
        display: flex;
        flex-direction: column;
        height: 100%;
    }

    .search-box {
        padding-bottom: 10px;
    }

        .search-box input {
            width: 100%;
            padding: 5px;
            background: #444;
            border: 1px solid #555;
            color: #fff;
            border-radius: 4px;
        }

    .actions {
        padding-bottom: 10px;
    }

    .btn-manage {
        width: 100%;
        background: #3a3a3a;
        color: #ccc;
        border: 1px solid #555;
        padding: 4px;
        cursor: pointer;
        border-radius: 4px;
    }

        .btn-manage:hover {
            background: #4a4a4a;
            color: white;
        }

    .tag-list {
        flex: 1;
        overflow-y: auto;
    }

    .tag-item {
        padding: 6px 8px;
        cursor: pointer;
        display: flex;
        align-items: center;
        border-radius: 4px;
        margin-bottom: 2px;
    }

        .tag-item:hover {
            background: #3a3a3a;
        }

        .tag-item.active {
            background: #42a5f5;
            color: white;
        }

    .tag-color {
        width: 10px;
        height: 10px;
        border-radius: 50%;
        margin-right: 8px;
    }

    .name {
        flex: 1;
    }

    .count {
        font-size: 0.8em;
        opacity: 0.7;
    }

    .icon {
        margin-right: 8px;
    }

    hr {
        border: 0;
        border-top: 1px solid #444;
        margin: 5px 0;
    }
</style>

@code {
    private List<Tag> Tags = [];

    protected override void OnInitialized()
    {
        Tags = Library.GetTags();
        AppState.OnChange += StateHasChanged;
        Library.LibraryChanged += RefreshData;
    }

    private void RefreshData()
    {
        Tags = Library.GetTags();
        InvokeAsync(StateHasChanged);
    }

    private void OnSearchInput(ChangeEventArgs e)
    {
        SetSearch(e.Value?.ToString() ?? "");
    }

    private void SetSearch(string text)
    {
        AppState.SetSearch(text);
    }

    private void OpenTagManager()
    {
        ModalService.Show("Manage Tags", @<TagManagerModal />);
    }

    public void Dispose()
    {
        AppState.OnChange -= StateHasChanged;
        Library.LibraryChanged -= RefreshData;
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
@using Tagium.Core.Models
@using Tagium.Core.Services
```

---

### `Tagium.Photino.Blazor\Services\AppState.cs`

```csharp
namespace Tagium.Photino.Blazor.Services;

public class AppState
{
    // State
    public string SearchText { get; private set; } = "";
    public bool IsMissingOnly { get; private set; }
    public HashSet<long> SelectedFileIds { get; private set; } = [];

    // Events
    public event Action? OnChange;
    public event Action? OnSelectionChange;

    public void SetSearch(string text)
    {
        if (SearchText != text)
        {
            SearchText = text;
            IsMissingOnly = false;
            SelectedFileIds.Clear();
            NotifyStateChanged();
        }
    }

    public void SetMissingOnly(bool isMissing)
    {
        if (IsMissingOnly != isMissing)
        {
            IsMissingOnly = isMissing;
            SearchText = "";
            SelectedFileIds.Clear();
            NotifyStateChanged();
        }
    }

    public void SelectFile(long id, bool multiSelect, bool rangeSelect)
    {
        // Simple single/multi logic for now. Range select requires list knowledge.
        if (!multiSelect)
        {
            SelectedFileIds.Clear();
        }

        _ = SelectedFileIds.Contains(id) && multiSelect ? SelectedFileIds.Remove(id) : SelectedFileIds.Add(id);

        NotifySelectionChanged();
    }

    public void ClearSelection()
    {
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
        OnChange?.Invoke(); // Selection is part of state
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
    public string[]? OpenFiles()
    {
        // Nfd.OpenDialogMultiple returns a path set, or throws/returns error status.
        // We wrap it to return a safe string array or null.

        NfdStatus result = Nfd.OpenDialogMultiple(out string[] paths, null, null);

        return result == NfdStatus.Ok ? paths : null;
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
using System.Diagnostics;

namespace Tagium.Photino.Blazor.Services;

public class SystemLauncherService
{
    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to open file: {ex.Message}");
        }
    }

    public void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            // Select the file in explorer
            string argument = $"/select,\"{path}\"";
            _ = Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Launcher] Failed to show in explorer: {ex.Message}");
        }
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\SchemeHandlers.cs`

```csharp
using Tagium.Core.Helpers;

namespace Tagium.Photino.Blazor.Setup;

public static class SchemeHandlers
{
    public static Stream HandleTagiumScheme(object sender, string scheme, string url, out string contentType)
    {
        contentType = "text/plain";
        Uri uri = new(url);

        // Format: tagium://thumbnail/{fileId}
        if (!uri.Host.Equals("thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            return Stream.Null;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');

        if (segments.Length == 0 || !long.TryParse(segments[0], out long fileId))
        {
            return Stream.Null;
        }

        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        string path = Path.Combine(cacheDir, $"{fileId}.png");

        if (!File.Exists(path))
        {
            // Returning null here signals a "Not Found" to WebView2 in some contexts,
            // or we can return a 404 stream if the wrapper supports it. 
            // Photino: returning null usually results in an empty response or 404.
            return null!;
        }

        contentType = "image/png";
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
```

---

### `Tagium.Photino.Blazor\Setup\ServiceRegister.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Tagium.Core.Data;
using Tagium.Core.Services;
using Tagium.Core.Settings;
using Tagium.Photino.Blazor.Services;

namespace Tagium.Photino.Blazor.Setup;

public static class ServiceRegistrar
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Core - Settings
        _ = services.AddSingleton<SettingsManager>();

        // Core - Data
        _ = services.AddSingleton<DatabaseManager>();
        _ = services.AddSingleton<TagRepository>();
        _ = services.AddSingleton<FileRepository>();
        _ = services.AddSingleton<FileTagRepository>();
        _ = services.AddSingleton<FileSearchRepository>();

        // Core - Services
        _ = services.AddSingleton<ThumbnailService>();
        _ = services.AddSingleton<LibraryService>();
        _ = services.AddSingleton<LibraryIntegrityService>();
        _ = services.AddSingleton<DataExportService>();
        _ = services.AddSingleton<DatabaseMergeService>();

        // UI Services
        _ = services.AddSingleton<AppState>();
        _ = services.AddSingleton<ModalService>();
        _ = services.AddSingleton<FileDialogService>();
        _ = services.AddSingleton<SystemLauncherService>();
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
    </style>
</head>
<body>
    <div id="app">Loading...</div>
    <script src="_framework/blazor.webview.js"></script>
    <script src="js/dragdrop.js"></script>
</body>
</html>
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

