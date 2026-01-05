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

        // Enable Write-Ahead Logging for better concurrency and performance
        using (SqliteCommand walCmd = connection.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode = WAL;";
            _ = walCmd.ExecuteNonQuery();
        }

        string schema = @"
            CREATE TABLE IF NOT EXISTS Tags (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
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