# Технический долг

Живой реестр. **Обновляется вручную по ходу работы** — когда пункт закрывается, зачёркивается `~~...~~` с указанием commit'а и даты; новые долги добавляются сверху в соответствующей секции. Периодический полный аудит — один раз в ≈3 месяца, записывается как новая datestamped-секция "Аудит N" с суммарным diff'ом.

## Аудит 1 — 2026-04-21

Первый систематический проход. 27 пунктов в 8 категориях, от bench-измеренных проблем до косметики. Приоритеты — actionable (делаем сразу), latent (ждут trigger'а), architectural (требуют решения), и gated drafts (не долг, отмечены для полноты).

### 🔴 Измеренные perf-проблемы

1. **Query latency под concurrent ingest = ~66× slowdown.** `perf-baseline.md:88`. SQLite writer-lock сериализует writes; WAL пускает readers параллельно, но write-транзакция блокирует их all-at-once. На сотнях событий/сек терпимо, на тысячах — нужен Tier-3 NBomber scenario. Плановый fix: writer-thread per workspace (`plan.md` open question). Trigger: жалобы на latency под нагрузкой.
2. **FTS5 MATCH в ~100× медленнее LIKE на частых словах.** `perf-baseline.md:59`, root cause в `decision-log.md` 2026-04-19. `IN (SELECT rowid FROM fts WHERE MATCH)` материализует rowid-set до внешнего LIMIT. `ANALYZE` проверен — не помогает (структурная проблема, не stats). Fix-план: переписать `FtsHas` на raw SQL с `JOIN EventsFts ... LIMIT N`. Не приоритет — `has` опциональный оператор, редкий.
3. **Summarize аллоцирует 23 MB на 100k строк.** `perf-baseline.md` Tier 1. `object?[]` per row + boxing в `Dictionary`-ключах. "Место для оптимизации когда станет узким местом". Сейчас не узкое.

### 🟠 Латентные баги (реальные defects без bug-report'ов)

4. ~~**`RetentionService` не видит admin-created workspaces без API-ключей.** `src/YobaLog.Core/Retention/RetentionService.cs:65` итерирует `_apiKeys.ConfiguredWorkspaces`. После Phase D можно создать workspace через `/admin/workspaces` без добавления ключей → retention на нём никогда не сработает → `.logs.db` растёт навсегда. Fix: инжектить `IWorkspaceStore`, итерировать `ListAsync`. 5-10 строк + один тест. **Приоритет: high.**~~ — closed 2026-04-21 (`refactor(debt): close top priorities from audit`). Regression-тест `AdminCreatedWorkspace_WithoutApiKeys_IsSweptByRetention` добавлен.
5. **Timestamp в dual-executor.** `plan.md:44`. `kusto-loco` странно обрабатывает `datetime()`-литерал; production-путь корректен, reference-case закомментирован. Риск — регрессия production-transformer'а на datetime-литералах не поймается dual-executor'ом. Workaround-custom-literal implementation или downgrade на голый `Kusto.Language`.
5a. ~~**`/health` 302 → `/Login` на HEAD-запросах.** На первом живом деплое 2026-04-21 `curl -I http://127.0.0.1:8082/health` вернул 302 на `/Login`; `curl` без `-I` (GET) — 200 OK. Root cause: `app.MapGet("/health", ...)` регистрирует endpoint только для GET. HEAD-запрос (curl -I, k8s httpGet с method=HEAD, uptime monitors) не матчится → `HttpContext.GetEndpoint() == null` в UseAuthorization middleware → fallback policy `RequireAuthenticatedUser` применяется → cookie auth challenge → 302 на LoginPath. DockerSmoke не ловил потому что curl без `-I` = GET. Fix: `MapMethods("/health", ["GET", "HEAD"], ...)`.~~ — closed 2026-04-22. HealthEndpointTests parameterized на [Theory] GET+HEAD × /health+/version (4 теста) гарантируют regression.

### 🟡 Заявленное неполное покрытие (отложено до багов)

6. **Edge-case тесты cursor-пагинации.** `plan.md:163`. Duplicate timestamps, cursor на удалённую запись, empty/last-page-exact-at-PageSize. Добавить при появлении багов.
7. **API-keys coverage неполный.** `plan.md:168`. Не покрыто: expired keys, rate-limit, unscoped key / `$system` access. Предполагалось закрыть в Фазе D — Фаза D закрыта, эта волна не прилетела.
8. **Python seqlog compat-тест.** `spec.md:40`. Заявлен как target, не покрыт.

### 🔵 Открытые архитектурные вопросы (требуют решения)

9. **VACUUM policy.** `plan.md:179`. После retention-прохода / по расписанию / по % deleted? SQLite-файлы пухнут пропорционально объёму delete'ов без периодического VACUUM.
10. **Props indexing UX.** `plan.md:185`. `DeclareIndexAsync` есть в `ILogStore` (spec §10), но операторского UI нет; автодетект горячих путей не задуман; защиты от high-cardinality (UUID-keys) нет.
11. **Time handling в UI.** `plan.md:186`. Частично решено (`decision-log.md` 2026-04-19 "рендер в TZ смотрящего"), но open-question-bullet в plan не вычеркнут.
12. **Write throughput scaling.** `plan.md:178`. Channels-based writer per workspace задуман, не реализован. Связан с #1.

### ⚪ Code hygiene / stale rationale

13. ~~**`#pragma warning disable CA1822` "instance-scoped for future DI"** в `src/YobaLog.Core/Kql/KqlTransformer.cs:12` и `src/YobaLog.Core/Kql/KqlCompletionService.cs:5`. Комментарий от Phase B (март); Phase D закрыта, DI у этих классов так и не появился. Либо инжектить реальные options, либо сделать методы static и снять pragma.~~ — closed 2026-04-21 (`refactor(debt): close top priorities from audit`). Оба класса → `static class`, pragma'ы сняты, `AddSingleton<KqlCompletionService>()` убран.
14. **Большие файлы.** `KqlTransformer.cs` 637 строк, `admin.ts` 448 строк. Обе — growing jointly-responsible. Не критично, но кандидаты на split: `KqlTransformer` → `Apply` (event-shape) и `Execute` (shape-changing) в разных файлах; `admin.ts` → модули `kql-completion.ts` / `live-tail.ts` / `share-modal.ts` / `filter-chips.ts`. Порог — когда следующая фича заставит лезть в один из файлов.
15. ~~**Дублирование NoWarn в test-csproj'ах.** `CA1707;CA1848;CA1861;CA1873;CA2007` повторяются в `YobaLog.Tests.csproj`, `YobaLog.E2ETests.csproj`, `YobaLog.Benchmarks.csproj`. Поднять в `Directory.Build.props` под `<When Condition="'$(IsTestProject)' == 'true'">`, оставить в каждом csproj только расширения.~~ — closed 2026-04-21 (`refactor(debt): close top priorities from audit`). Общий список переехал в `Directory.Build.targets` под `'$(IsTestProject)' == 'true'` (props грузится раньше csproj'а → IsTestProject ещё пуст; targets — позже). `YobaLog.Benchmarks.csproj` не IsTestProject, сохраняет свой список. `YobaLog.E2ETests.csproj` держит только расширения (`CA1001;CA1711`), `YobaLog.Tests.csproj` — пусто.

