using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace FileManager.Server;

public sealed class FileService
{
    public const int MaxFileBytes = 1 * 1024 * 1024;
    public const int MaxZipBytes = 10 * 1024 * 1024;
    private const long MaxZipExpandedBytes = 20L * 1024 * 1024;
    private const int MaxZipEntries = 100;
    private const int MaxCompressionRatio = 200;
    private static readonly ConcurrentDictionary<int, object> UserLocks = new();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
        "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4",
        "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private readonly Database _database;
    private readonly int _userId;
    private readonly string _root;

    public FileService(Database db, int userId, string dataDir)
    {
        _database = db;
        _userId = userId;
        _root = Path.Combine(dataDir, "storage", userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_root);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Ссылки на каталог запрещены.");
    }

    private object Gate => UserLocks.GetOrAdd(_userId, _ => new object());

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 ||
            !Regex.IsMatch(name, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant) ||
            name.Contains("..", StringComparison.Ordinal) || name.EndsWith('.') ||
            name != Path.GetFileName(name))
            throw new ArgumentException("Недопустимое имя файла или путь вне пользовательской папки.");
        string stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem))
            throw new ArgumentException("Имя зарезервировано операционной системой.");
    }

    private string PathFor(string name)
    {
        ValidateName(name);
        string path = Path.Combine(_root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Символические ссылки запрещены.");
        return path;
    }

    private static void ValidatePerson(Person person)
    {
        if (person is null || string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 100 ||
            person.Age is < 0 or > 150)
            throw new InvalidDataException("Неверные данные человека в JSON/XML.");
    }

    private static void ValidateContent(string fileName, string content)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".txt" or ".json" or ".xml"))
            throw new ArgumentException("Можно создавать только TXT, JSON, XML.");
        if (content is null || Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            throw new InvalidDataException("Размер содержимого превышает лимит 1 МиБ.");
        if (ext == ".json")
        {
            Person? person = JsonSerializer.Deserialize<Person>(content);
            ValidatePerson(person!);
        }
        else if (ext == ".xml")
        {
            XDocument doc = ParseXml(content);
            ValidatePerson(ParsePerson(doc));
        }
    }

    private static XDocument ParseXml(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxFileBytes
        };
        using var input = new StringReader(content);
        using var reader = XmlReader.Create(input, settings);
        return XDocument.Load(reader);
    }

    private static Person ParsePerson(XDocument doc)
    {
        if (doc.Root?.Name != "person" || !int.TryParse(doc.Root.Element("age")?.Value, out int age))
            throw new InvalidDataException("Неверная структура XML.");
        return new Person(doc.Root.Element("name")?.Value ?? "", age);
    }

    public static string PersonJson(Person person)
    {
        ValidatePerson(person);
        return JsonSerializer.Serialize(person, IndentedJsonOptions);
    }

    public static string PersonXml(Person person)
    {
        ValidatePerson(person);
        return new XDocument(new XElement("person", new XElement("name", person.Name),
            new XElement("age", person.Age))).ToString();
    }

    public void Save(string name, string content)
    {
        lock (Gate)
        {
            ValidateName(name);
            ValidateContent(name, content);
            string path = PathFor(name);
            bool existed = File.Exists(path);
            string temp = Path.Combine(_root, $"_temp_{Guid.NewGuid():N}.tmp");
            try
            {
                // CreateNew prevents overwriting an existing temporary file.
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(content);
                File.Move(temp, path, overwrite: true);
                _database.SaveFile(_userId, name, new FileInfo(path).Length, existed ? "MODIFY" : "CREATE");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    public FileReply Read(string name)
    {
        lock (Gate)
        {
            ValidateName(name);
            string path = PathFor(name);
            if (Path.GetExtension(name).ToLowerInvariant() is not (".txt" or ".json" or ".xml"))
                throw new ArgumentException("Чтение доступно только для TXT, JSON, XML.");
            if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.");
            if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("Файл больше 1 МиБ.");
            string content = File.ReadAllText(path, Encoding.UTF8);
            _database.Log(_userId, "READ", name);
            return new FileReply(name, content);
        }
    }

    public void Delete(string name)
    {
        lock (Gate)
        {
            string path = PathFor(name);
            if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден.");
            File.Delete(path);
            _database.DeleteFile(_userId, name);
        }
    }

    public ZipReply CreateZip(string name)
    {
        lock (Gate)
        {
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Имя архива должно заканчиваться на .zip.");
            string path = PathFor(name);
            if (File.Exists(path)) throw new IOException("Архив уже существует.");
            string[] files = Directory.GetFiles(_root)
                .Where(p => Path.GetExtension(p).ToLowerInvariant() is ".txt" or ".json" or ".xml")
                .ToArray();
            if (files.Length is 0 or > MaxZipEntries) throw new InvalidOperationException("Нет файлов или их слишком много.");
            long total = 0;
            foreach (string file in files)
            {
                PathFor(Path.GetFileName(file));
                long length = new FileInfo(file).Length;
                if (length > MaxFileBytes) throw new InvalidDataException("Файл больше 1 МиБ.");
                total = checked(total + length);
                if (total > MaxZipExpandedBytes) throw new InvalidDataException("Суммарный размер файлов слишком большой.");
            }
            try
            {
                using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
                    foreach (string file in files)
                        archive.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
                long archiveBytes = new FileInfo(path).Length;
                if (archiveBytes > MaxZipBytes) throw new InvalidDataException("ZIP больше 10 МиБ.");
                _database.SaveFile(_userId, name, archiveBytes, "CREATE_ZIP");
                return new ZipReply(name, files.Length);
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        }
    }

    public void UploadZip(string name, byte[] contents)
    {
        lock (Gate)
        {
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Нужен файл с расширением .zip.");
            string path = PathFor(name);
            if (File.Exists(path)) throw new IOException("Архив уже существует.");
            if (contents.Length is 0 or > MaxZipBytes) throw new InvalidDataException("ZIP больше 10 МиБ или пуст.");
            string temp = Path.Combine(_root, $"_temp_{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, contents);
                using (var archive = ZipFile.OpenRead(temp)) ValidateArchive(archive);
                File.Move(temp, path);
                _database.SaveFile(_userId, name, contents.Length, "UPLOAD_ZIP");
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    // Checks metadata BEFORE extraction; extraction also checks actual bytes written.
    private void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaxZipEntries)
            throw new InvalidDataException("Недопустимое количество ZIP-записей.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateName(entry.FullName); // Rejects ../, absolute paths, and subdirectories.
            if (!seen.Add(entry.FullName)) throw new InvalidDataException("Дублирующиеся имена в архиве.");
            if (Path.GetExtension(entry.FullName).ToLowerInvariant() is not (".txt" or ".json" or ".xml"))
                throw new InvalidDataException("В ZIP разрешены только TXT, JSON и XML.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("Символические ссылки в ZIP запрещены.");
            if (entry.Length > MaxFileBytes) throw new InvalidDataException("ZIP-бомба: файл больше 1 МиБ.");
            if (entry.Length > 0 && (entry.CompressedLength == 0 ||
                (double)entry.Length / entry.CompressedLength > MaxCompressionRatio))
                throw new InvalidDataException("Подозрительный коэффициент сжатия ZIP.");
            total = checked(total + entry.Length);
            if (total > MaxZipExpandedBytes) throw new InvalidDataException("ZIP-бомба: более 20 МиБ данных.");
            if (File.Exists(PathFor(entry.FullName)))
                throw new IOException($"Файл {entry.FullName} уже существует.");
        }
    }

    public ZipReply ExtractZip(string name)
    {
        lock (Gate)
        {
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Нужно имя ZIP-архива.");
            string path = PathFor(name);
            if (!File.Exists(path)) throw new FileNotFoundException("Архив не найден.");
            if (new FileInfo(path).Length > MaxZipBytes) throw new InvalidDataException("ZIP больше 10 МиБ.");
            using var archive = ZipFile.OpenRead(path);
            ValidateArchive(archive);
            var created = new List<string>();
            var metadata = new List<(string Name, long Size)>();
            long totalWritten = 0;
            try
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string target = PathFor(entry.FullName);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    created.Add(target);
                    long written = 0;
                    byte[] buffer = new byte[8192];
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written = checked(written + count);
                        totalWritten = checked(totalWritten + count);
                        if (written > MaxFileBytes || totalWritten > MaxZipExpandedBytes)
                            throw new InvalidDataException("ZIP-бомба: превышен фактический лимит распаковки.");
                        output.Write(buffer, 0, count);
                    }
                    if (written != entry.Length) throw new InvalidDataException("Размер файла не совпал с ZIP-метаданными.");
                    metadata.Add((entry.FullName, written));
                }
                _database.SaveExtracted(_userId, metadata, name);
                return new ZipReply(name, metadata.Count);
            }
            catch
            {
                foreach (string file in created)
                    if (File.Exists(file)) File.Delete(file);
                throw;
            }
        }
    }
}
