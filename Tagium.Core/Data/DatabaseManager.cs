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

        using (SqliteCommand walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            _ = walCmd.ExecuteNonQuery();
        }

        InitializeSchema(connection);
        MigrateSchema(connection);
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                ColorHex TEXT NOT NULL DEFAULT '#757575',
                Emoji TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id TEXT PRIMARY KEY,
                Path TEXT NOT NULL UNIQUE,
                Name TEXT NOT NULL DEFAULT '',
                Hash TEXT NULL,
                DateAdded INTEGER NOT NULL DEFAULT (strftime('%s', 'now')),
                IsMissing INTEGER NOT NULL DEFAULT 0,
                Size INTEGER NOT NULL DEFAULT 0,
                ModifiedTime INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId TEXT NOT NULL,
                TagId TEXT NOT NULL,
                FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (FileId, TagId)
            );

            CREATE TABLE IF NOT EXISTS LibraryRoots (
                Id TEXT PRIMARY KEY,
                Path TEXT NOT NULL UNIQUE,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                LastScanTime INTEGER NULL
            );
        ";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        _ = command.ExecuteNonQuery();
    }

    private static void MigrateSchema(SqliteConnection connection)
    {
        string[] columns = ["Size", "ModifiedTime"];
        foreach (string col in columns)
        {
            try
            {
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = $"ALTER TABLE Files ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0;";
                _ = cmd.ExecuteNonQuery();
            }
            catch
            {
            }
        }
    }
}