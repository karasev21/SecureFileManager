using FileManager.Server;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = FileService.MaxZipBytes + 1024);
string dataDir = builder.Configuration["FileManager:DataDirectory"]
    ?? Path.Combine(AppContext.BaseDirectory, "ManagerData");

builder.Services.AddSingleton(new StorageOptions(dataDir));
builder.Services.AddSingleton<Database>(services =>
    new Database(Path.Combine(services.GetRequiredService<StorageOptions>().DataDir, "manager.db")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

var app = builder.Build();

app.Use(async (http, next) =>
{
    try
    {
        await next(http);
    }
    catch (Exception ex)
    {
        if (http.Response.HasStarted) throw;
        int status = ex switch
        {
            FileNotFoundException => 404,
            UnauthorizedAccessException => 403,
            ArgumentException or InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException => 400,
            BadHttpRequestException bad => bad.StatusCode,
            IOException or InvalidOperationException => 409,
            _ => 500
        };
        if (status == 500)
        {
            ILogger logger = http.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("FileManager");
            ApiLog.UnhandledException(logger, ex);
        }
        string message = ex switch
        {
            System.Text.Json.JsonException => "Некорректный формат JSON. Проверьте синтаксис и типы данных.",
            System.Xml.XmlException => "Некорректный XML или запрещённая конструкция XML.",
            BadHttpRequestException when status == 400 => "Некорректный HTTP-запрос или формат данных.",
            _ when status == 500 => "Внутренняя ошибка сервера.",
            _ => ex.Message
        };
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new ErrorReply(message));
    }
});
app.UseRouting();
app.UseRateLimiter();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/register", (Credentials request, Database db) =>
{
    int id = db.Register(request.Username, request.Password);
    return Results.Ok(new { userId = id });
}).RequireRateLimiting("auth");

app.MapPost("/api/login", (Credentials request, Database db) =>
{
    LoginReply? reply = db.Login(request.Username, request.Password);
    return reply is null ? Results.Unauthorized() : Results.Ok(reply);
}).RequireRateLimiting("auth");

app.MapPost("/api/logout", (HttpContext http, Database db) =>
{
    int? userId = AuthenticatedUser(http, db);
    if (userId is null) return Results.Unauthorized();
    db.Logout(GetToken(http)!, userId.Value);
    return Results.Ok(new { message = "Logged out" });
});

app.MapGet("/api/drives", (HttpContext http, Database db) =>
{
    int? id = AuthenticatedUser(http, db);
    if (id is null) return Results.Unauthorized();
    var drives = DriveInfo.GetDrives().Where(d => d.IsReady)
        .Select(d => new DiskEntry(d.Name, d.DriveType.ToString(), d.DriveFormat,
            d.TotalSize, d.AvailableFreeSpace)).ToArray();
    db.Log(id.Value, "LIST_DRIVES");
    return Results.Ok(drives);
});

app.MapGet("/api/files", (HttpContext http, Database db, string? search) =>
{
    int? id = AuthenticatedUser(http, db);
    return id is null ? Results.Unauthorized() : Results.Ok(db.ListFiles(id.Value, search));
});

app.MapGet("/api/files/{name}", (HttpContext http, string name, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    return id is null ? Results.Unauthorized() : Results.Ok(new FileService(db, id.Value, storage.DataDir).Read(name));
});

app.MapPut("/api/files/{name}", (HttpContext http, string name, WriteRequest request, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    if (id is null) return Results.Unauthorized();
    new FileService(db, id.Value, storage.DataDir).Save(name, request.Content);
    return Results.Ok(new { message = "Saved" });
});

app.MapDelete("/api/files/{name}", (HttpContext http, string name, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    if (id is null) return Results.Unauthorized();
    new FileService(db, id.Value, storage.DataDir).Delete(name);
    return Results.Ok(new { message = "Deleted" });
});

app.MapPost("/api/archives/{name}/create", (HttpContext http, string name, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    return id is null ? Results.Unauthorized() : Results.Ok(new FileService(db, id.Value, storage.DataDir).CreateZip(name));
});

app.MapPut("/api/archives/{name}", async (HttpContext http, string name, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    if (id is null) return Results.Unauthorized();
    using var output = new MemoryStream();
    byte[] buffer = new byte[8192];
    int read;
    while ((read = await http.Request.Body.ReadAsync(buffer)) > 0)
    {
        if (output.Length + read > FileService.MaxZipBytes)
            throw new InvalidDataException("Загрузка ZIP превышает 10 МиБ.");
        output.Write(buffer, 0, read);
    }
    new FileService(db, id.Value, storage.DataDir).UploadZip(name, output.ToArray());
    return Results.Ok(new { message = "Uploaded" });
});

app.MapPost("/api/archives/{name}/extract", (HttpContext http, string name, Database db, StorageOptions storage) =>
{
    int? id = AuthenticatedUser(http, db);
    return id is null ? Results.Unauthorized() : Results.Ok(new FileService(db, id.Value, storage.DataDir).ExtractZip(name));
});

app.MapGet("/api/audit", (HttpContext http, Database db) =>
{
    int? id = AuthenticatedUser(http, db);
    return id is null ? Results.Unauthorized() : Results.Ok(db.History(id.Value));
});

app.Run();

static string? GetToken(HttpContext http)
{
    string authorization = http.Request.Headers.Authorization.ToString();
    return authorization.StartsWith("Bearer ", StringComparison.Ordinal)
        ? authorization[7..]
        : null;
}

static int? AuthenticatedUser(HttpContext http, Database db) => db.FindSession(GetToken(http));

public partial class Program { }
