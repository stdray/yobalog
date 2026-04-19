# YobaLog: План работ и стратегия тестирования

## Dual-executor (инвариант тестов query engine)
- **Reference executor:** `kusto-loco`'s `KustoQueryContext` — готовое in-memory исполнение KQL над `IEnumerable<LogEvent>`. Сами не пишем — доверие к oracle выше (кто тестирует тестировщика — в нашем случае MS + NeilMacMullen).
- **Production executor:** Kusto AST → SQL через `linq2db` для SQLite+FTS5 (full-text search по Message транслируется в FTS5 MATCH). Позже — та же схема для DuckDB.
- **Property-тесты:** одна KQL-строка + фикстурный dataset прогоняются через reference и через production executor; результаты обязаны совпадать. Любое расхождение = баг трансформера.
- **Следствие:** добавление нового бэкенда требует только реализации трансформера — тесты уже готовы. Без reference executor'а каждый бэкенд тестируется отдельно, одинаковые баги пропускаются.
- **Downgrade path:** если `kusto-loco` заглохнет — переходим на голый `Kusto.Language` и пишем свой executor. Transformer'ы не меняются (Kusto AST стабилен, поддерживается MS).

## Фазы
- [x] **Фаза A.0 — Bootstrap.** Репо-гигиена и тулинг, без кода приложения. Цель — задать тон один раз, чтобы не переделывать по каждому PR.
    - [x] `.gitignore` (bin/obj, node_modules, wwwroot-генерёжка, `*.db`/`*.db-wal`/`*.db-shm`, `.vs/`, user secrets).
    - [x] `.gitattributes` — LF в сорцах, CRLF в `.cmd/.bat/.ps1`, `bun.lock` как text, `.sln` как CRLF.
    - [x] `global.json` — пин .NET 10 SDK 10.0.202 (rollForward = latestFeature).
    - [x] `Directory.Build.props` на корне: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`, `<AnalysisMode>All</AnalysisMode>`, `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<LangVersion>latest</LangVersion>`, `<InvariantGlobalization>true</InvariantGlobalization>`.
    - [x] `Directory.Packages.props` — Central Package Management включён (`<CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>`), versions пока только тестовые (xunit, FluentAssertions, coverlet, Microsoft.NET.Test.Sdk).
    - [x] Solution skeleton: `YobaLog.slnx` (новый формат .NET 10) + `src/YobaLog.Core` (classlib) + `src/YobaLog.Web` (Razor Pages webapp, ссылка на Core) + `tests/YobaLog.Tests` (xunit, ссылка на Core). Шаблонные csproj очищены — общие property переехали в `Directory.Build.props`, Version из `PackageReference` убраны (CPM).
    - [x] Фронт-бутстрап рядом с `YobaLog.Web`: `package.json`, `tsconfig.json` со всеми strict-флагами + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes` + `noImplicitOverride` + `noUnusedLocals` + `noPropertyAccessFromIndexSignature`, `tailwind.config.js`, заглушки `ts/admin.ts` и `ts/app.css`, `bun.lock` (текстовый, закоммичен).
    - [x] `biome.json` — lint + format для TS; табы/ширина 2 для всего включая json (json формат табы разрешает); `noExplicitAny: error`, `noNonNullAssertion: error`, `useConst: error`. `useEditorconfig` **отключён** — biome 1.9 не парсит `indent_size = tab`, а менять editorconfig нельзя (`indent_size = tab` нужен dotnet format для корректной обработки .cs). Подчинили biome своим настройкам, editorconfig остаётся авторитетом для .NET. YAML в `.editorconfig` = space-2 (табы в YAML запрещены спекой формата, не редакторами).
    - [x] MSBuild target в `YobaLog.Web.csproj`: `BeforeTargets="Build"` + `Condition="'$(Configuration)' == 'Release'"` → `bun install --frozen-lockfile && bun run build`. В Debug MSBuild не трогает фронт (dev запускается отдельно).
    - [x] CI skeleton (`.github/workflows/ci.yml`): `bun install` + `biome check` + `bun run typecheck` + `dotnet restore` + `dotnet format --verify-no-changes` + `dotnet build -c Release` + `dotnet test -c Release`. Один workflow на push/PR в main.
    - [x] Smoke-test: всё зелёное локально — `biome check` ✓, `tsc --noEmit` ✓, `dotnet format --verify-no-changes` ✓, `dotnet build -c Release` (включая `bun run build` через MSBuild target) ✓, `dotnet test` 1/1 passed ✓.
