# Практика №2 — локальный CI/CD через act (Windows)

## Что реализовано

Основной GitHub Actions CI/CD остаётся в `.github/workflows/ci.yml`. Для демонстрации **локального раннера** используется отдельный workflow `.github/workflows/local-ci.yml`, который запускается утилитой `nektos/act` внутри Docker Desktop. Все Linux job запускаются в изолированных контейнерах. Windows EXE проверяется отдельно в облачном GitHub Actions.

Последовательность: **Build → Test / SAST / Trivy → Local Deploy**. Задания Test, SAST и Trivy стартуют после Build; Deploy — только после всех проверок.

| Job | Контейнер и действия |
| --- | --- |
| Local Build | `mcr.microsoft.com/dotnet/sdk:8.0`; locked restore, сборка, анализаторы .NET, публикация серверного и клиентского бинарников |
| Local Test | `mcr.microsoft.com/dotnet/sdk:8.0`; 16 интеграционных тестов, отчёт TRX, покрытие Cobertura |
| Local SAST | `python:3.12-bookworm`; Semgrep с `--severity ERROR --error`, SARIF |
| Local Dependency Scan | act Linux runner + Trivy; критические и высокие уязвимости → ненулевой код завершения |
| Local Deploy | act Linux runner + Docker CLI; получает артефакт Build, собирает образ, запускает приложение на `127.0.0.1:18080`, проверяет `/api/health`, удаляет тестовый контейнер |

NuGet кэшируется между запусками; результат компиляции передаётся как artifact `local-builds`. Отчёты `local-test-results`, `local-semgrep-report`, `local-trivy-report` сохраняются локальным artifact-сервером act.

## Установка и запуск на Windows

1. Установить **Docker Desktop**, включить Linux containers и дождаться успешного запуска Docker Engine.
2. Установить [nektos/act](https://nektosact.com/installation/index.html) и открыть новый терминал, чтобы команда `act --version` стала доступна. Для Windows можно проверить доступные способы установки через официальный сайт; используйте выпуск из официального репозитория.
3. Получить обновлённую ветку проекта с этим workflow (после проверки и объединения PR — `git pull origin main`).
4. В корневой папке `SecureFileManager` выполнить:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .github/scripts/run-local-ci.ps1
   ```

   Скрипт проверит `docker` и `act`, сначала соберёт локальные образы раннеров `.github/docker/act-dotnet.Dockerfile` и `.github/docker/act-semgrep.Dockerfile` (в них установлен Node.js для GitHub Actions), затем запустит Linux-конвейер через `act workflow_dispatch -W .github/workflows/local-ci.yml --pull=false` и создаст папку `.act-artifacts`.

5. Убедиться, что все пять локальных jobs завершились успешно, и сохранить снимки экрана с их результатами. Файлы артефактов сохраняются в `.act-artifacts`. При успешной проверке контейнер приложения автоматически удаляется; для демонстрации приложения отдельно используйте команды из основного README.

## Диагностика

- **`docker info` не выполняется**: запустите Docker Desktop и переключитесь на Linux-контейнеры.
- **`act` не найден**: установите утилиту, проверьте `act --version`, перезапустите терминал.
- **Ошибка скачивания GitHub Actions или образов**: нужен сетевой доступ к GitHub, Docker Hub, Microsoft Container Registry и PyPI. При первом запуске скачиваются образы .NET SDK, Python, runner, curl и зависимости Semgrep; это может занять время.
- **Проверка SAST или Trivy остановила работу**: проверьте SARIF/JSON, исправьте находки, затем запускайте заново; нельзя отключать правило остановки.
- **Ошибка Docker socket / permission denied**: проверьте работу Linux Docker Engine; не повышайте привилегии и не открывайте Docker Engine по TCP ради обхода ошибки.
- **Ошибка порта 18080**: порт может быть занят другим процессом; сначала выясните причину и освободите его.

## Важные ограничения безопасности и подтверждения

`act` использует Docker Engine хоста. Доступ к Docker-сокету позволяет фактически управлять хостовым Docker, поэтому запускайте только доверенный код; не запускайте непроверенные PR и не используйте продакшен-секреты. Не записывайте токены или пароли в YAML, локальный код или отчёты. При необходимости секреты передаются через окружение раннера, а не коммитятся.

**Статус:** конфигурация локального конвейера подготовлена в GitHub. Успешное выполнение через `act` на компьютере пользователя **не подтверждено** до выполнения команды и проверки логов. Облачный успех GitHub Actions не доказывает работу локального раннера.

Для защиты: продемонстрировать `docker info`, `act --version`, команду запуска, успешные jobs и отчёты; объяснить отличие SAST от DAST, зависимость Deploy от проверок, кэширование и передачу артефактов.

## Особенность локального запуска на Docker Desktop (Windows)

При запуске через `act --bind` исходники и папки `.act-artifacts/`, `.act-cache/` доступны каждому из изолированных Docker jobs через общий рабочий каталог. Это локальный обмен выходными файлами между стадиями **без обращения к встроенному HTTP-серверу артефактов и кэша `act`**: на Docker Desktop подключения к нему могут завершаться `ECONNREFUSED`. В `.act-artifacts/build` лежат бинарники, `.act-artifacts/tests` содержит TRX и Cobertura, `.act-artifacts/security` — SARIF и JSON. NuGet повторно использует `.act-cache/nuget` при последующих запусках. Эти папки исключены из Git и контекста Docker-сборки.

**Отличие от облака:** локальные каталоги не являются GitHub Actions artifacts; в облачном `.github/workflows/ci.yml` по-прежнему используются `upload-artifact` и `download-artifact`. На защите показывайте реальную передачу файлов в локальном общем каталоге и не называйте её загрузкой на сервер GitHub.

### Локальные сканеры после настройки `act --bind`

Локальный SAST запускает Semgrep с проектными правилами `.semgrep/csharp-security.yml` (`severity: ERROR`, `--error`) без режима `auto`, который конфликтует с `--metrics off`. Это проверка **конкретных** паттернов BinaryFormatter, включённого DTD и XmlUrlResolver; покрытие этих правил ограничено и не гарантирует выявление всех уязвимостей. Локальный Trivy CLI версии 0.70.0 скачивается из официальных GitHub releases, сверяется по SHA-256 из официального checksums-файла и повторно используется из `.act-cache/trivy-cli`. Сканирование `trivy fs --scanners vuln --severity HIGH,CRITICAL --exit-code 1` проверяет зависимости с выдачей JSON. В отличие от облачного Trivy action эта установка не требует GitHub token.
