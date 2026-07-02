using Microsoft.Data.Sqlite;
using Tagium.Core.Helpers;

namespace Tagium.Core.Data;

public class DatabaseManager
{
    public string LibraryDbPath { get; }
    public string CacheDbPath { get; }

    public DatabaseManager()
    {
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
        using (SqliteConnection conn = new($"Data Source={LibraryDbPath}"))
        {
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

        using (SqliteConnection conn = new($"Data Source={CacheDbPath}"))
        {
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

        MigrateSchema();
    }

    private void MigrateSchema()
    {
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
            }
            catch
            {
            }
        }

        try
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Tags ADD COLUMN Category TEXT NULL;";
            _ = cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}