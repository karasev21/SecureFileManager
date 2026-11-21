# Практическая работа №2: расширенный SAST C#/.NET

Проверено 21.11.2026, Semgrep CE **1.186.0**, Python 3.12, Linux-контейнер Docker Desktop.
Ветка: `feature/pr2-semgrep-security-suite`. Основа: `80ceca92589ae0a9240e3ad2f78daecd1ea71f6e`.
Изменения касаются правил, примеров, CI и документации; бизнес-логика приложения не менялась.

## Проверенные правила

Всего **15 правил ERROR** вместо 3: 13 проектных (включая три исходных) и 2 официальных.
В таблице P/N — число положительных/отрицательных аннотаций, проверенных через `semgrep --test`.
Каждое правило имеет хотя бы один опасный и один безопасный пример. Все наборы прошли.

| Полный ID | Опасная конструкция / риск | P/N |
| --- | --- | --- |
| `securefilemanager-csharp-insecure-binaryformatter` | Создание BinaryFormatter: небезопасная десериализация, CWE-502 | 1/1 |
| `securefilemanager-csharp-xxe-dtd-parsing` | Включение `DtdProcessing.Parse` в настройках: DTD/XXE, CWE-611 | 2/2 |
| `securefilemanager-csharp-external-xml-resolver` | Присвоение XmlUrlResolver свойству XmlResolver: внешние сущности, CWE-611 | 2/1 |
| `securefilemanager-csharp-dynamic-sql` | Конкатенация, интерполяция или string.Format достигают CommandText, SQL-конструктора либо EF Raw SQL: SQL injection, CWE-89 | 3/3 |
| `securefilemanager-csharp-http-path-traversal` | HTTP query/form/route, имя upload или строковый параметр Minimal API достигают файлового пути: чтение, запись, удаление, Move/Copy, CWE-22 | 6/4 |
| `securefilemanager-csharp-zip-slip` | ZipArchiveEntry.FullName достигает создаваемого файла/ExtractToFile без проверенного валидатора: Zip Slip, CWE-22 | 2/2 |
| `securefilemanager-csharp-shell-injection` | Динамические аргументы известного shell в Process.Start или ProcessStartInfo: command injection, CWE-78 | 5/3 |
| `securefilemanager-csharp-tls-validation-bypass` | DangerousAcceptAnyServerCertificateValidator или callback, всегда возвращающий true: MITM, CWE-295 | 2/2 |
| `securefilemanager-csharp-weak-password-hash` | MD5/SHA1 от текстового password/passwd/pwd/passphrase: быстрый перебор паролей, CWE-916 | 2/2 |
| `securefilemanager-csharp-low-pbkdf2-work-factor` | Pbkdf2 с известным числом итераций меньше проектного минимума 210000, CWE-916 | 2/2 |
| `securefilemanager-csharp-hardcoded-encryption-key` | Aes.Key из литерала, Base64/hex либо массива байтов: ключ в исходниках, CWE-321 | 2/2 |
| `securefilemanager-csharp-secret-in-logs` | Прямая передача password/token и других перечисленных имён секретов в Log*: утечка секрета, CWE-532 | 2/2 |
| `securefilemanager-csharp-ecb-mode` | Aes.Mode = ECB, EncryptEcb/DecryptEcb: раскрытие структуры шифротекста, CWE-327 | 2/1 |
| `use_deprecated_cipher_algorithm` | DES.Create / RC2.Create: устаревшие шифры, CWE-327 | 2/1 |
| `use_weak_rng_for_keygeneration` | System.Random.NextBytes → конструктор AesGcm: предсказуемый ключ, CWE-338 | 1/1 |