- [ ] **Фаза A — dog-food ready.** Ingestion (`/api/events/raw`, CLEF, API-ключи, workspace routing), retention по дате (одна цифра, без политик), минимальный viewer (логин + cursor-пагинация + хардкод-фильтры: Level/Category/TraceId/time-range/substring), `$system` workspace. На этой фазе уже перенаправляется Serilog из собственных сервисов — сервис работает.
    - [x] Доменные типы в Core: `LogLevel`, `WorkspaceId` (slug-валидация), `LogEvent`, `LogEventCandidate`, `ILogStore`, `LogQuery`.
    - [x] CLEF-парсер (`CleFParser`) — tolerant NDJSON, `@t`/`@l`/`@m`/`@mt`/`@x`/`@i`/`@tr`/`@sp`, `@@`-escape, reserved `@*` skip.
    - [x] Ingestion pipeline — `Channels` per workspace, bounded + Wait, writer-loop, `IHostedService`, shutdown-drain, per-workspace isolation.
    - [x] `SqliteLogStore` — linq2db + SQLite, WAL, cursor pagination (16-байтный opaque key), индексы, FTS5 инфраструктура (для Phase C), substring через LIKE.
    - [x] API-ключи — `IApiKeyStore` + `ConfigApiKeyStore` (из `appsettings`), `X-Seq-ApiKey` header + `?apiKey=` query.
    - [x] `POST /api/events/raw` — 201 с partial-batch ack `{received, errors}`, 401 на bad/missing key.
    - [x] `WorkspaceBootstrapper` — на старте создаёт `$system` + все workspaces из API-ключей.
    - [x] Retention service — `BackgroundService`, гоняет `DeleteOlderThanAsync` per workspace; отдельный `SystemRetentionDays` для `$system`.
    - [x] Self-observability — `SystemLoggerProvider` + `SystemLogFlusher`, фильтр по префиксу категории (`YobaLog.*`), собственные логи в `$system` через прямой `ILogStore.AppendBatchAsync` (минуя pipeline, чтобы не словить рекурсию). Queue-full → drop (DropWrite).
    - [x] Минимальный viewer — Razor Pages: список workspaces (`/`), event-таблица (`/ws/{id}`) с фильтрами (message substring / min level / trace / from-to) и cursor-пагинацией. Tailwind + DaisyUI, `data-theme="dark"`. Cookie-auth с единственным админом из `appsettings` (Username+Password, constant-time сравнение через `CryptographicOperations.FixedTimeEquals`). `/api/events/raw` остался `AllowAnonymous` — API-ключи не ломаются. Протестировано end-to-end через Playwright MCP (Edge): unauth → redirect на `/Login`, wrong creds → alert, correct → `ReturnUrl` ведёт обратно, logout чистит cookie.