### 🧪 Brittleness тест-инфраструктуры (скрытые зависимости от деталей)

16. **xUnit 2.x `ITestOutputHelper.test` private-field reflection** в `TraceArtifact.ExtractTestName`. Работает на 2.9.3, сломается на xUnit 3 (когда будем апгрейдить). Замена — `TestContext.Current` в xUnit 3.
17. **`IsRunningOnWindows()`-branch в Cake smoke-test curl** из-за `/dev/null` vs `NUL`. Работает, но выдаёт что Cake гоняется на Windows-dev-box'е; CI-only runner'ы эту ветку не касаются, так что `NUL`-путь не регрессирует сам.
18. **Htmx локальные копии** в `tests/YobaLog.E2ETests/Fixtures/htmx/` — пинованы к версиям `_Layout.cshtml`. При бампе CDN-версии в Layout'е легко забыть обновить fixture → скрытая регрессия в E2E. Guard: pre-commit hook или CI-check на diff между Layout'овой версией и фикстурой.
19. **Блок unpkg.com в Playwright route** (`WebAppFixture.cs`) — hardcoded subdomain. Смена CDN (jsdelivr?) потребует обновления route.
20. **Storage-state auth helper** логинится через форму один раз и сохраняет cookie. Ломается при изменении `/Login` DOM (переименование testid, смена flow на OAuth).
21. **`AdminUsersTests` оставляет state внутри shared fixture** если падает до `DisposeAsync`. Clean-up есть, но try-catch вокруг создания нет — test-failure в initialize может оставить DB-user.
22. **Fresh-slug workspace'ы** в `InfiniteScrollTests`, `ShareLinkTests`, `HtmxLiveTailTests` и др. создают DB-файлы без cleanup. Fixture-level temp-dir умирает вместе с фикстурой, но в течение life-cycle десятки `.logs.db` скапливаются.

