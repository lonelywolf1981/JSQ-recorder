# JSQ Experiment Controller

![.NET Framework](https://img.shields.io/badge/.NET_Framework-4.8-512BD4?logo=.net&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-0C7BDC)
![SQLite](https://img.shields.io/badge/Storage-SQLite-003B57?logo=sqlite&logoColor=white)
![xUnit](https://img.shields.io/badge/Tests-xUnit-5C2D91)
![Version](https://img.shields.io/badge/version-0.2.2-2EA44F)

Современное desktop-приложение для мониторинга, записи и экспорта экспериментальных данных с многопостовой архитектурой (A/B/C), очередями обработки и контролем качества потока в реальном времени.

---

## Что умеет

- Три независимых поста (`A`, `B`, `C`) с параллельной записью экспериментов.
- Живой мониторинг каналов, лимитов, предупреждений и аварий.
- Гибкое распределение каналов между постами (включая drag&drop/multi-select в UI).
- Надежное хранение данных в SQLite через Dapper.
- Экспорт результатов в legacy DBF-пакет.
- Автообновление из локальной сети (через сетевую папку + `manifest.json`) с применением на перезапуске.

---

## Архитектура

Однонаправленный поток данных:

```text
TCP -> IngestQueue -> DecodeQueue -> PersistQueue -> SQLite
```

Ключевые проекты решения:

- `JSQ.Core` — доменные модели, контракты, правила и справочники.
- `JSQ.Capture` — захват TCP и постановка в очереди.
- `JSQ.Decode` — декодирование протокола в значения каналов.
- `JSQ.Storage` — SQLite + Dapper, репозитории и батч-запись.
- `JSQ.Rules` — агрегации и детектор аномалий.
- `JSQ.Export` — формирование legacy DBF-экспорта.
- `JSQ.UI.WPF` — WPF UI (MVVM, CommunityToolkit).
- `JSQ.Updater` — отдельный процесс применения обновления.
- `JSQ.Tests` — unit/integration-тесты (xUnit).

---

## Технологии

- `.NET Framework 4.8`
- `WPF + MVVM (CommunityToolkit.Mvvm)`
- `SQLite + Dapper`
- `Serilog`
- `xUnit`

---

## Быстрый старт

### 1) Восстановление и сборка

```bash
dotnet restore "JSQ/JSQ.slnx"
dotnet build "JSQ/JSQ.slnx" -c Debug
```

### 2) Запуск UI

```bash
dotnet run --project "JSQ/JSQ.UI.WPF/JSQ.UI.WPF.csproj"
```

### 3) Тесты

```bash
dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj"
```

Точечный запуск:

```bash
dotnet test "JSQ/JSQ.Tests/JSQ.Tests.csproj" --filter "FullyQualifiedName~JSQ.Tests.ExperimentTests"
```

---

## Автообновление (LAN)

Механика обновления:

1. Приложение в фоне проверяет сетевую папку обновлений.
2. При наличии новой версии скачивает пакет в локальный кэш `.jsq_updater/cache`.
3. Проверяет `SHA256`.
4. Показывает текстовую подсказку в блоке здоровья системы.
5. На перезапуске запускается `JSQ.Updater.exe`, который применяет пакет и перезапускает UI.

Важно:

- Обновление **не перезаписывает локальную БД** (`*.db`, `*.db-wal`, `*.db-shm`).
- Пакет обновления должен содержать `manifest.json` в корне feed-папки.

Пример `manifest.json`:

```json
{
  "Version": "0.2.2",
  "PackageFile": "JSQ_update_v0.2.2.zip",
  "Sha256": "0C18C38A14DCD04A25FAEA86627DB98954AE9D1D3C027FDF9FF1A5D77B4493BD",
  "Size": 4704892,
  "Mandatory": false,
  "ReleaseNotes": "Обновление без перезаписи локальной базы данных",
  "PublishedAt": "2026-02-26T04:02:34Z"
}
```

Как считать `SHA256` и `Size` (в байтах):

```powershell
Get-FileHash "build\update_feed\stable\JSQ_update_v0.2.2.zip" -Algorithm SHA256
(Get-Item "build\update_feed\stable\JSQ_update_v0.2.2.zip").Length
```

---

## Структура репозитория

```text
JSQ/
  JSQ.slnx
  JSQ.Core/
  JSQ.Capture/
  JSQ.Decode/
  JSQ.Storage/
  JSQ.Rules/
  JSQ.Export/
  JSQ.Updater/
  JSQ.UI.WPF/
  JSQ.Tests/
```

---

## Состояние проекта

- Текущая версия UI: `0.2.2`
- Основная ветка для интеграции: `main`
- Ветка фичей/эволюции интерфейса: `newui`
