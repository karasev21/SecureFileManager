# Клиент-серверный файловый менеджер — лабораторная работа

.NET 8, C#, ASP.NET Core Minimal API, SQLite, консольный HTTP-клиент, xUnit, GitHub Actions.

> **Статус на 21.10.2026:** исходная версия успешно собрана и прошла 11/11 интеграционных тестов на компьютере пользователя. В этой обновлённой версии исправлены предупреждения анализаторов, добавлен отдельный тест коэффициента сжатия ZIP и внесены проверенные ранее исправления обработки исключений/очистки пула SQLite. **Именно обновлённую версию ещё требуется заново собрать и протестировать** в VS Code / GitHub Actions; до запуска нельзя заявлять 12/12. Проект предназначен для локальной учебной демонстрации, не для публичного Интернета.

## Архитектура

```
┌─────────────────────────┐     HTTP Bearer token      ┌─────────────────────────────┐
│ FileManager.Client      │ ──────────────────────────> │ FileManager.Server          │
│ VS Code terminal №2     │ <────────────────────────── │ VS Code terminal №1         │
└─────────────────────────┘         JSON / status        │                             │
                                                        ├─ Database.cs → SQLite       │
                                                        └─ FileService.cs → storage   │
                                                        └─────────────────────────────┘
```

Клиент и сервер — **два отдельных процесса**. База и пользовательские файлы расположены **на стороне сервера**. Клиент не открывает серверную SQLite напрямую. Проект предназначен для демонстрации на `127.0.0.1`.

## Запуск в VS Code на Windows

1. Распаковать архив в **новую папку**, чтобы не перезаписать предыдущую консольную работу.
2. Открыть папку `SecureFileManager_ClientServer` в VS Code.
3. Проверить `dotnet --version` (SDK 8 или 10 способен собирать проект под net8.0 при наличии необходимых targeting packs / restore).
4. В **первом терминале**:

   ```powershell
   dotnet restore SecureFileManager.sln
   dotnet build SecureFileManager.sln
   dotnet run --project FileManager.Server -- --urls http://127.0.0.1:5050
   ```

5. Открыть **второй терминал** (`Terminal` → `New Terminal`):

   ```powershell
   dotnet run --project FileManager.Client
   ```

6. Зарегистрировать пользователя, войти, создавать/читать TXT/JSON/XML, архивировать и распаковывать ZIP, смотреть журнал. Для загрузки тестового ZIP на сервер в меню клиента есть отдельный пункт `9`.
7. Открой `http://127.0.0.1:5050/api/health` — должен ответить JSON `{"status":"ok"}` (нужен работающий сервер).

**Важно:** HTTP без шифрования допустим только для локальной демонстрации на loopback. Для работы через сеть требуется TLS (HTTPS), серверная конфигурация и дополнительные меры безопасности.

## Основные API-эндпоинты

| Метод | Адрес | Назначение |
|---|---|---|
| POST | `/api/register` | Создать пользователя |
| POST | `/api/login` | Получить токен доступа |
| POST | `/api/logout` | Отозвать токен |
| GET | `/api/drives` | Сведения о дисках сервера |
| GET | `/api/files?search=...` | Файлы пользователя + поиск в SQLite |
| GET | `/api/files/{name}` | Прочитать TXT/JSON/XML |
| PUT | `/api/files/{name}` | Создать или изменить TXT/JSON/XML; JSON `{ "content": "..." }` |
| DELETE | `/api/files/{name}` | Удалить файл |
| POST | `/api/archives/{name}/create` | Создать ZIP из пользовательских файлов |
| PUT | `/api/archives/{name}` | Загрузить ZIP (тело запроса — бинарный ZIP) |
| POST | `/api/archives/{name}/extract` | Безопасно распаковать ZIP |
| GET | `/api/audit` | Последние 30 действий пользователя |
| GET | `/api/health` | Проверка доступности сервера |

Все маршруты, кроме регистрации, входа и `/api/health`, требуют заголовок `Authorization: Bearer <token>`.

## Защита: что реализовано, что проверяем

| Угроза | Контрмера | Тест |
|---|---|---|
| SQL injection | Параметризация SQL (`$user`, `$search`), без конкатенации пользовательского ввода | `Sql_injection_in_login...`, `Sql_injection_in_search...` |
| Обход аутентификации | Случайный токен, SHA-256 хеш токена в SQLite, срок жизни 8 часов, проверка на каждом запросе | `Unauthenticated_and_other_user...` |
| Слабое хранение паролей | PBKDF2-HMAC-SHA256, уникальная соль, 210000 итераций, FixedTimeEquals | Код `Database.Register`, `Login` |
| Path Traversal / Zip Slip | Запрет `..`, слешей, подкаталогов и недопустимых имён | `Xml_dtd_and_path_traversal...`, `Zip_slip...` |
| ZIP bomb / decompression bomb | 10 МиБ ZIP, 100 записей, 1 МиБ на файл, 20 МиБ суммарно, коэффициент сжатия ≤200, проверка по реальным байтам при распаковке | `ZipBombIsRejectedOnUpload`, `ZipCompressionRatioExceedingLimitIsRejected`, `TooManyZipEntriesAreRejected` |
| XML External Entity (XXE) | `DtdProcessing.Prohibit`, `XmlResolver = null` | `Xml_dtd_and_path_traversal...` |
| Чужие файлы | Папки `storage/<userId>` + фильтр SQLite `WHERE UserId = $user` + серверный токен | `Unauthenticated_and_other_user...` |
| Перебор паролей | Rate limiter на `/api/login`, `/api/register`: 10 запросов за минуту | Можно дополнительно демонстрировать вручную |

