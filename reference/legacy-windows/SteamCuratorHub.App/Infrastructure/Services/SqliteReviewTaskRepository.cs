using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using SteamCuratorHub.Application.Abstractions;
using SteamCuratorHub.Domain.Models;

namespace SteamCuratorHub.Infrastructure.Services;

/// <summary>
/// SQLite-backed review task store. The database is created and migrated lazily,
/// allowing the desktop app to start with an empty profile and survive restarts.
/// </summary>
public sealed class SqliteReviewTaskRepository : IReviewTaskRepository
{
    private readonly string _connectionString;
    private readonly ulong? _expectedSteamId;

    public SqliteReviewTaskRepository(string? databasePath = null, ulong? expectedSteamId = null)
    {
        if (expectedSteamId == 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSteamId), "SteamID must be greater than zero.");

        _expectedSteamId = expectedSteamId;
        DatabasePath = ResolveDatabasePath(databasePath, expectedSteamId);
        ValidateExistingDatabaseBeforeMigration(DatabasePath, expectedSteamId);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();

        EnsureSchema();
        BindOrValidateAccountScope();
    }

    public string DatabasePath { get; }

    /// <summary>
    /// Resolves the persistence location without touching the file system. An explicit
    /// path always wins. Account defaults are isolated from the legacy unscoped store.
    /// </summary>
    public static string ResolveDatabasePath(string? databasePath, ulong? steamId)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
            return Path.GetFullPath(databasePath);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamCuratorHub");
        return steamId is > 0
            ? Path.Combine(root, "accounts", steamId.Value.ToString(CultureInfo.InvariantCulture), "steam-curator-hub.db")
            : Path.Combine(root, "steam-curator-hub.db");
    }

    public IReadOnlyList<ReviewTask> GetAll()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, AppId, NameEnglish, NameChinese, HeaderImageUrl, TargetHours, DueDate, PlayedHours, Status, PlaythroughStatus, CuratorGroup, IsCurrent, CreatedAt, UpdatedAt, LastError FROM ReviewTasks ORDER BY UpdatedAt DESC";
        var tasks = new List<ReviewTask>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                tasks.Add(ReadTask(reader));
        }
        foreach (var task in tasks)
            LoadChildren(connection, task);
        return tasks;
    }

    public ReviewTask? GetById(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, AppId, NameEnglish, NameChinese, HeaderImageUrl, TargetHours, DueDate, PlayedHours, Status, PlaythroughStatus, CuratorGroup, IsCurrent, CreatedAt, UpdatedAt, LastError FROM ReviewTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        ReviewTask? task;
        using (var reader = command.ExecuteReader())
        {
            task = reader.Read() ? ReadTask(reader) : null;
        }
        if (task is null)
            return null;
        LoadChildren(connection, task);
        return task;
    }

    public void Add(ReviewTask task) => Save(task);

    public void Update(ReviewTask task) => Save(task);

    public void SetCurrent(Guid? id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (id.HasValue)
        {
            using var existsCommand = connection.CreateCommand();
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(1) FROM ReviewTasks WHERE Id = $id";
            existsCommand.Parameters.AddWithValue("$id", id.Value.ToString("D"));
            if (Convert.ToInt32(existsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                throw new KeyNotFoundException($"Task '{id.Value:D}' does not exist.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = id.HasValue
            ? "UPDATE ReviewTasks SET IsCurrent = CASE WHEN Id = $id THEN 1 ELSE 0 END WHERE IsCurrent = 1 OR Id = $id"
            : "UPDATE ReviewTasks SET IsCurrent = 0 WHERE IsCurrent = 1";
        if (id.HasValue)
            command.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Delete(Guid id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ReviewTasks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    private void Save(ReviewTask task)
    {
        task.UpdatedAt = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO ReviewTasks
                (Id, AppId, NameEnglish, NameChinese, HeaderImageUrl, TargetHours, DueDate,
                 PlayedHours, Status, PlaythroughStatus, CuratorGroup, IsCurrent, CreatedAt,
                 UpdatedAt, LastError)
                VALUES ($id, $appId, $nameEnglish, $nameChinese, $headerImageUrl, $targetHours,
                        $dueDate, $playedHours, $status, $playthroughStatus, $curatorGroup,
                        $isCurrent, $createdAt, $updatedAt, $lastError)
                """;
            command.Parameters.AddWithValue("$id", task.Id.ToString("D"));
            command.Parameters.AddWithValue("$appId", task.Game.AppId);
            command.Parameters.AddWithValue("$nameEnglish", task.Game.NameEnglish);
            command.Parameters.AddWithValue("$nameChinese", task.Game.NameChinese);
            command.Parameters.AddWithValue("$headerImageUrl", (object?)task.Game.HeaderImageUrl ?? DBNull.Value);
            command.Parameters.AddWithValue("$targetHours", task.TargetHours);
            command.Parameters.AddWithValue("$dueDate", FormatDateOnly(task.DueDate));
            command.Parameters.AddWithValue("$playedHours", task.PlayedHours);
            command.Parameters.AddWithValue("$status", (int)task.Status);
            command.Parameters.AddWithValue("$playthroughStatus", (int)task.PlaythroughStatus);
            command.Parameters.AddWithValue("$curatorGroup", (object?)task.CuratorGroup ?? DBNull.Value);
            command.Parameters.AddWithValue("$isCurrent", task.IsCurrent ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", FormatDate(task.CreatedAt));
            command.Parameters.AddWithValue("$updatedAt", FormatDate(task.UpdatedAt));
            command.Parameters.AddWithValue("$lastError", (object?)task.LastError ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        // Child collections are replaced as one unit. This keeps edits deterministic
        // and also removes stale achievements/drafts after a user deletes them.
        DeleteChildren(connection, transaction, task.Id);
        foreach (var achievement in task.Achievements)
            InsertAchievement(connection, transaction, task.Id, achievement);
        foreach (var draft in task.Drafts)
            InsertDraft(connection, transaction, task.Id, draft);
        foreach (var publication in task.Publications)
            InsertPublication(connection, transaction, task.Id, publication);
        foreach (var copy in task.CuratorCopies)
            InsertCuratorCopy(connection, transaction, task.Id, copy);

        transaction.Commit();
    }

    private void EnsureSchema()
    {
        using var connection = OpenRawConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS AccountMetadata (
                MetadataKey TEXT PRIMARY KEY,
                MetadataValue TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ReviewTasks (
                Id TEXT PRIMARY KEY,
                AppId INTEGER NOT NULL,
                NameEnglish TEXT NOT NULL,
                NameChinese TEXT NOT NULL,
                HeaderImageUrl TEXT NULL,
                TargetHours REAL NOT NULL,
                DueDate TEXT NOT NULL,
                PlayedHours REAL NOT NULL,
                Status INTEGER NOT NULL,
                PlaythroughStatus INTEGER NOT NULL,
                CuratorGroup TEXT NULL,
                IsCurrent INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                LastError TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Achievements (
                TaskId TEXT NOT NULL,
                AchievementId TEXT NOT NULL,
                NameEnglish TEXT NOT NULL,
                NameChinese TEXT NOT NULL,
                DescriptionEnglish TEXT NOT NULL,
                DescriptionChinese TEXT NOT NULL,
                IsUnlocked INTEGER NOT NULL,
                UnlockedAt TEXT NULL,
                Progress REAL NOT NULL,
                PRIMARY KEY (TaskId, AchievementId),
                FOREIGN KEY (TaskId) REFERENCES ReviewTasks(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS Drafts (
                Id TEXT PRIMARY KEY,
                TaskId TEXT NOT NULL,
                Type INTEGER NOT NULL,
                Language TEXT NOT NULL,
                Content TEXT NOT NULL,
                SteamFormattedContent TEXT NULL,
                RecommendationState INTEGER NULL,
                Version INTEGER NOT NULL,
                IsPublished INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (TaskId) REFERENCES ReviewTasks(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS Publications (
                Id TEXT PRIMARY KEY,
                TaskId TEXT NOT NULL,
                Type INTEGER NOT NULL,
                RecommendationState INTEGER NULL,
                PublishedAt TEXT NOT NULL,
                Succeeded INTEGER NOT NULL,
                SteamUrl TEXT NULL,
                ContentSnapshot TEXT NULL,
                ErrorMessage TEXT NULL,
                FOREIGN KEY (TaskId) REFERENCES ReviewTasks(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS CuratorCopies (
                Id TEXT PRIMARY KEY,
                TaskId TEXT NOT NULL,
                Status TEXT NOT NULL,
                SteamOfferId TEXT NULL,
                AppId INTEGER NULL,
                GameName TEXT NULL,
                ClaimedAt TEXT NULL,
                ExpiresAt TEXT NULL,
                ImportedAt TEXT NULL,
                Notes TEXT NULL,
                FOREIGN KEY (TaskId) REFERENCES ReviewTasks(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_ReviewTasks_UpdatedAt ON ReviewTasks(UpdatedAt);
            CREATE INDEX IF NOT EXISTS IX_Achievements_TaskId ON Achievements(TaskId);
            CREATE INDEX IF NOT EXISTS IX_Drafts_TaskId ON Drafts(TaskId);
            CREATE INDEX IF NOT EXISTS IX_Publications_TaskId ON Publications(TaskId);
            CREATE INDEX IF NOT EXISTS IX_CuratorCopies_TaskId ON CuratorCopies(TaskId);
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "Drafts", "RecommendationState", "INTEGER NULL");
        EnsureColumn(connection, "Publications", "RecommendationState", "INTEGER NULL");
        EnsureColumn(connection, "CuratorCopies", "SteamOfferId", "TEXT NULL");
        EnsureColumn(connection, "CuratorCopies", "AppId", "INTEGER NULL");
        EnsureColumn(connection, "CuratorCopies", "GameName", "TEXT NULL");
        EnsureColumn(connection, "CuratorCopies", "ExpiresAt", "TEXT NULL");
        EnsureColumn(connection, "CuratorCopies", "ImportedAt", "TEXT NULL");
        using var offerIndexCommand = connection.CreateCommand();
        offerIndexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_CuratorCopies_TaskId_SteamOfferId ON CuratorCopies(TaskId, SteamOfferId) WHERE SteamOfferId IS NOT NULL";
        offerIndexCommand.ExecuteNonQuery();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string declaration)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        using (var reader = checkCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {declaration}";
        alterCommand.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = OpenRawConnection();
        try
        {
            ValidateOpenedConnectionAccountScope(connection, _expectedSteamId);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection OpenRawConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void ValidateExistingDatabaseBeforeMigration(string databasePath, ulong? expectedSteamId)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
            return;

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true
        }.ToString();

        try
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var storedSteamId = ReadStoredSteamId(connection);
            if (storedSteamId.HasValue)
            {
                ValidateAccountMatch(storedSteamId.Value, expectedSteamId);
                return;
            }

            if (expectedSteamId.HasValue && ContainsUserData(connection))
            {
                throw new SteamAccountDatabaseException(
                    "The selected database contains unscoped data and cannot be assigned to a Steam account automatically. Use a new database path or migrate it explicitly.");
            }
        }
        catch (SteamAccountDatabaseException)
        {
            throw;
        }
        catch (SqliteException ex)
        {
            throw new SteamAccountDatabaseException("The selected database could not be validated safely.", ex);
        }
    }

    private void BindOrValidateAccountScope()
    {
        using var connection = OpenRawConnection();
        var storedSteamId = ReadStoredSteamId(connection);
        if (storedSteamId.HasValue)
        {
            ValidateAccountMatch(storedSteamId.Value, _expectedSteamId);
            return;
        }

        if (!_expectedSteamId.HasValue)
            return;
        if (ContainsUserData(connection))
        {
            throw new SteamAccountDatabaseException(
                "The selected database contains unscoped data and cannot be assigned to a Steam account automatically. Use a new database path or migrate it explicitly.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO AccountMetadata (MetadataKey, MetadataValue) VALUES ('SteamId', $steamId)";
        command.Parameters.AddWithValue("$steamId", _expectedSteamId.Value.ToString(CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static void ValidateOpenedConnectionAccountScope(SqliteConnection connection, ulong? expectedSteamId)
    {
        var storedSteamId = ReadStoredSteamId(connection);
        if (!storedSteamId.HasValue)
        {
            if (expectedSteamId.HasValue)
                throw new SteamAccountDatabaseException("The database has no Steam account binding.");
            return;
        }

        ValidateAccountMatch(storedSteamId.Value, expectedSteamId);
    }

    private static void ValidateAccountMatch(ulong storedSteamId, ulong? expectedSteamId)
    {
        if (!expectedSteamId.HasValue)
            throw new SteamAccountDatabaseException("The database is bound to a Steam account, but no SteamID is configured.");
        if (storedSteamId != expectedSteamId.Value)
            throw new SteamAccountDatabaseException("The database belongs to a different Steam account.");
    }

    private static ulong? ReadStoredSteamId(SqliteConnection connection)
    {
        if (!TableExists(connection, "AccountMetadata"))
            return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MetadataValue FROM AccountMetadata WHERE MetadataKey = 'SteamId' LIMIT 1";
        var value = command.ExecuteScalar() as string;
        if (value is null)
            return null;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) || steamId == 0)
            throw new SteamAccountDatabaseException("The database contains an invalid Steam account binding.");
        return steamId;
    }

    private static bool ContainsUserData(SqliteConnection connection)
    {
        foreach (var table in new[] { "ReviewTasks", "Achievements", "Drafts", "Publications", "CuratorCopies" })
        {
            if (!TableExists(connection, table))
                continue;
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT EXISTS(SELECT 1 FROM \"{table}\" LIMIT 1)";
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                return true;
        }
        return false;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name)";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static ReviewTask ReadTask(SqliteDataReader reader)
    {
        var appId = Convert.ToUInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        var game = new Game(
            appId,
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
        return new ReviewTask
        {
            Id = Guid.Parse(reader.GetString(0)),
            Game = game,
            TargetHours = reader.GetDouble(5),
            // Due dates are calendar dates from the user's perspective. Keep them
            // timezone-neutral so UTC conversion cannot move a date backwards.
            DueDate = ParseDateOnly(reader.GetString(6)),
            PlayedHours = reader.GetDouble(7),
            Status = (ReviewTaskStatus)reader.GetInt32(8),
            PlaythroughStatus = (PlaythroughStatus)reader.GetInt32(9),
            CuratorGroup = reader.IsDBNull(10) ? null : reader.GetString(10),
            IsCurrent = reader.GetInt32(11) != 0,
            CreatedAt = ParseDate(reader.GetString(12)),
            UpdatedAt = ParseDate(reader.GetString(13)),
            LastError = reader.IsDBNull(14) ? null : reader.GetString(14)
        };
    }

    private static void LoadChildren(SqliteConnection connection, ReviewTask task)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT AchievementId, NameEnglish, NameChinese, DescriptionEnglish, DescriptionChinese, IsUnlocked, UnlockedAt, Progress FROM Achievements WHERE TaskId = $taskId ORDER BY AchievementId";
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            var loadedAchievements = new List<Achievement>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                loadedAchievements.Add(new Achievement
                {
                    Id = reader.GetString(0),
                    NameEnglish = reader.GetString(1),
                    NameChinese = reader.GetString(2),
                    DescriptionEnglish = reader.GetString(3),
                    DescriptionChinese = reader.GetString(4),
                    IsUnlocked = reader.GetInt32(5) != 0,
                    UnlockedAt = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
                    Progress = reader.GetDouble(7)
                });
            }
            foreach (var achievement in loadedAchievements)
                task.Achievements.Add(achievement);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Type, Language, Content, SteamFormattedContent, Version, IsPublished, CreatedAt, UpdatedAt, RecommendationState FROM Drafts WHERE TaskId = $taskId ORDER BY UpdatedAt DESC";
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                task.Drafts.Add(new Draft
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ReviewTaskId = task.Id,
                    Type = (DraftType)reader.GetInt32(1),
                    Language = reader.GetString(2),
                    Content = reader.GetString(3),
                    SteamFormattedContent = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Version = reader.GetInt32(5),
                    IsPublished = reader.GetInt32(6) != 0,
                    CreatedAt = ParseDate(reader.GetString(7)),
                    UpdatedAt = ParseDate(reader.GetString(8)),
                    RecommendationState = reader.IsDBNull(9) ? null : (CuratorRecommendationState)reader.GetInt32(9)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Type, PublishedAt, Succeeded, SteamUrl, ContentSnapshot, ErrorMessage, RecommendationState FROM Publications WHERE TaskId = $taskId ORDER BY PublishedAt DESC";
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                task.Publications.Add(new Publication
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ReviewTaskId = task.Id,
                    Type = (PublicationType)reader.GetInt32(1),
                    PublishedAt = ParseDate(reader.GetString(2)),
                    Succeeded = reader.GetInt32(3) != 0,
                    SteamUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                    ContentSnapshot = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ErrorMessage = reader.IsDBNull(6) ? null : reader.GetString(6),
                    RecommendationState = reader.IsDBNull(7) ? null : (CuratorRecommendationState)reader.GetInt32(7)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Status, ClaimedAt, Notes, SteamOfferId, AppId, GameName, ExpiresAt, ImportedAt FROM CuratorCopies WHERE TaskId = $taskId ORDER BY ClaimedAt DESC";
            command.Parameters.AddWithValue("$taskId", task.Id.ToString("D"));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                task.CuratorCopies.Add(new CuratorCopy
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    ReviewTaskId = task.Id,
                    Status = reader.GetString(1),
                    ClaimedAt = reader.IsDBNull(2) ? null : ParseDate(reader.GetString(2)),
                    Notes = reader.IsDBNull(3) ? null : reader.GetString(3),
                    SteamOfferId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    AppId = reader.IsDBNull(5) ? 0 : Convert.ToUInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
                    GameName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    ExpiresAt = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                    ImportedAt = reader.IsDBNull(8) ? task.CreatedAt : ParseDate(reader.GetString(8))
                });
            }
        }
    }

    private static void DeleteChildren(SqliteConnection connection, SqliteTransaction transaction, Guid taskId)
    {
        foreach (var table in new[] { "Achievements", "Drafts", "Publications", "CuratorCopies" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE TaskId = $taskId";
            command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertAchievement(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, Achievement value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO Achievements (TaskId, AchievementId, NameEnglish, NameChinese, DescriptionEnglish, DescriptionChinese, IsUnlocked, UnlockedAt, Progress) VALUES ($taskId, $id, $nameEnglish, $nameChinese, $descriptionEnglish, $descriptionChinese, $isUnlocked, $unlockedAt, $progress)";
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$id", value.Id);
        command.Parameters.AddWithValue("$nameEnglish", value.NameEnglish);
        command.Parameters.AddWithValue("$nameChinese", value.NameChinese);
        command.Parameters.AddWithValue("$descriptionEnglish", value.DescriptionEnglish);
        command.Parameters.AddWithValue("$descriptionChinese", value.DescriptionChinese);
        command.Parameters.AddWithValue("$isUnlocked", value.IsUnlocked ? 1 : 0);
        command.Parameters.AddWithValue("$unlockedAt", value.UnlockedAt.HasValue ? FormatDate(value.UnlockedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$progress", value.Progress);
        command.ExecuteNonQuery();
    }

    private static void InsertDraft(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, Draft value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO Drafts (Id, TaskId, Type, Language, Content, SteamFormattedContent, RecommendationState, Version, IsPublished, CreatedAt, UpdatedAt) VALUES ($id, $taskId, $type, $language, $content, $steamContent, $recommendationState, $version, $isPublished, $createdAt, $updatedAt)";
        command.Parameters.AddWithValue("$id", value.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$type", (int)value.Type);
        command.Parameters.AddWithValue("$language", value.Language);
        command.Parameters.AddWithValue("$content", value.Content);
        command.Parameters.AddWithValue("$steamContent", (object?)value.SteamFormattedContent ?? DBNull.Value);
        command.Parameters.AddWithValue("$recommendationState", value.RecommendationState.HasValue ? (int)value.RecommendationState.Value : DBNull.Value);
        command.Parameters.AddWithValue("$version", value.Version);
        command.Parameters.AddWithValue("$isPublished", value.IsPublished ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", FormatDate(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(value.UpdatedAt));
        command.ExecuteNonQuery();
    }

    private static void InsertPublication(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, Publication value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO Publications (Id, TaskId, Type, RecommendationState, PublishedAt, Succeeded, SteamUrl, ContentSnapshot, ErrorMessage) VALUES ($id, $taskId, $type, $recommendationState, $publishedAt, $succeeded, $steamUrl, $contentSnapshot, $errorMessage)";
        command.Parameters.AddWithValue("$id", value.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$type", (int)value.Type);
        command.Parameters.AddWithValue("$recommendationState", value.RecommendationState.HasValue ? (int)value.RecommendationState.Value : DBNull.Value);
        command.Parameters.AddWithValue("$publishedAt", FormatDate(value.PublishedAt));
        command.Parameters.AddWithValue("$succeeded", value.Succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$steamUrl", (object?)value.SteamUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$contentSnapshot", (object?)value.ContentSnapshot ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)value.ErrorMessage ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertCuratorCopy(SqliteConnection connection, SqliteTransaction transaction, Guid taskId, CuratorCopy value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO CuratorCopies (Id, TaskId, Status, SteamOfferId, AppId, GameName, ClaimedAt, ExpiresAt, ImportedAt, Notes) VALUES ($id, $taskId, $status, $steamOfferId, $appId, $gameName, $claimedAt, $expiresAt, $importedAt, $notes)";
        command.Parameters.AddWithValue("$id", value.Id.ToString("D"));
        command.Parameters.AddWithValue("$taskId", taskId.ToString("D"));
        command.Parameters.AddWithValue("$status", value.Status);
        command.Parameters.AddWithValue("$steamOfferId", (object?)value.SteamOfferId ?? DBNull.Value);
        command.Parameters.AddWithValue("$appId", value.AppId > 0 ? value.AppId : DBNull.Value);
        command.Parameters.AddWithValue("$gameName", string.IsNullOrWhiteSpace(value.GameName) ? DBNull.Value : value.GameName);
        command.Parameters.AddWithValue("$claimedAt", value.ClaimedAt.HasValue ? FormatDate(value.ClaimedAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$expiresAt", value.ExpiresAt.HasValue ? FormatDate(value.ExpiresAt.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", FormatDate(value.ImportedAt));
        command.Parameters.AddWithValue("$notes", (object?)value.Notes ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string FormatDate(DateTime value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDateOnly(DateTime value) => value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime ParseDate(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTime ParseDateOnly(string value) => DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None);
}

public sealed class SteamAccountDatabaseException : InvalidOperationException
{
    public SteamAccountDatabaseException(string message) : base(message) { }
    public SteamAccountDatabaseException(string message, Exception innerException) : base(message, innerException) { }
}
