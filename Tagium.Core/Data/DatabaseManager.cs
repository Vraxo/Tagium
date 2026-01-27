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

        int userVersion = GetUserVersion(connection);

        if (userVersion == 0)
        {
            // Fresh database - create V2 schema directly
            CreateV2Schema(connection);
            SetUserVersion(connection, 2);
        }
        else if (userVersion == 1)
        {
            // Existing V1 database with integer IDs - migrate to V2
            MigrateV1ToV2(connection);
            SetUserVersion(connection, 2);
        }
        // else version 2+ - schema already current
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        _ = cmd.ExecuteNonQuery();
    }

    private static void CreateV2Schema(SqliteConnection connection)
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
                IsMissing INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS FileTags (
                FileId TEXT NOT NULL,
                TagId TEXT NOT NULL,
                FOREIGN KEY(FileId) REFERENCES Files(Id) ON DELETE CASCADE,
                FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE,
                PRIMARY KEY (FileId, TagId)
            );
        ";

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = schema;
        _ = command.ExecuteNonQuery();
    }

    private static void MigrateV1ToV2(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();

        try
        {
            // Rename old tables
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    ALTER TABLE Tags RENAME TO Tags_Old;
                    ALTER TABLE Files RENAME TO Files_Old;
                    ALTER TABLE FileTags RENAME TO FileTags_Old;
                ";
                _ = cmd.ExecuteNonQuery();
            }

            // Create new schema
            CreateV2Schema(connection);

            // Maps to track old int IDs -> new UUIDs
            Dictionary<long, string> tagIdMap = [];
            Dictionary<long, string> fileIdMap = [];

            // Migrate Tags
            using (SqliteCommand selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = "SELECT Id, Name, ColorHex, Emoji FROM Tags_Old";
                using SqliteDataReader reader = selectCmd.ExecuteReader();

                using SqliteCommand insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO Tags (Id, Name, ColorHex, Emoji) VALUES (@Id, @Name, @Color, @Emoji)";

                var idParam = insertCmd.Parameters.Add("@Id", (SqliteType)System.Data.DbType.String);
                var nameParam = insertCmd.Parameters.Add("@Name", (SqliteType)System.Data.DbType.String);
                var colorParam = insertCmd.Parameters.Add("@Color", (SqliteType)System.Data.DbType.String);
                var emojiParam = insertCmd.Parameters.Add("@Emoji", (SqliteType)System.Data.DbType.String);

                while (reader.Read())
                {
                    long oldId = reader.GetInt64(0);
                    string newId = Guid.NewGuid().ToString("N");
                    tagIdMap[oldId] = newId;

                    idParam.Value = newId;
                    nameParam.Value = reader.GetString(1);
                    colorParam.Value = reader.GetString(2);
                    emojiParam.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
                    _ = insertCmd.ExecuteNonQuery();
                }
            }

            // Migrate Files
            using (SqliteCommand selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = "SELECT Id, Path, Name, Hash, DateAdded, IsMissing FROM Files_Old";
                using SqliteDataReader reader = selectCmd.ExecuteReader();

                using SqliteCommand insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT INTO Files (Id, Path, Name, Hash, DateAdded, IsMissing) VALUES (@Id, @Path, @Name, @Hash, @DateAdded, @IsMissing)";

                var idParam = insertCmd.Parameters.Add("@Id", (SqliteType)System.Data.DbType.String);
                var pathParam = insertCmd.Parameters.Add("@Path", (SqliteType)System.Data.DbType.String);
                var nameParam = insertCmd.Parameters.Add("@Name", (SqliteType)System.Data.DbType.String);
                var hashParam = insertCmd.Parameters.Add("@Hash", (SqliteType)System.Data.DbType.String);
                var dateParam = insertCmd.Parameters.Add("@DateAdded", (SqliteType)System.Data.DbType.Int64);
                var missingParam = insertCmd.Parameters.Add("@IsMissing", (SqliteType)System.Data.DbType.Int32);

                while (reader.Read())
                {
                    long oldId = reader.GetInt64(0);
                    string newId = Guid.NewGuid().ToString("N");
                    fileIdMap[oldId] = newId;

                    idParam.Value = newId;
                    pathParam.Value = reader.GetString(1);
                    nameParam.Value = reader.GetString(2);
                    hashParam.Value = reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3);
                    dateParam.Value = reader.GetInt64(4);
                    missingParam.Value = reader.GetBoolean(5) ? 1 : 0;
                    _ = insertCmd.ExecuteNonQuery();
                }
            }

            // Migrate FileTags
            using (SqliteCommand selectCmd = connection.CreateCommand())
            {
                selectCmd.Transaction = transaction;
                selectCmd.CommandText = "SELECT FileId, TagId FROM FileTags_Old";
                using SqliteDataReader reader = selectCmd.ExecuteReader();

                using SqliteCommand insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT OR IGNORE INTO FileTags (FileId, TagId) VALUES (@FileId, @TagId)";

                var fileIdParam = insertCmd.Parameters.Add("@FileId", (SqliteType)System.Data.DbType.String);
                var tagIdParam = insertCmd.Parameters.Add("@TagId", (SqliteType)System.Data.DbType.String);

                while (reader.Read())
                {
                    long oldFileId = reader.GetInt64(0);
                    long oldTagId = reader.GetInt64(1);

                    if (fileIdMap.TryGetValue(oldFileId, out string? newFileId) &&
                        tagIdMap.TryGetValue(oldTagId, out string? newTagId))
                    {
                        fileIdParam.Value = newFileId;
                        tagIdParam.Value = newTagId;
                        _ = insertCmd.ExecuteNonQuery();
                    }
                }
            }

            // Drop old tables
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = @"
                    DROP TABLE Tags_Old;
                    DROP TABLE Files_Old;
                    DROP TABLE FileTags_Old;
                ";
                _ = cmd.ExecuteNonQuery();
            }

            transaction.Commit();

            // Rename thumbnail files
            RenameThumbnails(fileIdMap);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static void RenameThumbnails(Dictionary<long, string> fileIdMap)
    {
        string cacheDir = AppDataHelper.GetThumbnailCacheDirectory();
        if (!Directory.Exists(cacheDir))
        {
            return;
        }

        foreach (var kvp in fileIdMap)
        {
            string oldPath = Path.Combine(cacheDir, $"{kvp.Key}.png");
            string newPath = Path.Combine(cacheDir, $"{kvp.Value}.png");

            if (File.Exists(oldPath))
            {
                try
                {
                    File.Move(oldPath, newPath, overwrite: true);
                }
                catch
                {
                    // Best effort - thumbnails can be regenerated
                }
            }
        }
    }
}