- [~] **Фаза B — transformer и dual-executor тесты.** Parser/AST/reference executor — готовые (`Kusto.Language` + `kusto-loco`).
    - [x] Visitor по Kusto AST → LINQ expression tree (через `linq2db` → SQL для SQLite).
    - [x] Операторы: `where`, `take`, `order by` (single + multi, asc/desc).
    - [x] Предикаты `where`: `==`, `!=`, `<`, `<=`, `>`, `>=`, `contains` (case-insensitive), `and`, `or`, `not()`.
    - [x] Колонки: `Id` (long), `Level` (int rank 0..5), `LevelName` (string), `Timestamp` (datetime literal → Unix ms), `TraceId`, `SpanId`, `Message`.
    - [x] Dual-executor тесты — 34 property-случая, reference = `KustoQueryContext`, production = `IQueryable<EventRecord>`.
    - [x] `SqliteLogStore.QueryKqlAsync` — реальный SQL через linq2db, 7 integration-тестов.
    - [x] `project`, `extend`, `summarize`, `count` — `KqlResult { Columns, Rows }` как loose-typed result, `Execute` разбивает pipeline на SQL-pushdown prefix (where/take/order) + in-memory shape-changing suffix. MVP scope: `project <col|alias=col>`, `extend <alias=col>`, `count`, `summarize count() [by <cols>]`. Computed expressions + другие агрегаты throw с actionable message. Viewer пока использует старый `QueryKqlAsync` (event-shape), миграция на `QueryKqlResult` + generic renderer — когда появится usecase.
    - [x] FTS5 MATCH — добавлен KQL-оператор `has` для `Message`. Транслятор детектит `Message has '...'` и зовёт `KqlSqlExpressions.FtsHas(id, message, term)` с `[Sql.Expression("{0} IN (SELECT rowid FROM EventsFts WHERE Message MATCH {2})", IsPredicate=true)]` — linq2db рендерит FTS5-subquery, использует indexed MATCH вместо full scan. C#-fallback через word-boundary Tokenize (для in-memory/dual-executor). `has_cs` не поддержан (FTS5 default tokenizer case-insensitive). `contains` по-прежнему LIKE full scan по spec §7. 3 integration-теста + 4 dual-executor случая.
    - [ ] Timestamp в dual-executor — kusto-loco странно обрабатывает `datetime()`-литерал; production корректен, reference закомментирован.
    - [x] Unsupported operators/predicates → explicit `UnsupportedKqlException` с actionable message.
- [x] **Фаза C — swap на KQL.** Viewer прозрачно переехал на KQL: форма та же, page-model билдит KQL-строку и гоняет через `SqliteLogStore.QueryKqlAsync`. Pagination через `where Timestamp < cursor or (Timestamp == cursor and Id < id)`. Протестировано через Playwright end-to-end (55 событий, 2 страницы, фильтры сходятся).
    - [x] Form → KQL builder в `WorkspaceModel.BuildKql()` — from/to/minLevel/trace/message/cursor.
    - [x] `ILogStore.QueryKqlAsync` — часть контракта; `SqliteLogStore` реализует.
    - [x] `<textarea>` с сырым KQL — collapsible под формой. Когда непусто → форма дизейблится, `RawKql` летит в page-model. Pager (order by, take, cursor) всегда добавляется на сервере. Parse-ошибки и `UnsupportedKqlException` рендерятся красным alert'ом с сохранением пользовательского ввода.
    - [x] Server-side автокомплит через htmx + `KustoCodeService.GetCompletionItems`. Endpoint `GET /api/kql/completions?q=...&pos=...` возвращает HTML-fragment с дропдауном. Схема `events` зарегистрирована в `GlobalState` — completions знают про Id/Timestamp/Level/LevelName/TraceId/SpanId/Message/Exception. Insert-on-click через minimal TS (`ts/admin.ts`): замена EditRange → BeforeText+AfterText, caret между ними, Escape закрывает панель. Live-проверено: `Le` → {Level, LevelName}; после `|` → {where, count, extend, join, …}; click вставляет `LevelName ` с пробелом-разделителем.
    - [x] Saved queries CRUD — `ISavedQueryStore` + `SqliteSavedQueryStore`, `.meta.db` per workspace (spec §6). Upsert by name, List ordered. UI: chip-list с delete-кнопками, "Save as…"-форма, клик по chip → `?saved=<name>` подгружает KQL. Live-проверено.
    - [x] Retention-политики с ссылками на saved queries — `RetentionPolicy { Workspace, SavedQuery, RetainDays }` список в конфиге. Если у workspace есть policies, применяются они (каждая = saved query filter + Timestamp < cutoff); если нет — fallback `DefaultRetainDays`. `$system` всегда по `SystemRetainDays`. Missing saved query → warn + skip. Новый `ILogStore.DeleteKqlAsync(WorkspaceId, KustoCode)` через transformer → linq2db DELETE. 3 новых теста (+4 обновлённых).
