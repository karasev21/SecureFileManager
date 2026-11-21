using FileManager.Server;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace FileManager.Tests;

public class SecurityIntegrationTests
{
    [Fact]
    public async Task NormalFileRoundtripAndAuditWork()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        var save = await client.PutAsJsonAsync("/api/files/note.txt", new WriteRequest("hello"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        FileReply? read = await client.GetFromJsonAsync<FileReply>("/api/files/note.txt");
        Assert.Equal("hello", read?.Content);
        var files = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        Assert.Contains(files!, file => file.Name == "note.txt");
        var audit = await client.GetFromJsonAsync<List<AuditEntry>>("/api/audit");
        Assert.Contains(audit!, entry => entry.Action == "CREATE");
    }

    [Fact]
    public async Task MissingFileReturnsUnderstandableError()
    {
        using var app = new TestApplication();
        using var client = await app.Authenticated("alice");
        using var response = await client.GetAsync("/api/files/missing.txt");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        ErrorReply? error = await response.Content.ReadFromJsonAsync<ErrorReply>();
        Assert.NotNull(error);
        Assert.Contains("не найден", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidJsonFileReturnsUnderstandableError()
    {
        using var app = new TestApplication();
        using var client = await app.Authenticated("alice");
        using var response = await client.PutAsJsonAsync(
            "/api/files/person.json", new WriteRequest("{broken json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ErrorReply? error = await response.Content.ReadFromJsonAsync<ErrorReply>();
        Assert.NotNull(error);
        Assert.Contains("JSON", error.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlInjectionInLoginDoesNotAuthenticate()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        client.DefaultRequestHeaders.Authorization = null;
        var response = await client.PostAsJsonAsync("/api/login", new Credentials("alice' OR '1'='1' --", "wrongpassword"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SqlInjectionInSearchDoesNotReturnAllFiles()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        await client.PutAsJsonAsync("/api/files/note.txt", new WriteRequest("private"));
        var response = await client.GetFromJsonAsync<List<FileEntry>>(
            "/api/files?search=" + Uri.EscapeDataString("' OR 1=1 --"));
        Assert.Empty(response!);
        var check = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        Assert.Single(check!);
    }

    [Fact]
    public async Task UnauthenticatedAndOtherUserRequestsAreDenied()
    {
        using var app = new TestApplication();
        var alice = await app.Authenticated("alice");
        await alice.PutAsJsonAsync("/api/files/private.txt", new WriteRequest("secret"));
        var bob = await app.Authenticated("bob");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/files/private.txt")).StatusCode);
        var files = await bob.GetFromJsonAsync<List<FileEntry>>("/api/files");
        Assert.Empty(files!);
        using var anonymous = app.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/files")).StatusCode);
    }

    [Fact]
    public async Task ValidUploadedZipCanBeExtracted()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        var upload = await Upload(client, "safe.zip", MakeZip(("import.txt", "hello")));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var extract = await client.PostAsync("/api/archives/safe.zip/extract", null);
        Assert.Equal(HttpStatusCode.OK, extract.StatusCode);
        var list = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        Assert.Contains(list!, item => item.Name == "import.txt");
    }

    [Fact]
    public async Task PasswordGuessRateLimitBlocksExcessiveRequests()
    {
        using var app = new TestApplication();
        using var client = app.Factory.CreateClient();
        HttpResponseMessage? last = null;
        for (int i = 0; i < 11; i++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync("/api/login", new Credentials("missing", "invalid"));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last?.StatusCode);
        last?.Dispose();
    }

    [Fact]
    public async Task ZipSlipIsRejectedOnUpload()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        byte[] zip = MakeZip(("../outside.txt", "attack"));
        var response = await Upload(client, "slip.zip", zip);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(app.Root, "storage", "outside.txt")));
    }

    [Fact]
    public async Task ZipBombIsRejectedOnUpload()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        byte[] zip = MakeZip(("bomb.txt", new string('0', FileService.MaxFileBytes + 1)));
        var response = await Upload(client, "bomb.zip", zip);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ZipCompressionRatioExceedingLimitIsRejected()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        // Less than 1 MiB uncompressed, but a huge compression ratio.
        byte[] zip = MakeZip(("ratio.txt", new string('A', 100_000)));
        var response = await Upload(client, "ratio.zip", zip);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var files = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        Assert.Empty(files!); // Reject the upload without persisting it.
    }

    [Fact]
    public async Task TooManyZipEntriesAreRejected()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        var files = Enumerable.Range(0, 101).Select(i => ($"f{i}.txt", "ok")).ToArray();
        var response = await Upload(client, "entries.zip", MakeZip(files));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task XmlDtdAndPathTraversalAreRejected()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        string xml = "<!DOCTYPE person [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><person><name>&x;</name><age>20</age></person>";
        var response = await client.PutAsJsonAsync("/api/files/person.xml", new WriteRequest(xml));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var badName = await client.PutAsJsonAsync("/api/files/" + Uri.EscapeDataString("../escape.txt"),
            new WriteRequest("no"));
        Assert.NotEqual(HttpStatusCode.OK, badName.StatusCode);
    }

    [Fact]
    public async Task ZipCanBeCreatedAndExtractedAfterDelete()
    {
        using var app = new TestApplication();
        var client = await app.Authenticated("alice");
        await client.PutAsJsonAsync("/api/files/note.txt", new WriteRequest("saved"));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/archives/test.zip/create", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/files/note.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/archives/test.zip/extract", null)).StatusCode);
        Assert.Equal("saved", (await client.GetFromJsonAsync<FileReply>("/api/files/note.txt"))?.Content);
    }


    [Fact]
    public async Task ConcurrentWritesKeepFileAndMetadataConsistent()
    {
        using var app = new TestApplication();
        using var client = await app.Authenticated("alice");
        string[] contents = Enumerable.Range(0, 16)
            .Select(index => $"version-{index:D2}:{new string((char)('a' + index), 512 + index)}")
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(contents.Select(content =>
            client.PutAsJsonAsync("/api/files/race.txt", new WriteRequest(content))));
        foreach (HttpResponseMessage response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        FileReply? stored = await client.GetFromJsonAsync<FileReply>("/api/files/race.txt");
        Assert.NotNull(stored);
        Assert.Contains(stored.Content, contents);
        List<FileEntry>? files = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        FileEntry record = Assert.Single(files!);
        Assert.Equal("race.txt", record.Name);
        Assert.Equal((long)Encoding.UTF8.GetByteCount(stored.Content), record.SizeBytes);
        string userDir = Path.Combine(app.Root, "storage", "1");
        Assert.Equal(stored.Content, File.ReadAllText(Path.Combine(userDir, "race.txt")));
        Assert.Empty(Directory.GetFiles(userDir, "_temp_*.tmp"));
    }

    [Fact]
    public async Task ConcurrentUploadsOfSameZipAllowOnlyOneSuccess()
    {
        using var app = new TestApplication();
        using var client = await app.Authenticated("alice");
        HttpResponseMessage[] responses = await Task.WhenAll(
            Upload(client, "same.zip", MakeZip(("first.txt", "one"))),
            Upload(client, "same.zip", MakeZip(("second.txt", "two"))));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        foreach (HttpResponseMessage response in responses)
            response.Dispose();

        List<FileEntry>? files = await client.GetFromJsonAsync<List<FileEntry>>("/api/files");
        FileEntry archive = Assert.Single(files!);
        Assert.Equal("same.zip", archive.Name);
        string physicalFile = Path.Combine(app.Root, "storage", "1", "same.zip");
        Assert.Equal(new FileInfo(physicalFile).Length, archive.SizeBytes);
        using var zip = ZipFile.OpenRead(physicalFile);
        Assert.Single(zip.Entries);
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string name, byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return client.PutAsync("/api/archives/" + name, content);
    }

    private sealed class TestApplication : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fm-tests-" + Guid.NewGuid().ToString("N"));
        public WebApplicationFactory<Program> Factory { get; }

        public TestApplication()
        {
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
                web.ConfigureTestServices(services =>
                {
                    services.RemoveAll<StorageOptions>();
                    services.AddSingleton(new StorageOptions(Root));
                }));
        }

        public async Task<HttpClient> Authenticated(string username)
        {
            HttpClient client = Factory.CreateClient();
            string password = "StrongTestPassword123!";
            var register = await client.PostAsJsonAsync("/api/register", new Credentials(username, password));
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            var login = await client.PostAsJsonAsync("/api/login", new Credentials(username, password));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                document.RootElement.GetProperty("token").GetString());
            return client;
        }

        public void Dispose()
        {
            Factory.Dispose();
            // SQLite keeps physical connections in a pool after individual connections close.
            // Release the pooled handles before deleting this test's temporary database.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
