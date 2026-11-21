using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

const string baseUrl = "http://127.0.0.1:5050/";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
string? token = null;
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
Console.WriteLine("=== Клиент файлового менеджера ===");
Console.WriteLine($"Сервер: {baseUrl} (запускается отдельно)");

while (true)
{
    try
    {
        if (token is null)
        {
            Console.WriteLine("\n1 — Регистрация  2 — Вход  0 — Выход");
            string choice = Ask("Пункт");
            if (choice == "0") break;
            if (choice is not ("1" or "2")) continue;
            string login = Ask("Логин");
            string password = ReadPassword("Пароль");
            if (choice == "1")
            {
                await Send(HttpMethod.Post, "api/register", new { username = login, password });
                continue;
            }
            JsonElement? reply = await Send(HttpMethod.Post, "api/login", new { username = login, password }, showResponse: false);
            if (reply is not null && reply.Value.TryGetProperty("token", out var property))
            {
                token = property.GetString();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                Console.WriteLine("Вход выполнен.");
            }
            continue;
        }
        Console.WriteLine("\n1 — Диски  2 — Файлы  3 — Создать/изменить TXT  4 — Прочитать файл");
        Console.WriteLine("5 — Удалить  6 — Сохранить JSON  7 — Сохранить XML");
        Console.WriteLine("8 — Создать ZIP  9 — Загрузить ZIP  10 — Распаковать ZIP");
        Console.WriteLine("11 — Журнал  12 — Поиск в SQLite  0 — Выйти из аккаунта");
        string menu = Ask("Пункт");
        if (menu == "0")
        {
            await Send(HttpMethod.Post, "api/logout");
            http.DefaultRequestHeaders.Authorization = null;
            token = null;
            continue;
        }
        switch (menu)
        {
            case "1": await Send(HttpMethod.Get, "api/drives"); break;
            case "2": await Send(HttpMethod.Get, "api/files"); break;
            case "3": await Send(HttpMethod.Put, FilePath(Ask("Имя TXT")), new { content = Ask("Текст (одна строка)") }); break;
            case "4": await Send(HttpMethod.Get, FilePath(Ask("Имя файла"))); break;
            case "5": await Send(HttpMethod.Delete, FilePath(Ask("Имя файла"))); break;
            case "6":
            {
                string file = Ask("Имя .json");
                string personName = Ask("Имя человека");
                if (!int.TryParse(Ask("Возраст"), out int age)) { Console.WriteLine("Некорректный возраст."); break; }
                string content = JsonSerializer.Serialize(new { Name = personName, Age = age });
                await Send(HttpMethod.Put, FilePath(file), new { content });
                break;
            }
            case "7":
            {
                string file = Ask("Имя .xml");
                string personName = Ask("Имя человека");
                if (!int.TryParse(Ask("Возраст"), out int age)) { Console.WriteLine("Некорректный возраст."); break; }
                var xml = new System.Xml.Linq.XDocument(new System.Xml.Linq.XElement("person",
                    new System.Xml.Linq.XElement("name", personName), new System.Xml.Linq.XElement("age", age)));
                await Send(HttpMethod.Put, FilePath(file), new { content = xml.ToString() });
                break;
            }
            case "8": await Send(HttpMethod.Post, ZipPath(Ask("Имя нового ZIP"), "create")); break;
            case "9":
            {
                string source = Ask("Локальный путь к ZIP");
                if (!File.Exists(source)) { Console.WriteLine("ZIP не найден на компьютере клиента."); break; }
                var bytes = await File.ReadAllBytesAsync(source);
                if (bytes.Length > 10 * 1024 * 1024) { Console.WriteLine("Лимит загрузки ZIP — 10 МиБ."); break; }
                string dest = Ask("Имя ZIP на сервере");
                using var request = new HttpRequestMessage(HttpMethod.Put, "api/archives/" + Uri.EscapeDataString(dest))
                { Content = new ByteArrayContent(bytes) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                await Execute(request);
                break;
            }
            case "10": await Send(HttpMethod.Post, ZipPath(Ask("Имя ZIP"), "extract")); break;
            case "11": await Send(HttpMethod.Get, "api/audit"); break;
            case "12": await Send(HttpMethod.Get, "api/files?search=" + Uri.EscapeDataString(Ask("Поиск по имени"))); break;
            default: Console.WriteLine("Нет такого пункта."); break;
        }
    }
    catch (HttpRequestException ex) { Console.WriteLine("Ошибка соединения с сервером. Убедитесь, что сервер запущен на http://127.0.0.1:5050. Подробности: " + ex.Message); }
    catch (TaskCanceledException) { Console.WriteLine("Превышено время ожидания сервера."); }
    catch (IOException ex) { Console.WriteLine("Ошибка доступа к файлу: " + ex.Message); }
}

string Ask(string caption)
{
    Console.Write(caption + ": ");
    return Console.ReadLine()?.Trim() ?? "";
}

string ReadPassword(string caption)
{
    Console.Write(caption + ": ");
    var text = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0) text.Length--;
        }
        else if (!char.IsControl(key.KeyChar) && text.Length < 256)
            text.Append(key.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}

string FilePath(string name) => "api/files/" + Uri.EscapeDataString(name);
string ZipPath(string name, string action) => "api/archives/" + Uri.EscapeDataString(name) + "/" + action;

async Task<JsonElement?> Send(HttpMethod method, string route, object? payload = null, bool showResponse = true)
{
    using var request = new HttpRequestMessage(method, route);
    if (payload is not null) request.Content = JsonContent.Create(payload);
    return await Execute(request, showResponse);
}

async Task<JsonElement?> Execute(HttpRequestMessage request, bool showResponse = true)
{
    using var response = await http.SendAsync(request);
    string body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        string? serverMessage = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.ValueKind == JsonValueKind.Object &&
                    errorDocument.RootElement.TryGetProperty("error", out var errorProperty) &&
                    errorProperty.ValueKind == JsonValueKind.String)
                    serverMessage = errorProperty.GetString();
            }
            catch (JsonException)
            {
                // The server may return a non-JSON error body.
            }
        }

        string fallback = response.StatusCode switch
        {
            System.Net.HttpStatusCode.BadRequest => "Проверьте введённые данные или формат файла.",
            System.Net.HttpStatusCode.Unauthorized => "Требуется вход в аккаунт или повторная авторизация.",
            System.Net.HttpStatusCode.Forbidden => "Доступ запрещён.",
            System.Net.HttpStatusCode.NotFound => "Файл или адрес не найден.",
            System.Net.HttpStatusCode.Conflict => "Конфликт: файл уже существует или операция невозможна.",
            System.Net.HttpStatusCode.RequestEntityTooLarge => "Размер загружаемых данных превышает допустимый лимит.",
            System.Net.HttpStatusCode.TooManyRequests => "Слишком много запросов. Повторите попытку позже.",
            _ when (int)response.StatusCode >= 500 => "Внутренняя ошибка сервера. Повторите попытку позже.",
            _ => "Не удалось выполнить запрос."
        };
        Console.WriteLine($"Ошибка (HTTP {(int)response.StatusCode}): {serverMessage ?? fallback}");
        return null;
    }
    if (string.IsNullOrWhiteSpace(body)) return null;
    using var document = JsonDocument.Parse(body);
    if (showResponse)
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    return document.RootElement.Clone();
}