Это **36 положительных и 29 отрицательных аннотаций**. Число аннотаций не равно числу отдельных файлов или xUnit-тестов.
Два официальных правила сохранены в `.semgrep/vendor` из коммита
`a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2` репозитория
[semgrep/semgrep-rules](https://github.com/semgrep/semgrep-rules/tree/a5ad1714f00c9f7fde980cab0ea1d6dc6d414fd2/csharp/dotnet/security).
Паттерны, ID и severity этих двух правил сохранены; ссылки и лицензия находятся в `.semgrep/vendor/README.md`.

Исходные XML-правила уточнены: сравнение enum с Parse и создание неиспользуемого resolver сами по себе не считаются включением опасной настройки.
Отрицательные примеры также проверяют параметризованный SQL, константные SQL-строки, фиксированные пути, проверенные имена,
System.Text.Json, PBKDF2 с 210000/600000 итераций, криптографический RNG, стандартный TLS и журналирование ID пользователя.
MD5 для контрольной суммы файла не считается хранением пароля. Тестовые учётные данные в xUnit не объявляются production-секретами.

## Фактические результаты

| Проверка | Итог |
| --- | --- |
| `semgrep --validate --strict --config .semgrep --metrics off` | Успешно: 0 ошибок конфигурации, 15 правил |
| `semgrep --test --config .semgrep/csharp-security.yml .semgrep/csharp-security.cs --metrics off` | Успешно: 13/13 |
| `semgrep --test .semgrep/vendor --metrics off` | Успешно: 2/2 |
| Общий `.github/scripts/run-sast.sh` | Успешно, код 0; validate + оба набора тестов + сканирование |
| Сканирование приложения и xUnit | 15 правил, 7 C#-файлов, **0 нарушений**, ~100% разобранных строк |
| SARIF приложения | `results: []`, `executionSuccessful: true`, уведомлений об ошибках нет |
| Контроль блокировки на опасном custom fixture | **33 ожидаемых нарушения**, настоящий код Semgrep **1** |
| Существующие SecurityIntegrationTests | **16/16** прошли, 0 ошибок, 0 пропусков |
| Полный CI/CD через act и облачный GitHub Actions | В этом сеансе не запускались; локально выполнен общий SAST-шаг и xUnit |

В финальных проверках неожиданных падений нет. Код 1 при сканировании намеренно опасного fixture — ожидаемый результат проверки блокировки.
Эти 33 демонстрационных срабатывания **не являются нарушениями приложения**. Три дополнительных положительных vendor-примера проверены через `--test`.
Автоматическое `semgrep --test .semgrep` находило только vendor `.yaml`; поэтому исходный `.yml` в CI тестируется явной парой config/fixture.

Проверялись `FileManager.Server/{ApiLog,Contracts,Database,FileService,Program}.cs`,
`FileManager.Client/Program.cs`, `FileManager.Tests/SecurityIntegrationTests.cs`.
Скомпилированные `bin/obj` исключаются. Намеренно опасные `.semgrep/*.cs` проверяются отдельно и не входят в production-отчёт.
Новые исключения для найденных проблем и комментарии подавления не добавлялись.

## Как устроен CI

`ci.yml`, `local-ci.yml` и дополнительный `semgrep-csharp.yml` устанавливают одну версию из `.semgrep/requirements.txt`
и вызывают общий `.github/scripts/run-sast.sh` с нужным путём SARIF.
Динамические `auto`, `p/default`, `p/security-audit` заменены проверенным набором из репозитория.
Сканирование правил не требует загрузки плавающих наборов из Registry.

Скрипт использует `set -euo pipefail`, `--strict`, `--severity ERROR` и `--error`.
Ошибка конфигурации/тестов или нарушение безопасности завершают шаг с ненулевым кодом.
Облачные workflow сохраняют SARIF через `if: always()`, включая отчёт со срабатываниями.
Локальный workflow сохраняет `.act-artifacts/security/semgrep.sarif` в bind-mounted каталоге act.
Зависимость deploy и Windows EXE от SAST сохранена. Merge в main не выполнялся.

## Воспроизведение и демонстрация преподавателю

Из корня ветки в Linux/WSL с Python 3.12:

```sh
python -m pip install -r .semgrep/requirements.txt
bash .github/scripts/run-sast.sh .act-artifacts/security/semgrep.sarif
```

На Windows можно использовать Docker Desktop (PowerShell):

```powershell
docker build -f .github/docker/act-semgrep.Dockerfile -t securefilemanager/act-semgrep:local .
docker run --rm -v "${PWD}:/src" -w /src securefilemanager/act-semgrep:local bash -lc "python -m pip install -r .semgrep/requirements.txt && bash .github/scripts/run-sast.sh .act-artifacts/security/semgrep.sarif"
```

Для отдельного показа validate и test выполните команды из таблицы результатов.
[Официальная документация Semgrep](https://semgrep.dev/docs/writing-rules/testing-rules) описывает аннотации `ruleid:`/`ok:`;
значение `--error` и кодов завершения приведено в [CLI reference](https://semgrep.dev/docs/cli-reference).

1. Покажите ветку и diff: 3 → 15 правил, опасные/безопасные примеры, общий SAST-скрипт в workflow.
2. Запустите общий скрипт: видны версия, validate, 13/13 + 2/2 и 0 находок приложения.
3. Откройте SARIF и журнал запуска. Для пустого отчёта покажите массив из 15 правил и `results: []`.
4. Покажите, что проверка способна остановить CI. В Linux/WSL выполните:

```sh
semgrep scan --config .semgrep/csharp-security.yml --strict --severity ERROR \
  --metrics off --no-git-ignore --error .semgrep/csharp-security.cs
echo "$?"  # ожидается 1: 33 намеренно опасных примера
```

5. Покажите TRX: 16 интеграционных тестов, включая SQL injection, изоляцию пользователей, отсутствие авторизации,
   Zip Slip, ZIP bomb, степень сжатия, лимит записей, XML/path traversal, rate limiting и конкурентные операции.
6. Для запуска облачного CI отправьте эту feature-ветку в origin и откройте Actions → SecureFileManager CI/CD.
   SARIF появится в артефакте `semgrep-sast-report`; успешный облачный запуск не заявляется без фактического run.

## Границы покрытия

Это целевой набор проверок опасных конструкций, а не доказательство отсутствия всех уязвимостей.
Semgrep CE выполняет анализ внутри функций; сложные межфункциональные потоки могут не обнаруживаться.
HTTP-правило покрывает перечисленные request-источники и типизированные строковые параметры блочных Minimal API handlers;
произвольные DTO/методы-обработчики и все варианты API не моделируются.
SQL-правило отмечает динамически построенный SQL, который требует ревью: безопасный allowlist идентификаторов тоже может потребовать уточнения правила.
Shell-правило покрывает известные интерпретаторы в Process.Start и конструкторе/initializer ProcessStartInfo; последующие присвоения свойств отдельно не моделируются.
Проверка TLS ограничена HttpClientHandler, паролей — указанными именами переменных и API, логов — прямыми аргументами Log*.
Порог PBKDF2 — минимум существующего проекта, а не универсальная рекомендация для любой системы; значение из runtime-конфигурации статически не оценивается.
Проверка ключа ограничена Aes.Key и указанными способами задания литералов.

В качестве sanitizers доверяем проверенному `FileService.ValidateName` и приватному `FileService.PathFor` в этом классе.
При изменении их реализации нужны повторное ревью и интеграционные тесты; сам вызов функции не доказывает её безопасность.
У официального RNG-правила подтверждён поток NextBytes → AesGcm; upstream-вариант присвоения cipher.Key не заявляется как проверенное покрытие CE.
Фактические лимиты ZIP bomb, авторизация и гонки проверяются существующими интеграционными тестами, а не выводятся из отсутствия SAST-находок.