- [ ] **Фаза D — KQL-first UI + админка.** Интерфейс, который заточен под KQL вместо формы фильтров, и DB-backed CRUD для админских сущностей вместо конфига.
    - [ ] **KQL-first UI.** Форма фильтров (Message/Level/Trace/From/To) была нужна до появления автокомплита и виртуального KQL-редактора — сейчас дублирует KQL-builder. Делаем KQL-textarea expanded по дефолту, форму либо коллапсим, либо вовсе удаляем (решим на живом прогоне после переноса).
    - [ ] **Hover-фильтры в таблице событий.** Над ячейкой при наведении — всплывают ✓ и ✗. ✓ → дописать `| where Field == "value"` в KQL. ✗ → `| where Field != "value"`. Для Timestamp метки "искать от" / "искать до" и оба inclusive (`>=` / `<=`), чтобы строка, на которой кликнули, оставалась в выборке. MVP — append-only; smart-merge (заменять существующий предикат по тому же полю) — позже, если надоест чистить хвост вручную.
    - [ ] **Админка через UI.** Сейчас всё config-driven: workspaces косвенно через `ApiKeys.Keys[].Workspace`, API-ключи plain в `ConfigApiKeyStore`, retention policies массивом в `Retention.Policies`. Переезжаем на DB-бэкед стораджи в `$system.meta.db`:
        - `IWorkspaceStore` + CRUD-страница `/admin/workspaces`. Rename/delete — через `ILogStore.DropWorkspaceAsync` + чистка `.meta.db`.
        - `IApiKeyStore` DB-backed (заменяет `ConfigApiKeyStore`): хранить `sha256(token)` + первые 6 символов для UI-идентификации, plaintext показывать **один раз** при создании. Миграция config → DB при старте, если DB пустая.
        - `IRetentionPolicyStore` + `/admin/retention` — CRUD полиси per workspace, ссылается на saved query по имени (как сейчас).
        - `IUserStore` + `/admin/users` — мульти-админ. Хеши через существующий `AdminPasswordHasher`. Login проверяет DB, fallback на config-admin пока DB пустая. CRUD: create, change password, delete.
        - **Antiforgery на Login** включить обратно (сейчас `[IgnoreAntiforgeryToken]`) — в админке появятся мутирующие формы, делать токены только для них = лишнее ветвление.
