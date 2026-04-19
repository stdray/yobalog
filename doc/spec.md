# YobaLog: Lightweight Structured Logging System

## 1. Концепция и Ядро (Backend)
- **Принцип:** совместимость с Seq. Везде, где есть выбор семантики (протокол, имена полей, поведение API-ключей, форма retention-политик), повторяем Seq. Цель — работать со стандартными Seq-таргетами (Serilog, seqlog, seq-logging, Winston) без модификаций.
- **Исключение из Seq-совместимости — query-язык.** Seq использует собственный синтаксис (filter expressions + SQL-подобный "SQL queries", похожий на C# query comprehension). YobaLog берёт KQL — это сознательное расхождение ради готового парсера `Kusto.Language` и in-memory executor'а `kusto-loco` (см. §3). В .NET-экосистеме аналогичной библиотеки под Seq-диалект не существует.
- **Архитектура:** монолит на .NET 10. Внутренние границы — через интерфейсы (ingestion, store, query engine, retention); вынос модулей в отдельные сервисы на старте не планируется, пока нет понимания, что и зачем пилить.
- **Протокол приема:** CLEF (Newline-Delimited JSON) + Seq legacy Events-envelope — полная совместимость с экосистемой Seq.
- **Endpoints:**
    - `POST /api/v1/ingest/clef` — канонический версионированный путь. Принимает CLEF NDJSON (`application/vnd.serilog.clef`) и Seq Events-envelope (`application/json`, `{"Events":[…]}`; внутри либо CLEF, либо legacy Raw — нормализуется в CLEF).
    - `POST /seq-compat/api/events/raw` — приёмник для Seq-клиентов (Serilog.Sinks.Seq, seq-logging, seqlog). Клиенты строят endpoint как `<base-url>/api/events/raw` (просто конкатенация строки), поэтому пользователь прописывает в своём Serilog/winston-конфиге base URL `https://yobalog.example.com/seq-compat` — клиент дописывает хвост сам. Тот же handler, что и canonical. UI workspace'а даст кнопку «Copy Seq URL» с готовым base URL для конкретного API-ключа.
    - Будущие форматы (`gelf`, и т.п.) — новый `MapPost("/api/v1/ingest/<fmt>", ...)` + формат-специфичный парсер. Auth и pipeline-dispatch шарятся через `IngestionHandlers.ResolveScopeAsync` + `IIngestionPipeline.IngestAsync`.
- **Хранение:**
    - Стартовый backend — **SQLite + FTS5** через `linq2db`. Один файл `.db` на каждое хранилище (Workspace).
    - Обоснование: inverted index на `Message` через FTS5 — из коробки, без собственной реализации (главный UX-gap против Seq закрыт бесплатно); `linq2db` даёт общий путь трансляции KQL→SQL, тот же, что пригодится для DuckDB.
    - LiteDB пропускается: row-storage → row-storage — промежуточный шаг без выигрыша в архитектуре. Compile-time trade-off в пользу меньшего числа реализаций `ILogStore` (см. §11).
    - Дальняя перспектива — **DuckDB** для колоночного хранения, после мёрджа [linq2db#5451](https://github.com/linq2db/linq2db/pull/5451). PR авторства разработчика YobaLog — timeline в своих руках.
- **Схема документа (зафиксирована):**
    - Индексируемые поля (минимальный набор, расширение — только через миграцию): `Timestamp` (@t), `Level` (@l), `TemplateHash` (хеш от @mt), `TraceId` (@tr), `SpanId` (@sp).
    - Поле `Message` (@m) и `Exception` (@x) — без индекса; поиск по substring = full scan.
    - `Properties` — JSON-колонка (динамический мешок). Индексация — только явный allowlist путей per-workspace через expression-index на `json_extract(properties, '$.path')` (SQLite / DuckDB / Postgres).
    - **Политика на не-индексированные фильтры:** допустимы, UI показывает warning "full scan".
- **Конфигурация сервиса:** `appsettings.json`. Прямая зависимость от YobaConf запрещена — иначе получим цикл (YobaConf будет писать логи сюда же). Инфраструктурные сервисы не зависят друг от друга.

## 2. Интеграция (Стандартные таргеты)
Используем существующие библиотеки, поддерживающие протокол Seq:
- **.NET:** Serilog + Seq Sink.
- **Python:** `logging` + `seqlog`.
- **TS/JS:** Winston/Pino + `seq-logging`.

## 3. Функциональные возможности
- **Query Engine:** KQL — официальный диалект, не собственный "KQL-подобный". Парсер = `Microsoft.Azure.Kusto.Language` (NuGet от MS, тот же, что в Azure Data Explorer / Log Analytics) через обёртку [`kusto-loco`](https://github.com/NeilMacMullen/kusto-loco) (MIT). AST = Kusto AST; свой AST не пишем. Unsupported-операторы режутся на стадии трансляции с понятной ошибкой ("operator X not supported in yobalog"). Трансляция Kusto AST → backend-query: `Expression Trees` через `linq2db` (SQLite+FTS5 на старте, позже DuckDB). Full-text поиск по `Message` транслируется в FTS5 MATCH на SQLite. Агрегации (`summarize`, `count`, `by`) — часть KQL из коробки.
- **Saved Queries / Shared Queries:**
    - Сохранённый запрос = KQL + агрегация. Заменяет нужду в domain-specific таблицах (аналог yobapub-овского `PlaybackErrorStore` = запрос с `summarize count() by domain`).
    - Анонимная ссылка на view (Guid, TTL, read-only).
    - **TSV Export:** оптимизированная выдача для LLM (Gemini/Claude).
    - **Маскирование (UX):** при нажатии "Поделиться" пользователю показывается список полей, попадающих во view; чекбоксами отмечаются маскируемые. Авто-разметка (`client_ip`, `email`, `Authorization`, имена пользователей) предзаполняет allowlist.
    - **Маскирование (механика):** детерминированная замена (HMAC + соль сессии) — связи между записями сохраняются, значения не раскрываются.
- **Admin:**
    - Управление пользователями, workspace'ами, API-ключами.
    - **API-ключи (стиль Seq):** `X-Seq-ApiKey` header или `?apiKey=`. Скоуп — workspace. Rate-limit per key.
    - **Retention-политики (стиль Seq):** набор правил; каждое правило = KQL-фильтр + отсечка по дате. Пример: `@l in [Error, Fatal] → 90 дней; @l == Warning → 30 дней; остальное → 7 дней`. Глобальный hard cap по размеру файла.
    - **Retention-фильтры = saved queries.** Retention-правило не имеет своего редактора запросов — ссылается на сохранённый запрос из workspace. Удаление saved query, на который ссылается retention, блокируется с предупреждением "используется в retention-политике {name}". Это единственное UI-отличие retention-фильтра от обычного сохранённого запроса.

## 4. UI и Фронтенд
- **Движок:** ASP.NET Core Razor Pages (SSR).
- **Интерактивность:** htmx (динамическая подгрузка контента без перезагрузки страницы).
- **Скрипты:** jQuery (для сложных UI-манипуляций) + Alpine.js (опционально, для простого локального стейта, например, открытия модалок).
- **Общие компоненты (Shared Library):**
    - Авторизация (Login/Logout).
    - Общий макет (Layout) на готовой component-библиотеке поверх Tailwind (DaisyUI / Flowbite) с тёмной темой из коробки (`dark`/`night`/`business`). Кастомизация запрещена — берём как есть. Конкретная библиотека выбирается в первом frontend-спринте.
    - Уведомления (Toasts) через htmx-события.
- **Редактор запросов:** обычная `<textarea>` (KQL-запросы короткие, Monaco — overkill 5 MB + web workers). Автокомплит — server-side через htmx: debounced keyup → `hx-post /api/kql/completions?q=...&pos=...` → сервер прогоняет через `Kusto.Language` (`KustoCode.GetCompletions(position)`) → HTML-дропдаун через OOB-swap. Вся intelligence на бэке, на фронте — 0 логики парсинга KQL.
- **Live tail (v1):** `htmx` SSE-extension (https://htmx.org/extensions/sse/). Сервер пушит HTML-фрагменты, htmx делает out-of-band swap. SSE выбран вместо `/ws/` потому что поток односторонний.
- **Стратегия под высокий event rate:**
    - **Sliding window:** сервер держит на SSE-подписчика окно ~100 последних событий (на экране в 99% сценариев видно меньше — гнать больше бессмысленно и опасно для браузера).
    - **Coalescing:** события батчатся в N-миллисекундных окнах, отправляются одним HTML-фрагментом.
    - **Rate cap:** если input rate превышает окно, старейшие в окне вытесняются; клиент видит маркер разрыва ("N событий пропущено").
    - **Viewport awareness:** пока пользователь не наверху списка, новые события не префендятся — накапливаются в индикаторе "N новых"; клик/скролл к верху применяет.
    - **Маркер разрыва = точка пагинации:** скролл в него вызывает обычный `before`-cursor load-more (покрыто §7). Высокий трафик не создаёт новых механизмов — переиспользует существующие.
- Переход на client-side JSON-рендер — только при упоре в производительность рендера в браузере; это осознанный шаг в сторону SPA.

## 5. Сборка фронта
- **Стек:** TypeScript + Tailwind через npm, сборка через bun (встроенный bundler, TS из коробки, нативный Windows-бинарник).
- **Зависимости:** `package.json` рядом с `.csproj`, devDependencies: `tailwindcss`, `typescript`. Сам bun — бинарник, не в `package.json`.
- **Dev:** два терминала — `dotnet watch run` и `bun run dev` (параллельно `bun build ts/admin.ts --outdir=wwwroot/js --watch` + `tailwindcss -i ts/app.css -o wwwroot/css/app.css --watch`). CSS/JS — статика из `wwwroot`, браузер подхватывает без рестарта приложения.
- **Release:** MSBuild target `BeforeTargets="Build"` с `Condition="'$(Configuration)' == 'Release'"` — `bun install --frozen-lockfile && bun run build` (`build` = `typecheck` + минифицированные js/css). CI через `dotnet publish -c Release` собирает всё разом.

## 6. Хранилище: master DB + per-workspace
- **Master DB** (один файл SQLite): пользователи, API-ключи (глобальный реестр), список workspace'ов, глобальные retention-дефолты, аудит.
- **Per-workspace пара файлов:**
    - `{workspace}.logs.db` — сами события (горячие данные, высокий write throughput, агрессивный retention/shrink).
    - `{workspace}.meta.db` — сохранённые запросы, shared links, per-workspace конфиг, allowlist'ы индексации и маскировки.
- **Обоснование разделения:** retention чистит `logs.db`, не трогая meta; shrink логов не блокирует чтение сохранённых запросов; бэкап/экспорт разных частей с разной частотой.
- **Workspace ID:** пользовательский slug, regex `^[a-z0-9][a-z0-9-]{1,39}$` (стиль Docker image names — `acme-prod`, `yobapub`). Зарезервированный префикс `$` только для системных (`$system`). Ренейминг на старте не поддерживается (ломает shared-links и URL-ы) — добавляется позже с редиректами.

## 7. Инварианты query-слоя
- ✅ Пагинация только cursor-based (композитный ключ по `@t` + Id).
- ✅ Все фильтры транслируются в native query backend'а (SQL через `linq2db`) — никогда в память.
- ✅ Страница — единственная материализация; всё остальное — streaming/cursor.
- ❌ Запрещён load-all → LINQ-фильтр (антипаттерн из yobapub `LogStore.QueryWithCursor`).
- ❌ Запрещён `.ToList()` перед фильтром.
- ❌ Запрещена offset-пагинация на больших таблицах.
- ⚠ Фильтр по неиндексированному Properties-пути допустим, но UI показывает предупреждение о full scan.

## 8. Self-observability
- Сервис пишет свои события через собственный ingestion-путь в зарезервированный workspace `$system`.
- **Защита от рекурсии:** фильтр по category в `LiteDbLoggerProvider` — внутренние категории (`YobaLog.Ingestion.*`, `YobaLog.Query.*`, `YobaLog.Retention.*`) никогда не роутятся в user workspace'ы.
- **Что туда пишется:** результаты retention-проходов, ingestion-ошибки (malformed CLEF, rate-limit rejects), query-статистика (медленные запросы, full scan'ы), аудит админских действий.
- **Admin UI:** системный workspace скрыт для не-админов, но доступен через тот же query engine/KQL. Retention-политика отдельная, более консервативная.

## 9. Локализация
- **Стартовый язык:** английский. Русский — отложен, но каркас предусматривает.
- **Механизм:** `IStringLocalizer` (ASP.NET Core Localization) + `.resx` ресурсы на culture. Ключи в коде — короткие английские идентификаторы (не фразы).
- **Конвенция ключей:** dot-notation (`page.logs.header`, `errors.invalid_query`). Нативно ложится в `.resx` через namespaces и на JSON-словарь для фронта без трансформаций.
- **Scope:** все user-facing строки (UI labels, validation messages, API error responses) идут через локализатор. Хардкод строк в разметке/коде запрещён.
- **Форматирование:** числа, даты, pluralization — через culture-aware API. TZ пользователя хранится отдельно от language.
- **Frontend:** использует те же серверные ресурсы через inline-инжект. На SSR-рендере backend кладёт в `<head>` JS-объект `window.__i18n = {...}` только с текущей локалью. TS-код читает как типизированный dict (типы генерятся из `.resx` ключей). Плюсы: единый источник (`.resx`), никаких лишних HTTP, смена языка — через reload страницы, словарь обновляется бесплатно. Отвергнуто: отдельный `/api/i18n/{locale}`-endpoint (лишний раунд, дублирование источника) и build-time codegen в TS-модули (ломает цикл правок строк без ребилда фронта).
- **Переключение:** per-user в профиле; UI-язык не зависит от `Accept-Language` браузера (во избежание путаницы между языком и тайм-зоной).

## 10. Storage abstraction (`ILogStore`)

Единственная точка взаимодействия query engine'а, retention-сервиса, ingestion-pipeline с хранилищем. Все остальные компоненты работают только через этот интерфейс — чтобы свап на specialized engine (Lucene.NET, DuckDB, chDB) в будущем был заменой одной реализации, а не "перепроектированием трёх слоёв".

### Контракт

```csharp
public interface ILogStore
{
    // Ingestion
    ValueTask AppendBatchAsync(string workspaceId, IReadOnlyList<LogEvent> batch, CancellationToken ct);

    // Query через AST (не через backend-specific query)
    IAsyncEnumerable<LogEvent> QueryAsync(string workspaceId, KustoCode ast, QueryOptions opts, CancellationToken ct);
    ValueTask<long> CountAsync(string workspaceId, KustoCode ast, CancellationToken ct);

    // Retention
    ValueTask<long> DeleteAsync(string workspaceId, KustoCode predicate, CancellationToken ct);
    ValueTask<long> DeleteOlderThanAsync(string workspaceId, DateTimeOffset cutoff, CancellationToken ct); // fast path для time-based

    // Schema
    ValueTask DeclareIndexAsync(string workspaceId, string propertyPath, IndexKind kind, CancellationToken ct);

    // Management
    ValueTask CreateWorkspaceAsync(string workspaceId, WorkspaceSchema schema, CancellationToken ct);
    ValueTask DropWorkspaceAsync(string workspaceId, CancellationToken ct);
    ValueTask CompactAsync(string workspaceId, CancellationToken ct); // может быть no-op
    ValueTask<WorkspaceStats> GetStatsAsync(string workspaceId, CancellationToken ct);
}

public sealed record QueryOptions(int PageSize, ReadOnlyMemory<byte>? Cursor);
public enum IndexKind { BTree, Hash, FullText, Bitmap }
public sealed record WorkspaceStats(long EventCount, long SizeBytes, DateTimeOffset? OldestEvent);
```

### Ключевые решения контракта

- **AST, не строки.** Query и Delete принимают `KustoCode`, а не SQL/BsonExpression. Backend сам знает, как превратить AST в свой query-формат.
- **Opaque cursor.** `QueryOptions.Cursor` — байты. Backend сам кодирует/декодирует: для SQLite — `(timestamp, id)`; для DuckDB — то же; для segment-based engine — `(segment_id, row_offset)`. Интерфейс не знает и не хочет знать.
- **Batch append основной путь.** Не `AppendAsync(single)` — только batch. Row-oriented эмулирует через одну запись; segment-based — нативная семантика.
- **Два пути delete.** `DeleteAsync(predicate)` — общий (любой фильтр через AST). `DeleteOlderThanAsync(cutoff)` — fast-path для retention, нативен для всех backend'ов (time-range query, у специализированных движков — drop сегмента).
- **`DeclareIndex` с kind-подсказкой.** Специализированные движки могут игнорировать (Lucene всегда inverted, columnar всегда columnar); SQLite мапит `BTree` → expression-index, `FullText` → FTS5 virtual table; LiteDB — только BTree, остальное — no-op с warning.
- **`Compact` опциональный.** SQLite — VACUUM; LiteDB — Shrink; DuckDB — no-op (авто-compaction); Lucene — segment merge. Implementation-specific, вызывается retention-сервисом после крупных удалений.
- **`GetStatsAsync` для self-observability.** Retention использует (пороги), UI показывает, `$system` workspace логирует изменения.

### Что это даёт

- Swap LiteDB → SQLite+FTS5 → DuckDB → Lucene.NET — **одна новая реализация интерфейса**, остальной код не трогается.
- dual-executor тесты работают против любой реализации автоматически.
- retention policy (KQL-фильтр + time cutoff) маппится на `DeleteAsync` + `DeleteOlderThanAsync` одинаково для всех бэкендов.
