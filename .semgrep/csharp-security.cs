// Semgrep fixtures only: parsed by --test, never compiled into the application.
using System;
using System.IO;
using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

class SecurityRuleExamples
{
    void Deserialization()
    {
        // ruleid: securefilemanager-csharp-insecure-binaryformatter
        var bad = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
        // ok: securefilemanager-csharp-insecure-binaryformatter
        var good = JsonSerializer.Deserialize<Person>("{\"Name\":\"Alice\"}");
    }

    void Xml()
    {
        var settings = new XmlReaderSettings
        {
            // ruleid: securefilemanager-csharp-xxe-dtd-parsing
            DtdProcessing = DtdProcessing.Parse,
            // ruleid: securefilemanager-csharp-external-xml-resolver
            XmlResolver = new XmlUrlResolver()
        };
        // ruleid: securefilemanager-csharp-xxe-dtd-parsing
        settings.DtdProcessing = System.Xml.DtdProcessing.Parse;
        // ruleid: securefilemanager-csharp-external-xml-resolver
        settings.XmlResolver = new System.Xml.XmlUrlResolver();
        var safe = new XmlReaderSettings
        {
            // ok: securefilemanager-csharp-xxe-dtd-parsing
            DtdProcessing = DtdProcessing.Prohibit,
            // ok: securefilemanager-csharp-external-xml-resolver
            XmlResolver = null
        };
        // ok: securefilemanager-csharp-xxe-dtd-parsing
        bool comparisonIsNotConfiguration = safe.DtdProcessing == DtdProcessing.Parse;
    }

    void Sql(SqliteConnection db, string username)
    {
        using var cmd = db.CreateCommand();
        // ruleid: securefilemanager-csharp-dynamic-sql
        cmd.CommandText = "SELECT Id FROM Users WHERE Username = '" + username + "'";
        string query = $"SELECT Id FROM Users WHERE Username = '{username}'";
        // ruleid: securefilemanager-csharp-dynamic-sql
        cmd.CommandText = query;
        // ruleid: securefilemanager-csharp-dynamic-sql
        var bad = new SqliteCommand(string.Format("SELECT Id FROM Users WHERE Username = '{0}'", username), db);
        // ok: securefilemanager-csharp-dynamic-sql
        cmd.CommandText = "SELECT Id FROM Users WHERE Username = $user";
        cmd.Parameters.AddWithValue("$user", username);
        // ok: securefilemanager-csharp-dynamic-sql
        cmd.CommandText = "SELECT Id " + "FROM Users WHERE Username = $user";
        // ok: securefilemanager-csharp-dynamic-sql
        var good = new SqliteCommand("SELECT Id FROM Users WHERE Username = $user", db);
    }

    void HttpPaths(HttpContext http, IFormFile upload)
    {
        string name = http.Request.Query["name"].ToString();
        string target = Path.Combine("/srv/storage/1", name);
        // ruleid: securefilemanager-csharp-http-path-traversal
        File.ReadAllText(target);
        // ruleid: securefilemanager-csharp-http-path-traversal
        File.WriteAllText(Path.Combine("/srv/storage/1", upload.FileName), "content");
        // ok: securefilemanager-csharp-http-path-traversal
        File.WriteAllText("/srv/storage/1/fixed.txt", name);
        FileService.ValidateName(name);
        // ok: securefilemanager-csharp-http-path-traversal
        File.ReadAllText(Path.Combine("/srv/storage/1", name));
    }

    void RequestPaths(HttpRequest request)
    {
        string name = request.RouteValues["name"].ToString();
        // ruleid: securefilemanager-csharp-http-path-traversal
        File.Delete(Path.Combine("/srv/storage/1", name));
        // ok: securefilemanager-csharp-http-path-traversal
        File.ReadAllText("/srv/storage/1/fixed.txt");
    }

