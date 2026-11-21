namespace FileManager.Server;

public sealed record Credentials(string Username, string Password);
public sealed record LoginReply(string Token, int UserId, DateTime ExpiresAtUtc);
public sealed record WriteRequest(string Content);
public sealed record FileEntry(string Name, long SizeBytes, string UpdatedAt);
public sealed record AuditEntry(string Timestamp, string Action, string? FileName, string? Details);
public sealed record FileReply(string Name, string Content);
public sealed record ZipReply(string Archive, int Files);
public sealed record DiskEntry(string Name, string DriveType, string FileSystem, long TotalBytes, long AvailableBytes);
public sealed record ErrorReply(string Error);
public sealed record Person(string Name, int Age);
public sealed record StorageOptions(string DataDir);
