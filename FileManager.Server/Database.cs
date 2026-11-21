using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FileManager.Server;

public sealed class Database
{
    private const int Iterations = 210_000;
    private readonly string _connectionString;

    public Database(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE,
                Salt BLOB NOT NULL,
                PasswordHash BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL REFERENCES Users(Id),
                Name TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(UserId, Name)
            );
            CREATE TABLE IF NOT EXISTS Operations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL REFERENCES Users(Id),
                Action TEXT NOT NULL,
                FileName TEXT,
                Details TEXT,
                HappenedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                TokenHash BLOB PRIMARY KEY,
                UserId INTEGER NOT NULL REFERENCES Users(Id),
                ExpiresAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return db;
    }

    public int Register(string username, string password)
    {
        if (username is null || !Regex.IsMatch(username, "^[A-Za-z0-9_]{3,24}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Логин: 3–24 латинские буквы, цифры или '_'.");
        if (password is null || password.Length < 8 || password.Length > 256)
            throw new ArgumentException("Пароль: от 8 до 256 символов.");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO Users (Username, Salt, PasswordHash) VALUES ($user,$salt,$hash) RETURNING Id;";
        command.Parameters.AddWithValue("$user", username);
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$hash", hash);
        try
        {
            int id = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            Log(id, "REGISTER");
            return id;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("Логин уже занят.", ex);
        }
    }

    public LoginReply? Login(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || username.Length > 128 || password is null || password.Length > 256)
            return null;
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Id, Salt, PasswordHash FROM Users WHERE Username = $user;";
        command.Parameters.AddWithValue("$user", username);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        int id = reader.GetInt32(0);
        byte[] salt = reader.GetFieldValue<byte[]>(1);
        byte[] storedHash = reader.GetFieldValue<byte[]>(2);
        reader.Close();
        byte[] attempt = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        if (!CryptographicOperations.FixedTimeEquals(storedHash, attempt))
        {
            Log(id, "LOGIN_FAILED");
            return null;
        }
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] tokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        DateTime expires = DateTime.UtcNow.AddHours(8);
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO Sessions(TokenHash,UserId,ExpiresAt) VALUES($hash,$user,$expires);";
        insert.Parameters.AddWithValue("$hash", tokenHash);
        insert.Parameters.AddWithValue("$user", id);
        insert.Parameters.AddWithValue("$expires", expires.ToString("O"));
        insert.ExecuteNonQuery();
        Log(id, "LOGIN");
        return new LoginReply(token, id, expires);
    }

    public int? FindSession(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        byte[] tokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT UserId FROM Sessions WHERE TokenHash = $hash AND ExpiresAt > $now;";
        command.Parameters.AddWithValue("$hash", tokenHash);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        object? value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Logout(string token, int userId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE TokenHash = $hash AND UserId = $user;";
        command.Parameters.AddWithValue("$hash", SHA256.HashData(Encoding.ASCII.GetBytes(token)));
        command.Parameters.AddWithValue("$user", userId);
        command.ExecuteNonQuery();
        Log(userId, "LOGOUT");
    }

    public IReadOnlyList<FileEntry> ListFiles(int userId, string? search)
    {
        if (search?.Length > 200) throw new ArgumentException("Поисковый запрос слишком длинный.");
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT Name, SizeBytes, UpdatedAt FROM Files
            WHERE UserId = $user AND ($search = '' OR instr(Name, $search) > 0)
            ORDER BY Name;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$search", search ?? "");
        using var reader = command.ExecuteReader();
        var result = new List<FileEntry>();
        while (reader.Read()) result.Add(new FileEntry(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        reader.Close();
        Log(userId, "LIST_FILES");
        return result;
    }

    public IReadOnlyList<AuditEntry> History(int userId)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT HappenedAt, Action, FileName, Details
            FROM Operations WHERE UserId = $user ORDER BY Id DESC LIMIT 30;
            """;
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var rows = new List<AuditEntry>();
        while (reader.Read()) rows.Add(new AuditEntry(reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        reader.Close();
        Log(userId, "VIEW_HISTORY");
        return rows;
    }

    public void SaveFile(int userId, string fileName, long length, string action)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO Files(UserId,Name,SizeBytes) VALUES($user,$name,$bytes)
                ON CONFLICT(UserId,Name) DO UPDATE SET
                SizeBytes=excluded.SizeBytes, UpdatedAt=CURRENT_TIMESTAMP;
                """;
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$name", fileName);
            command.Parameters.AddWithValue("$bytes", length);
            command.ExecuteNonQuery();
        }
        WriteLog(db, tx, userId, action, fileName, null);
        tx.Commit();
    }

    public void SaveExtracted(int userId, List<(string Name, long Size)> files, string archive)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var file in files)
        {
            using var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO Files(UserId,Name,SizeBytes) VALUES($user,$name,$size);";
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$name", file.Name);
            command.Parameters.AddWithValue("$size", file.Size);
            command.ExecuteNonQuery();
            WriteLog(db, tx, userId, "EXTRACT_FILE", file.Name, archive);
        }
        WriteLog(db, tx, userId, "EXTRACT_ZIP", archive, $"Files: {files.Count}");
        tx.Commit();
    }

    public void DeleteFile(int userId, string fileName)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var command = db.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "DELETE FROM Files WHERE UserId = $user AND Name = $name;";
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$name", fileName);
            command.ExecuteNonQuery();
        }
        WriteLog(db, tx, userId, "DELETE", fileName, null);
        tx.Commit();
    }

    public void Log(int userId, string action, string? name = null, string? details = null)
    {
        using var db = Open();
        WriteLog(db, null, userId, action, name, details);
    }

    private static void WriteLog(SqliteConnection db, SqliteTransaction? tx, int userId,
        string action, string? name, string? details)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO Operations(UserId,Action,FileName,Details)
            VALUES($user,$action,$name,$details);
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$details", (object?)details ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