- [~] **Фаза E — usability-полировка.** Share links + маскирование UI (готово); infinite scroll (готово); live-tail polish.
    - [~] **Live tail / auto-refresh:** базовый контур готов. `ITailBroadcaster` + `InMemoryTailBroadcaster` (Channel per-subscriber, Bounded DropOldest, KQL-фильтр per-subscription — publish общий на workspace, каждый subscriber прогоняет через compiled `KqlTransformer.Apply`). `ChannelIngestionPipeline` публикует батчи после успешного AppendBatchAsync. `GET /api/ws/{id}/tail?kql=<>` SSE-endpoint через `RazorPartialRenderer` → partial `_EventLiveRow.cshtml` (single source of truth для row markup). Viewer: toggle "Live tail" динамически создаёт `<div hx-ext="sse" sse-connect=...>`, htmx-ext-sse делает `afterbegin` swap в `#events-body`. CSS `event-live` → flash-animation (DaisyUI primary-color fade 1.4s). Shape-changing KQL (project/summarize/count/extend) rejected at subscribe. Проверено через Playwright. **Остаётся:** coalescing (N-ms окна), rate cap + drop marker, viewport awareness ("N новых" индикатор), reconnect at filter-change, persistence window (не per-subscriber 100, а скользящий общий).
    - [ ] **Viewport awareness:** пока пользователь не наверху — новые события не префендятся, накапливаются в индикаторе "N новых"; клик/скролл к верху применяет.
    - [x] **Infinite scroll (вместо "Load older"):** sentinel-row в конце `<tbody>` с `hx-trigger="intersect once"` фетчит следующую страницу через ту же `/ws/{id}` URL. `WorkspaceModel.OnGetAsync` при `HX-Request` возвращает `Partial("_RowsFragment", this)` — те же rows + новый sentinel. htmx 2.0 `outerHTML`-swap на `<tr>` использует template-fragments автоматически. Copy/expand handlers в `admin.ts` делают event-delegation на `document`, так что работают на новых строках без `htmx.process`. Верхняя граница (cursor-based "show newer") — когда появится use-case. Spec §7 запрещает offset.
    - [x] **Share → masked TSV** per spec §3. DB-backed links: `ShareLink(Id, Kql, CreatedAt, ExpiresAt, Salt, Columns, Modes)` в `.meta.db` per workspace, Id — `ShortGuid` (22-char base64url от Guid.NewGuid). `GET /share/{ws}/{id}.tsv` anonymous, 404 для неизвестного id, 410 Gone + lazy-delete при expiry. `RetentionService` сметает протухшие ссылки заодно с событиями. Три режима на поле: `keep` / `mask` (детерминированный HMAC-SHA256(salt,value)→4-byte hex с префиксом из последнего сегмента пути — `email:a1b2c3d4`, связи в рамках одной ссылки сохраняются) / `hide` (поле вырезается, column тоже). Property keys — плоский namespace с top-level, top-level shadows properties на collision. Policy-per-workspace в `FieldMaskingPolicy` — модалка не переспрашивает одно и то же. Revocation — `DELETE` row (UI пока нет). HTML-preview шарированного view отложен — 99% use-case TSV-to-LLM.
