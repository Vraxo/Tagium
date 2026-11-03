using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Data;
using Tagium.Core.Helpers;

namespace Tagium.Core.Data;

public class SqliteGuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    public override void SetValue(IDbDataParameter parameter, Guid value)
    {
        parameter.Value = value.ToString("N");
    }

    public override Guid Parse(object value)
    {
        return Guid.Parse((string)value);
    }
}

public class DatabaseManager
{
    public string LibraryDbPath { get; }
    public string CacheDbPath { get; }
    private readonly ILogger<DatabaseManager> _logger;

    static DatabaseManager()
    {
        SqlMapper.AddTypeHandler(new SqliteGuidTypeHandler());
    }

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

        connection.Execute("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");

        IEnumerable<dynamic> dbs = connection.Query("PRAGMA database_list;");
        bool isAttached = dbs.Any(d => string.Equals((string)d.name, "cache", StringComparison.OrdinalIgnoreCase));

        if (!isAttached)
        {
            connection.Execute($"ATTACH DATABASE '{CacheDbPath}' AS cache;");
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
            conn.Execute(@"
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
            ");
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
            conn.Execute(@"
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
            ");
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
                string colType = col switch
                {
                    "Rating" => "INTEGER NULL",
                    "Title" or "Description" => "TEXT NULL",
                    _ => "INTEGER NOT NULL DEFAULT 0"
                };
                conn.Execute($"ALTER TABLE cache.Files ADD COLUMN {col} {colType};");
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
            conn.Execute("ALTER TABLE Tags ADD COLUMN Category TEXT NULL;");
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