    void ZipPaths(ZipArchive archive)
    {
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = Path.Combine("/srv/storage/1", entry.FullName);
            // ruleid: securefilemanager-csharp-zip-slip
            using var output = new FileStream(path, FileMode.CreateNew);
        }
    }

    void MinimalApi(WebApplication app)
    {
        app.MapGet("/unsafe/{name}", (string name) =>
        {
            // ruleid: securefilemanager-csharp-http-path-traversal
            return File.ReadAllText(Path.Combine("/srv/storage/1", name));
        });
        app.MapGet("/safe/{name}", (string name) =>
        {
            FileService.ValidateName(name);
            // ok: securefilemanager-csharp-http-path-traversal
            return File.ReadAllText(Path.Combine("/srv/storage/1", name));
        });
        app.MapPut("/move/{name}", (string name) =>
        {
            // ruleid: securefilemanager-csharp-http-path-traversal
            File.Move("/srv/storage/1/temp.txt", Path.Combine("/srv/storage/1", name));
        });
        app.MapPut("/copy/{name}", (string name) =>
        {
            // ruleid: securefilemanager-csharp-http-path-traversal
            File.Copy(Path.Combine("/srv/storage/1", name), "/srv/storage/1/copy.txt");
        });
    }

    void ValidatedZip(ZipArchiveEntry entry)
    {
        string name = entry.FullName;
        FileService.ValidateName(name);
        // ok: securefilemanager-csharp-zip-slip
        using var output = File.Create(Path.Combine("/srv/storage/1", name));
    }

    void Shell(string name, HttpContext http)
    {
        // ruleid: securefilemanager-csharp-shell-injection
        Process.Start("cmd.exe", "/c type " + name);
        string command = $"cat {name}";
        // ruleid: securefilemanager-csharp-shell-injection
        Process.Start("/bin/sh", command);
        // ruleid: securefilemanager-csharp-shell-injection
        Process.Start("pwsh", http.Request.Query["command"].ToString());
        // ok: securefilemanager-csharp-shell-injection
        Process.Start("/bin/sh", "-c 'echo ready'");
        var safe = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false };
        safe.ArgumentList.Add("--");
        safe.ArgumentList.Add(name);
        // ok: securefilemanager-csharp-shell-injection
        Process.Start(safe);
    }

    void Tls(HttpClientHandler handler)
    {
        // ruleid: securefilemanager-csharp-tls-validation-bypass
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // ruleid: securefilemanager-csharp-tls-validation-bypass
        handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => true;
        // ok: securefilemanager-csharp-tls-validation-bypass
        handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => errors == SslPolicyErrors.None;
        // ok: securefilemanager-csharp-tls-validation-bypass
        var defaultHandler = new HttpClientHandler();
    }

    void ShellStartInfo(string name)
    {
        // ruleid: securefilemanager-csharp-shell-injection
        var bad = new ProcessStartInfo("/bin/sh", "-c 'cat " + name + "'");
        // ruleid: securefilemanager-csharp-shell-injection
        var alsoBad = new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/c type {name}" };
        // ok: securefilemanager-csharp-shell-injection
        var good = new ProcessStartInfo("/usr/bin/stat") { UseShellExecute = false };
        good.ArgumentList.Add("--");
        good.ArgumentList.Add(name);
    }

    void Passwords(string password, byte[] salt, byte[] fileContent)
    {
        // ruleid: securefilemanager-csharp-weak-password-hash
        byte[] weak = MD5.HashData(Encoding.UTF8.GetBytes(password));
        // ruleid: securefilemanager-csharp-weak-password-hash
        byte[] alsoWeak = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        // ok: securefilemanager-csharp-weak-password-hash
        byte[] checksum = MD5.HashData(fileContent);
        // ruleid: securefilemanager-csharp-low-pbkdf2-work-factor
        var low = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA256, 32);
        const int TooLow = 209_999;
        // ruleid: securefilemanager-csharp-low-pbkdf2-work-factor
        var boundaryLow = Rfc2898DeriveBytes.Pbkdf2(password, salt, TooLow, HashAlgorithmName.SHA256, 32);
        // ok: securefilemanager-csharp-low-pbkdf2-work-factor, securefilemanager-csharp-weak-password-hash
        var good = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        // ok: securefilemanager-csharp-low-pbkdf2-work-factor
        var stronger = Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, HashAlgorithmName.SHA256, 32);
    }

    void Encryption(Aes cipher, byte[] data, byte[] externalKey)
    {
        // ruleid: securefilemanager-csharp-hardcoded-encryption-key
        cipher.Key = Encoding.UTF8.GetBytes("0123456789abcdef");
        // ruleid: securefilemanager-csharp-hardcoded-encryption-key
        cipher.Key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        // ok: securefilemanager-csharp-hardcoded-encryption-key
        cipher.Key = RandomNumberGenerator.GetBytes(32);
        // ok: securefilemanager-csharp-hardcoded-encryption-key
        cipher.Key = externalKey;
        // ruleid: securefilemanager-csharp-ecb-mode
        cipher.Mode = CipherMode.ECB;
        // ruleid: securefilemanager-csharp-ecb-mode
        cipher.EncryptEcb(data, PaddingMode.PKCS7);
        // ok: securefilemanager-csharp-ecb-mode
        var authenticatedCipher = new AesGcm(externalKey, 16);
    }

    void Logs(ILogger logger, string password, string token, int userId)
    {
        // ruleid: securefilemanager-csharp-secret-in-logs
        logger.LogInformation("Login password: {Password}", password);
        // ruleid: securefilemanager-csharp-secret-in-logs
        logger.LogDebug("Bearer {Token}", token);
        // ok: securefilemanager-csharp-secret-in-logs
        logger.LogInformation("Login failed for user {UserId}", userId);
        // ok: securefilemanager-csharp-secret-in-logs
        logger.LogInformation("Authentication failed");
    }

    class Person { public string Name { get; set; } }
}

// Mirror the reviewed project boundary; these methods are NOT executed by --test.
class FileService
{
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$") ||
            name.Contains("..") || name != Path.GetFileName(name))
            throw new ArgumentException();
    }

    string PathFor(string name)
    {
        ValidateName(name);
        return Path.Combine("/srv/storage/1", name);
    }

    void ActualProjectPattern(ZipArchiveEntry entry)
    {
        string target = PathFor(entry.FullName);
        // ok: securefilemanager-csharp-zip-slip
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }
}

class UnrelatedHelper
{
    string PathFor(string name) => Path.Combine("/srv/storage/1", name);

    void SameMethodNameIsNotTrusted(ZipArchiveEntry entry)
    {
        string target = PathFor(entry.FullName);
        // ruleid: securefilemanager-csharp-zip-slip
        File.WriteAllText(target, "bad");
    }
}
