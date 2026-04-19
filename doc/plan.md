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
    - [ ] `project`, `extend`, `summarize`, `count` — нужен shape-changing result type (`KqlResult { Columns, Rows }`), большой рефакторинг. Отложено до явной необходимости.
    - [ ] FTS5 MATCH special case для `Message contains` — сейчас через LIKE (full scan, spec §7 warning ок). Оптимизация, не блокер.
    - [ ] Timestamp в dual-executor — kusto-loco странно обрабатывает `datetime()`-литерал; production корректен, reference закомментирован.
    - [x] Unsupported operators/predicates → explicit `UnsupportedKqlException` с actionable message.
- [ ] **Фаза C — swap на KQL.** Хардкод-фильтры UI заменяются на `<textarea>` с KQL (server-side автокомплит через htmx — позже). Saved queries (CRUD). Retention-политики с фильтрами-ссылками на saved queries.
- [ ] **Фаза D — usability.** Share links + маскирование UI; TSV export; live tail (SSE + sliding window).
- [ ] **Фаза E — второй бэкенд: DuckDB.** Вторая реализация `ILogStore` после мёрджа [linq2db#5451](https://github.com/linq2db/linq2db/pull/5451). Transformer пишется минимально (SQL с поправками на DuckDB-диалект), dual-executor тесты покрывают автоматически.

## Отложено / tech debt после Фазы A
Накопленный долг, намеренно пропущенный ради dog-food ready. Забираем **после** Фазы B.
- [ ] **Hashed admin password.** Сейчас Admin:Password лежит plaintext в `appsettings.Development.json`. Миграция на PBKDF2 (`Rfc2898DeriveBytes`, HMACSHA256, ~600k iterations) с версионированным форматом `v1:{iter}:{b64salt}:{b64hash}`. Заодно добавить CLI-команду `yobalog hash-password` или Razor-handler для генерации хеша из plaintext.
- [ ] **Copy-to-clipboard** в event-row: кнопка у каждого Message/Exception копирует текст (навигатор API, без deps).
- [ ] **Expandable event row:** клик по строке разворачивает JSON Properties (dict → definition list), удобно для `SourceContext`, `TraceId`, кастомных полей. Событие уже содержит `Properties` dictionary — дело только в UI.
- [ ] **Antiforgery на Login.** Сейчас `[IgnoreAntiforgeryToken]` — работает, но включим обратно, как добавится второй admin или мутирующие формы в UI.
- [ ] **Edge case тесты cursor-пагинации:** duplicate timestamps, cursor на удалённую запись, boundaries (empty result, last page exactly at PageSize). Добавить при появлении багов либо до Фазы C (когда cursor станет частью KQL-пайплайна).

## Тестовое покрытие до фазы A (что ещё нельзя пропустить)
- [x] **CLEF-парсер:** tolerant NDJSON (кривая строка не убивает батч), partial-batch ack, malformed `@t`, missing required fields. _(CleFParserTests, 20 тестов)_
- [x] **Ingestion pipeline:** Channels-based writer под нагрузкой — no event loss при `Count > capacity`; корректный shutdown-drain; per-workspace isolation. _(ChannelIngestionPipelineTests, 7 тестов)_
- [~] **API-ключи:** невалидный/missing → 401; валидный + правильный workspace → 201. **Не покрыто:** expired keys, rate-limit, unscoped key/$system access (нужна модель прав). Отложено до Phase D/E. _(IngestionEndpointTests + ConfigApiKeyStoreTests, 12 тестов)_
- [x] **Retention executor:** удаляет ровно то, что в фильтре; идемпотентен; concurrent ingestion не блокируется. _(RetentionServiceTests, 6 тестов)_
- [x] **Cursor pagination:** базовая пагинация. **Не покрыто:** duplicate timestamps, cursor на удалённую запись, boundaries — добавить при появлении edge-case багов. _(SqliteLogStoreTests)_
- [x] **Workspace isolation:** данные из workspace A физически недостижимы запросом из B. _(SqliteLogStoreTests)_
- [ ] **Маскирование:** HMAC детерминирован (один вход → один выход); удалённый share = 404; expired = 410. _(Phase D)_

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