**Ограничения:** это учебная защита, а не полный аудит. `UserLocks` синхронизирует файловые операции одного пользователя в одном серверном процессе, но не защищает от параллельной работы других процессов. Нет транзакции между диском и SQLite, возможны несогласованности при сбое между этими операциями. Проверка reparse points не устраняет все race conditions с внешним локальным атакующим. Токен не привязан к устройству, в тестовом окружении HTTP не шифруется, централизованного мониторинга попыток входа для несуществующих логинов нет. Нельзя размещать учебный сервер в Интернете без TLS, правильных прав на каталог, дополнительного hardening и независимых тестов.

## Тесты безопасности (автоматически)

```powershell
dotnet test SecureFileManager.sln --logger "trx;LogFileName=tests.trx" --collect:"XPlat Code Coverage"
```

xUnit запускает ASP.NET Core TestServer в памяти (без ручного запуска). В текущей версии **12 сценариев**, включая отдельный тест чрезмерного коэффициента сжатия; тесты формируют небольшие ZIP, многогигабайтные ZIP-бомбы не создаются. Ранее пройдены 11 сценариев, **12 тестов этой версии требуют повторного прогона**. Проверки подтверждают лишь конкретные случаи, не полный аудит защищённости.

## CI / Continuous Delivery GitHub Actions

Рабочий процесс `.github/workflows/ci.yml` запускается при push в `main` / `master`, pull request и вручную. Этапы:

1. Checkout репозитория, установка .NET 8, `dotnet restore` с включённым NuGet audit.
2. Сборка с Roslyn/.NET анализаторами, `dotnet format analyzers` для ошибок анализаторов.
3. `dotnet test` с интеграционными тестами на атаки, отчётами TRX и coverage Cobertura. Сборка в CI использует `-warnaserror` и проверку `dotnet format analyzers --severity warn`, чтобы предупреждения анализаторов нельзя было молча игнорировать.
4. Artifact `test-results-and-coverage` доступен даже при провале тестов.
5. Только после успешных проверок публикация двух приложений (`dotnet publish`) и Artifact `client-and-server-builds`.
6. Dependabot предлагает еженедельные обновления NuGet и GitHub Actions.

Это **CI + continuous delivery в виде артефактов сборки**, а не автоматическая установка сервера в облако. Деплой в production намеренно не настроен. Важно: встроенный NuGet audit предупреждает об уязвимостях зависимостей, но сам по себе не гарантирует падение job при любой найденной уязвимости; отдельно проверяйте предупреждения и настраивайте строгость policy.

### Как загрузить на GitHub

Создай новый **приватный** репозиторий без предварительного README и выполни в терминале из корневой папки:

```powershell
git init
git add .
git commit -m "Client-server file manager with security tests and CI"
git branch -M main
git remote add origin https://github.com/lizaveta4/SecureFileManager.git
git push -u origin main
```

Репозиторий `lizaveta4/SecureFileManager` создан и приватный. Дальше GitHub → **Actions** → **File manager CI**. Сборки и отчёты появятся в Artifacts после успешных job. **Не добавляй реальные пароли, ManagerData или .db файлы в git.**

## Для защиты преподавателю

* Почему сервер, а не клиент, решает, к какому `UserId` обращаться? — Не следует доверять клиенту; сервер получает ID из проверенного токена.
* Почему параметризованный SQL мешает SQL injection? — Команда и данные отправляются отдельно, данные не превращаются в SQL-код.
* Почему одной проверки `entry.Length` недостаточно? — ZIP метаданные и фактически извлечённые байты требуется проверять независимо.
* В чём разница ZIP Slip и ZIP bomb? — ZIP Slip пытается записать за пределы целевой папки; ZIP bomb перегружает ресурсы распаковкой.
* Как проверяется защита? — Автотесты отправляют опасные HTTP-запросы и ZIP-архивы и проверяют HTTP-ответ и отсутствие нежелательных побочных эффектов.
* Чем CI отличается от CD? — CI проверяет и собирает изменения, continuous delivery формирует пригодные к распространению артефакты; автоматического deployment здесь нет.

Документация: [Microsoft ASP.NET Core integration tests](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests), [Microsoft .NET code analysis](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview), [GitHub Actions for .NET](https://docs.github.com/en/actions/tutorials/build-and-test-code/net).