### 📄 Устаревшие docs / конфигурация

23. ~~**`AGENTS.md` line 13**: *"Status: pre-code, spec-stage"*, *"There is no code yet"*. Абсолютно устарело (Phase A.0–D закрыты, 367 тестов зелёных).~~ — closed 2026-04-21 (`refactor(debt): close top priorities from audit`). Секция удалена.
24. ~~**`AGENTS.md` target stack**: *"+ jQuery, optional Alpine.js"* — не используем, только vanilla-TS + htmx. Плюс `(planned, not yet scaffolded)` — всё уже scaffold'нуто.~~ — closed 2026-04-21 (`refactor(debt): close top priorities from audit`). Заголовок → "Target stack", список обновлён (vanilla TS + htmx + Tailwind + DaisyUI), "(planned, not yet scaffolded)" снят.
25. **CI `permissions: packages: write`** — нужен только для `publish` job'а; `test` / `e2e` могут жить на `contents: read`. Per-job permissions — принятая практика для least-privilege.

### 🆕 Gated drafts (не долг, для полноты картины)

26. **OTel Phase F/G/H** в `plan.md` + `<!-- proposal -->` в `spec.md` + `decision-log.md` entry 2026-04-21. Решения зафиксированы, кода нет; статус — ждут approval на старт Phase F.
27. **DuckDB backend** — заблокирован upstream `linq2db#5451`, external dependency.

---

## Сводка по impact / urgency

**Делать сейчас (immediate actions, выбрано из категорий ⚪/📄/🟠):**
- ~~#4 retention-service → `IWorkspaceStore` — real bug, малый fix.~~ — закрыто 2026-04-21.
- ~~#13 снять stale `CA1822`-pragma'ы в KqlTransformer/KqlCompletionService.~~ — закрыто 2026-04-21.
- ~~#15 NoWarn → `Directory.Build.props`.~~ — закрыто 2026-04-21 (через `Directory.Build.targets`).
- ~~#23–24 зачистить stale lines в AGENTS.md.~~ — закрыто 2026-04-21.

**Architectural, требуют решения сессии:**
- #9 VACUUM policy, #10 Props indexing UX, #12 writer-threading.

**Measured but not painful today:**
- #1–3 perf. Tier-3 load-тесты откладываем "когда появится hot-path" (сам принцип, sound).

**Coverage gaps ждут trigger'а:**
- #6–8 cursor-edge-cases, full API-keys, Python compat.

**Test brittleness — живи с этим:**
- #16–22 существующие workaround'ы корректные, risk — drift при upgrade'ах зависимостей / смене external hosts.

## Правила обновления

- Закрытие пункта: зачеркнуть `~~текст~~`, добавить в конце `— closed <commit-sha>, <YYYY-MM-DD>`. Не удалять — контекст для ревью.
- Новый пункт: добавить в соответствующую секцию, по возможности указать file:line + impact + trigger.
- Новый полный аудит: добавить секцию `## Аудит N — YYYY-MM-DD` перед текущей, пометить предыдущие секции как прочитанные (не удаляем).
- Связь с planом: если пункт → plan-bullet, дублировать ref в обе стороны.