- [ ] **Фаза F — второй бэкенд: DuckDB.** Вторая реализация `ILogStore` после мёрджа [linq2db#5451](https://github.com/linq2db/linq2db/pull/5451). Transformer пишется минимально (SQL с поправками на DuckDB-диалект), dual-executor тесты покрывают автоматически.

## Perf / регрессии
См. [`performance-testing.md`](performance-testing.md) для философии и tier'ов, [`perf-baseline.md`](perf-baseline.md) для актуальных чисел.
- [x] BDN-проект `benchmarks/YobaLog.Benchmarks/` + стартовые бенчи (CleFParser, KqlTransformer на всех операторах, SqliteLogStore ingest + 3 способа query).
- [x] `perf-baseline.md` как живой snapshot (TBD-таблицы, заполняются после первого `-j Short` прогона).
- [ ] **Первый честный baseline-run** — `dotnet run -c Release -- --filter "*" -j Short`, результаты в `perf-baseline.md`. ~10 минут локально.
- [ ] Stopwatch/GC.GetTotalMemory никогда — только BDN (см. антипаттерны в `performance-testing.md`).
- [ ] Tier 3 (NBomber load), memory footprint через `dotnet-counters` — когда появится hot-path; сейчас преждевременно.

## Отложено / tech debt после Фазы A
Накопленный долг, намеренно пропущенный ради dog-food ready.
- [x] **Hashed admin password.** `AdminPasswordHasher` (PBKDF2/HMACSHA256, 600k iter, 32-byte key, 16-byte salt), формат `v1:{iter}:{b64salt}:{b64hash}`. `AdminAuthOptions.PasswordHash` — приоритет над plaintext `Password`. CLI: `dotnet run --project YobaLog.Web -- --hash-password <plain>` печатает hash и выходит. 7 unit + 2 integration-теста.
- [x] **Copy-to-clipboard** — кнопки `⧉` у Message (inline), TraceId и Properties JSON в expanded-row. Vanilla JS через `navigator.clipboard.writeText` + короткая "copied"-индикация.
- [x] **Expandable event row** — клик по строке раскрывает hidden-row с Id/Timestamp(ISO 8601 full precision)/Level(name + rank)/Template/TraceId/SpanId/EventId + Properties JSON.
- [ ] **Edge case тесты cursor-пагинации:** duplicate timestamps, cursor на удалённую запись, boundaries (empty result, last page exactly at PageSize). Добавить при появлении багов.

## Тестовое покрытие до фазы A (что ещё нельзя пропустить)
- [x] **CLEF-парсер:** tolerant NDJSON (кривая строка не убивает батч), partial-batch ack, malformed `@t`, missing required fields. _(CleFParserTests, 20 тестов)_
- [x] **Ingestion pipeline:** Channels-based writer под нагрузкой — no event loss при `Count > capacity`; корректный shutdown-drain; per-workspace isolation. _(ChannelIngestionPipelineTests, 7 тестов)_
- [~] **API-ключи:** невалидный/missing → 401; валидный + правильный workspace → 201. **Не покрыто:** expired keys, rate-limit, unscoped key/$system access (нужна модель прав). Вернёмся при DB-migrate API-ключей в Фазе D. _(IngestionEndpointTests + ConfigApiKeyStoreTests, 12 тестов)_
- [x] **Retention executor:** удаляет ровно то, что в фильтре; идемпотентен; concurrent ingestion не блокируется. _(RetentionServiceTests, 6 тестов)_
- [x] **Cursor pagination:** базовая пагинация. **Не покрыто:** duplicate timestamps, cursor на удалённую запись, boundaries — добавить при появлении edge-case багов. _(SqliteLogStoreTests)_
- [x] **Workspace isolation:** данные из workspace A физически недостижимы запросом из B. _(SqliteLogStoreTests)_
- [x] **Маскирование:** HMAC детерминирован (один вход → один выход); удалённый share = 404; expired = 410. _(ShareEndpointTests + ValueMaskerTests + SqliteShareLinkStoreTests, 17 тестов)_

---

## Открытые вопросы

- [ ] **Производительность записи.** SQLite сериализует writes — потенциальное узкое место при высоком event rate. Плановое решение: `System.Threading.Channels` + один writer-loop per workspace с батчевой записью в WAL-режиме.
- [ ] **Обслуживание БД.** Автоматический `VACUUM` SQLite после срабатывания Retention Policy (маппится на `ILogStore.CompactAsync`). Когда запускать — после удаления >N процентов или по расписанию?
- [ ] **Покрытие KQL.**
    - MVP-операторы: `where`, `project`, `extend`, `summarize`, `count`, `take`, `order by`. `join` — позже.
    - Graceful degradation для unsupported operators: не "query failed", а "operator X not supported in yobalog, try Y".
    - Кастомные функции yobalog (`mask()` и т.п.) — через `Kusto.Language` custom function registration, либо layer поверх результата reference executor'а (решается при имплементации первой кастомной функции).
    - Sync между тем, что парсится в Kusto AST, и тем, что реально работает в наших бэкендах — явный allowlist; автоматическая проверка в тестах (любой AST-node вне allowlist → explicit fail на трансляции).
- [ ] **Props indexing policy.** Allowlist путей per-workspace через `ILogStore.DeclareIndexAsync` — единая стратегия (SQLite/DuckDB/Postgres expression-index на `json_extract`). Подвопросы: UX управления allowlist'ом; автодетект "горячих" путей по статистике запросов; защита от high-cardinality полей (UUID-ы как ключи Properties).
- [ ] **Time handling.** Клиентский `@t` в CLEF — ISO 8601 с offset. Seq верит клиенту, хранит UTC-эквивалент, не компенсирует clock skew. Повторяем это поведение. Подвопрос: UI — показывать время в TZ смотрящего или в TZ события?

---

## Рюшечки

Чистое UX-добро, не блокирует dog-food / никаких архитектурных решений. Делать, когда будет скучно.

- [ ] **Auto-detect чувствительных полей** в share-модалке: `email|ip|auth|token|password|ssn` в имени пути → предзаполнить radio как `mask`/`hide`, чтобы не клацать вручную.
