# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

## 2026-04-21 (draft) — OpenTelemetry integration: scope + cost assessment

**Status: RESEARCH DRAFT.** Not approved for implementation. Code changes gated on review of this entry + spec `<!-- OTel proposal -->` comments + Phase F/G/H bullets in `plan.md`.

**Problem.** Modern .NET-app hygiene 2026 includes OpenTelemetry. yobalog is uniquely positioned — it IS a log store, so OTel intersects on two sides:
- **Ingest side:** OTLP-enabled clients should be able to write to yobalog with zero adapter code.
- **Emit side:** yobalog's own operations (ingestion / query / retention) should emit OTel spans for self-observability.

Three directions analyzed; each stands on its own — not all-or-nothing.

### Direction 1 — OTLP ingestion (HTTP/Protobuf for logs): Phase F recommendation

**Recommendation: Phase F.** Smallest unit of real value — any OTel-enabled .NET / Go / Python / JS app becomes a drop-in yobalog writer without code changes.

**Path: mirror Seq's exact surface.** `POST /ingest/otlp/v1/logs` with `X-Seq-ApiKey` header. yobalog IS Seq-compatible; users who today point their OTel exporter at Seq should change only URL base and nothing else. OTel-standard `/v1/logs` path diverges from Seq's `/ingest/otlp/v1/logs` — Seq prefixes to disambiguate from its public API. We follow. Optional: also expose `/v1/logs` as an alias so OTel clients that hard-code the standard path work out of the box — trivial extra `MapPost`, open question below.

**Protocol: HTTP/Protobuf only.** HTTP/JSON (~1-2 % real deployments; Seq skipped too) and gRPC (HTTP/2 + Kestrel config + reverse-proxy-hostile) both deferred to Phase F+1 if demand appears. Matches our Dockerfile (HTTP-only, port 8080).

**OTLP LogRecord → CLEF mapping** (OTel Logs proto v1.5.0):

| OTLP                                            | CLEF                          | Notes                                                                                     |
|-------------------------------------------------|-------------------------------|-------------------------------------------------------------------------------------------|
| `time_unix_nano` (fixed64)                      | `@t`                          | ÷ 1_000_000 → ms. If 0, fallback to `observed_time_unix_nano`. If both 0, reject record.  |
| `severity_number` (SeverityNumber enum 1-24)    | `@l`                          | 1-4→Verbose, 5-8→Debug, 9-12→Information, 13-16→Warning, 17-20→Error, 21-24→Fatal.        |
| `severity_text` (string)                        | `Properties["severity_text"]` | Preserve raw; may differ from number (custom levels).                                     |
| `body` (AnyValue)                               | `@m`                          | string_value→direct. int/double/bool→ToString. array/map→System.Text.Json-serialize.      |
| `attributes` (list\<KeyValue>)                  | `Properties`                  | Flatten into flat namespace (per spec §1).                                                |
| `resource.attributes`                           | `Properties`                  | Merge with attributes; on key collision **resource wins** (deployment identity).          |
| `trace_id` (bytes[16])                          | `@tr`                         | Hex-encode → 32-char lowercase. All-zero = absent, skip.                                  |
| `span_id` (bytes[8])                            | `@sp`                         | Hex-encode → 16-char lowercase. All-zero = absent, skip.                                  |
| `event_name` (string, new in 1.5)               | `@mt`                         | If non-empty, preserved as message template name.                                         |
| `dropped_attributes_count`                      | `Properties["otlp_dropped"]`  | Skip if 0; debugging signal otherwise.                                                    |
| `flags` (fixed32)                               | `Properties["otlp_flags"]`    | W3C trace flags; keep only if non-zero.                                                   |

**Workspace routing:** same as Seq-compat. `X-Seq-ApiKey` header resolves via `CompositeApiKeyStore` → target workspace. `service.name` from resource attributes lands in `Properties` for filtering, NOT for routing (keeps the multi-tenant model explicit, resists attribute-injection workspace hops).

**Effort:** 3-5 d. `OpenTelemetry.Proto` NuGet (first-party from OTel, v1.5.0) gives compiled Protobuf types at parser boundary. New `IOtlpLogParser` + endpoint handler + shared `IIngestionPipeline.IngestAsync` + 5-10 compat tests with real OTel exporter from a .NET / Python client (mirrors `WinstonSeqCompatTests` pattern — external process emits, we assert store state).

### Direction 2 — Self-emission (yobalog as OTel client): Phase G recommendation

**Recommendation: Phase G, after F lands.** Self-emission writes into `$system` workspace via a custom exporter — cleaner to build on a codebase that already understands OTLP-shaped data.

**Packages** (as of April 2026, OTel .NET 1.15.x line):
- `OpenTelemetry` 1.15.x — core.
- `OpenTelemetry.Extensions.Hosting` 1.15.x — `AddOpenTelemetry()` DI integration.
- `OpenTelemetry.Instrumentation.AspNetCore` 1.15.1 — auto-trace incoming HTTP (built-in .NET 8+ metrics).
- `OpenTelemetry.Instrumentation.Http` 1.15.x — auto-trace outgoing HttpClient (costs nothing, useful for future).
- **No `OpenTelemetry.Instrumentation.Sqlite`** exists (neither official nor community). Trace SQLite writes via a manual `ActivitySource` at `SqliteLogStore.AppendBatchAsync` boundary.

**Destination: custom exporter → `$system`.** `BaseExporter<Activity>` that maps each completed Activity → `LogEventCandidate` with `Properties.Kind = "span"`, flattening `parent_id / name / duration / start_unix_ns / status_code` into Properties. Writes via `ILogStore.AppendBatchAsync` directly to `$system` — same pattern as `SystemLoggerProvider`, bypasses `IIngestionPipeline` to avoid recursion on the pipeline's own Activities.

**Hot-path overhead budget** (grounded in BDN numbers from `perf-baseline.md`):
- `ActivitySource.StartActivity()` with no listener: ~10 ns → sprinkle freely.
- With listener, new Activity: 0.5-2 μs → fine at batch granularity.
- **Do NOT emit a span per event inside `ChannelIngestionPipeline.WriteLoop`.** At 100k events/sec (current SqliteLogStore throughput), per-event overhead would be 50-200 ms/sec = 5-20 % CPU on tracing alone. Instrument batch boundaries only.
- Safe targets: ingestion batch (~1 span / 1k events), KQL query (1 span / request — currently 300 μs-5 ms, overhead <1 %), SQLite BulkCopy (1 span / batch), retention sweep per workspace.
- ASP.NET Core auto-instrumentation covers HTTP endpoints by default; explicitly skip `/health` / `/version` to avoid span churn under load-balancer pings.

**Effort:** 1-2 d. Packages + `AddOpenTelemetry()` in `YobaLogApp` + named `ActivitySource`s at batch points (`YobaLog.Ingestion`, `YobaLog.Query`, `YobaLog.Retention`, `YobaLog.Storage.Sqlite`) + custom exporter + BDN regression test (baseline vs +OTel).

### Direction 3 — Trace ingestion + UI: Phase H recommendation

**Recommendation: Phase H, defer indefinitely unless F gets traction.** Only build trace support after ingest-logs proves valuable — if nobody's sending logs over OTLP, nobody's sending spans either.

**Storage option analysis:**

| Option                                            | Pro                                                                                                                  | Con                                                                                                                                                                | Verdict       |
|---------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------|
| (a) Rich-log: `Events.Kind='span'` + everything in Properties | Reuse Events table, single KQL target.                                                                              | `Duration` / `ParentSpanId` / `Kind` / `Status` live in Properties JSON → awkward KQL (`where json_extract(PropertiesJson,'$.Duration') > 100`). Schema lies: Events is optimized for log text; spans have structure. | **Reject**    |
| (b) Separate `spans` table per workspace          | Schema matches reality: `(SpanId, TraceId, ParentSpanId, Name, Kind, StartUnixNs, EndUnixNs, StatusCode, AttributesJson, EventsJson, LinksJson)`. Indexed `(TraceId, StartUnixNs)` for waterfall lookups. KQL transformer adds a `spans`-target branch alongside `events`. | Duplicated migration path; two tables per workspace file.                                                                                                         | **Recommend** |
| (c) Skip traces entirely                          | Zero scope.                                                                                                          | Direction 1 + 2 already cover half the OTel surface; skipping traces means trace-waterfall UI never ships.                                                        | **Fallback**  |

**UI: trace waterfall** = Razor partial `_TraceWaterfall.cshtml`, takes `TraceId`, fetches spans ordered by `start_unix_ns`, renders `<div>` bars with width ∝ duration, indent = depth-in-parent-tree. Hover-tooltip on span for attributes / events / status. ~200 lines Razor + ~100 lines TS (event-delegated hover). **No D3 / vis.js** — keeps dependency footprint flat, matches our "htmx + DaisyUI, no heavy client framework" posture.

**KQL extensions:** `spans | where Duration > 100ms | order by StartTime`. New columns on the `spans` target: `Duration` (int ms, computed from `EndUnixNs - StartUnixNs`), `ParentSpanId`, `Kind`, `Status`. Transformer: `ApplyEventQuery` generalizes to `ApplyQuery(target ∈ {events, spans})`; dual-executor tests extended with 10-15 spans-cases. No new KQL operators.

**Service map / aggregate views — explicit defer.** Dependency graph over spans requires `GROUP BY service.name + resource.attributes`-style rollups; huge UX surface, minimal value for self-hosted single-service observability. Document as "out of MVP".

**Effort:** 7-10 d. Spans schema + migration + separate `linq2db` table mapping + OTLP traces Protobuf parser + KQL transformer `spans`-target branch + waterfall Razor partial + span-details panel.

### Rejected alternatives

- **"Jaeger-only for traces"** — fragments observability across two stores and two UIs. Defeats the point of yobalog being self-hosted + single-pane-of-glass.
- **"Seq-native `Seq.OpenTelemetry.Exporter` for client-side emit"** — that package is a CLIENT-side exporter sending from an OTel-enabled app INTO Seq's OTLP ingester. Doesn't apply on the receiving end; we'd be building the receiver.
- **"Ingest metrics (OTLP Metrics)"** — yobalog is a log/trace store. Metrics are Prometheus/Grafana territory, completely different storage shape (counters / gauges / histograms → time-series), completely different query surface. Explicit-no; documented as hard-scope in the spec §1 proposal. Seq's upcoming 2026.1 OTLP-metrics support is NOT a signal to follow — they're also bolting on a separate metrics UX.
- **"All three directions at once"** — scope creep, no feedback loop. F alone delivers measurable value (Seq-compat + OTLP-compat = largest protocol surface among self-hosted log stores).

### Unresolved questions for review

1. **Protocol-buffer source:** prefer `OpenTelemetry.Proto` NuGet (first-party, tracks spec) over compiling `.proto` from source via `Grpc.Tools`. Generated types are `public` + non-sealed + mutable → formally escapes our "max static typing, no mutable escape hatches" invariant (AGENTS.md). Scoped to parser boundary only — domain `LogEventCandidate` stays immutable. Acceptable?
2. **Path alias `/v1/logs`:** expose OTel-standard path alongside Seq-prefixed `/ingest/otlp/v1/logs`? Trivial `MapPost`, helps OTel clients that hard-code the standard. Opens door for endpoint sprawl.
3. **gRPC support scope:** defer entirely to Phase F+1 (recommended), or ship in F because the `OpenTelemetry.Proto` NuGet already brings Protobuf and Grpc.AspNetCore is a one-line addition? Cost: HTTP/2 at reverse proxy, ~100 extra lines of gRPC service, one extra endpoint binding in Kestrel.
4. **Activity emission in unit tests:** OTel SDK listener is set up at DI time in `YobaLogApp.ConfigureServices`. Tests should NOT emit spans (no consumer, waste). Gate `AddOpenTelemetry().WithTracing(...)` on `!IsEnvironment("Testing")` — same pattern as `UseHttpsRedirection`.

### Recommendation

**Phase F (OTLP-logs ingest) first, by a wide margin.** Smallest surface, biggest real-world payoff — any OTel-enabled .NET / Go / Python / JS app becomes a yobalog writer. Direct extension of the existing ingestion pipeline: same `ILogStore`, same `CompositeApiKeyStore`, same workspace routing, ~80 % shared test infrastructure with the Serilog / Winston-compat suites.

Phase G (self-emission) gated on F landing: it writes INTO a workspace that just learned OTLP-shaped data, so we consume our own dog food.

Phase H (traces + waterfall UI) gated on G proving useful: if telemetry emission stays quiet, spans-storage stays hypothetical. Can live as a deferred plan bullet indefinitely.

**Non-decision to record:** we are explicitly NOT shipping OTLP-metrics ingestion, ever. yobalog = logs + (optionally, later) traces. Metrics elsewhere.

## 2026-04-21 — Build pipeline: Cake + GitVersion; Docker = chiseled + smoke-test; deploy = manual `deploy` tag

**Решение:** сборочный pipeline синхронизирован с yobaconf (commit `9f99b84`) — теперь один шаблон на оба репо, расширяться будут вместе:
- **GitVersion** (`GitVersion.yml`, `next-version: 0.3.0`) — ContinuousDelivery mode, конфиг из yobapub. Версия прокидывается в MSBuild `Version` / `InformationalVersion` + в Docker build-args (`APP_VERSION`, `GIT_SHORT_SHA`, `GIT_COMMIT_DATE`). `0.3.0` отражает прогресс: Phase A.0 / A / B / C / D закрыты, активно B (Phase E live-tail polish) + рюшечки.
- **Cake** (`build.cake` + `build.sh` + `build.ps1` + `.config/dotnet-tools.json`) — orchestration в C# DSL. Tasks: Clean → Restore → Version → Build → Test → Docker → DockerSmoke → DockerPush. Дополнительный task `E2ETest` для Playwright-тестов — отдельный от `Test` чтобы не качать Chromium (~200MB) на каждый build main. Локально `./build.sh --target=Test` = unit, `--target=E2ETest` = browser, `--target=Docker` = образ.
- **Docker runtime** — `mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-noble-chiseled`. ~15MB base, нет shell. Self-contained publish `linux-x64`. Dockerfile двухстадийный: SDK + bun installer → chiseled runtime. yobalog'овский `data/` volume (`VOLUME ["/app/data"]`) для per-workspace `.logs.db` + `.meta.db`. Риск "chiseled упал на runtime, не попасть внутрь" закрывается **DockerSmoke** task: `docker run -d` + `curl /Login` (не `/` как у yobaconf — у нас корень под auth). 30s timeout.
- **Deploy** — **только по ручному тегу `deploy`** (`git tag deploy && git push origin deploy`). Main-push: build + test + e2e + Docker build + push в ghcr.io, но **без** SSH-деплоя. Тег `deploy` = явный act of will. SSH-job на VPS монтирует `/opt/yobalog/data:/app/data`, задаёт `Admin__Username`/`Admin__Password` из secrets (config-admin fallback пока DB-users пусты).

**Причина:**
- **Синхронизация с yobaconf:** оба репо — .NET 10 Razor Pages, одинаковые build-steps, одинаковый chiseled-runtime. Расхождение в pipeline = double maintenance при любом изменении build-tool'а. Решение: зеркалить, отмечать в decision-log каждую sync-точку.
- **E2ETest отдельный task:** `test` job (unit) ~30с на CI; `e2e` job (Playwright) ~3-5 мин с установкой Chromium. Параллельное выполнение — `publish` ждёт обоих, зато main-build разблокируется за время быстрейшего. Trace-zip'ы аплоадятся на failure → дебаг флейков без локального повтора.
- **DockerSmoke на `/Login`, не `/`:** yobalog требует аутентификации на корне (fallback policy). `/Login` — единственный `[AllowAnonymous]` маршрут, который гарантированно отдаёт 200 без токена. Альтернатива "смотреть на 302" хрупкая — `curl -f` будет fail'иться на редиректах, `-L` следует за ними → снова 302 → infinite.

**Отклонения от yobaconf pipeline:**
- Dockerfile объявляет `VOLUME ["/app/data"]` — yobaconf хранит master key в env, мы пишем `.db` файлы.
- Cake `E2ETest` task — у yobaconf пока нет E2E-тестов.
- DockerSmoke endpoint = `/Login` (anonymous), у yobaconf — `/`.
- Deploy secrets включают `YOBALOG_ADMIN_USERNAME`/`PASSWORD` для config-admin bootstrap; yobaconf использует master key.

**Откатили:**
- "Объединить Test + E2ETest в один Cake task" — каждый main-push тянул бы Chromium на 5 минут.
- "DockerSmoke на `/`" — корень за auth, fall-back redirects к /Login, curl путается.
- "Gate publish только на test" — e2e-regressions доехали бы до ghcr без проверки браузерного слоя.

## 2026-04-21 — UI-тесты: `Microsoft.Playwright` + `data-testid` обязателен, text/role-name/CSS-селекторы запрещены

## 2026-04-21 — UI-тесты: `Microsoft.Playwright` + `data-testid` обязателен, text/role-name/CSS-селекторы запрещены

**Решение:** Playwright MCP остаётся для интерактивной smoke-проверки при разработке; CI-регрессии — отдельный проект `tests/YobaLog.UiTests/` на `Microsoft.Playwright` (.NET SDK), chromium headless. Элементы, которые трогают тесты, обязаны иметь `data-testid="<kebab-slug>"` в Razor-разметке. Локаторы в тестах — только `page.GetByTestId(...)`; `GetByText`, `GetByRole(Name=...)`, `GetByPlaceholder` и CSS-класс-селекторы (`.btn-primary`, `.alert-error`) запрещены на UI chrome. `HasText=...` разрешён только внутри testid-scoped локатора и только для проверки data-контента (event message, saved query name).

**Причина:**
- Все user-facing строки — цели локализации (spec §9). Тест, матчащийся на "Apply" / "Keys" / "no events", сломается на первом же переводе или ребрендинге. `GetByRole(Name=...)` ровно так же читает localized accessible name — псевдо-семантика, не решение.
- CSS-классы DaisyUI (`.btn-primary`, `.alert-error`) — стилевая деталь, subject to refactor. Переход с DaisyUI на Flowbite или просто смена palette — и `.alert-error` переименовывается в `.alert-danger`, все селекторы отваливаются.
- `data-testid` — явный контракт "эта штука — точка тестирования". Devs видят атрибут в разметке → думают дважды перед удалением. Переводчики и designer'ы к нему не прикасаются.
- Selenium / Atata исключены отдельно: WebDriver-протокол медленнее CDP, флакier; Atata добавляет фреймворк поверх и без того проблемного стека.

**Откатили:** гипотетический вариант "матчить по тексту + переопределять culture на `en-US` в test-setup, чтобы тесты читали английский независимо от локали" — работает только до появления первого переформулированного label'а (переводы добавляют расхождения и в пределах одного языка).

## 2026-04-19 — Ingestion namespace split: `/api/v1/ingest/<fmt>` (native) + `/compat/<tech>/…` (vendor compat)

**Решение:** два независимых URL-корня под ingestion.
- **Нативный канонический** — `POST /api/v1/ingest/<fmt>` (версия в пути, формат в последнем сегменте). Сегодня: `/api/v1/ingest/clef`. Будущее: `/api/v1/ingest/gelf`, `/api/v1/ingest/otlp`, и т.п.
- **Вендор-совместимый** — `POST /compat/<tech>/…`. Сегодня: `/compat/seq/api/events/raw`. Будущее: `/compat/hec/...` (Splunk HEC), `/compat/statsd/...` и т.п. Внутренний path внутри `/compat/<tech>/` диктуется форматом клиентского URL-конструктора: Seq-клиенты (Serilog.Sinks.Seq, seq-logging, seqlog) жёстко конкатят `/api/events/raw` к base URL, поэтому наш handler сидит по этому хвосту и пользователь прописывает в Serilog-конфиге `serverUrl: https://<host>/compat/seq`.

**Причина:**
- Нативный surface заслуживает версии в пути — когда пайплайн/ingestion/shape нативно поменяется, выкатим `/api/v2/ingest/clef` без поломки `v1`. Seq-совместимость версионировать бесполезно — контракт не наш.
- Смешивать Seq-specific path с будущими HEC/statsd слотами нельзя: каждый vendor диктует свой URL-shape (HEC например требует `/services/collector/event`), общего суффикса нет. Дать каждому свой `/compat/<tech>/` namespace — один слот, одна ответственность, нет conflict'а на корне.
- Auth (`X-Seq-ApiKey` / `?apiKey=`) и pipeline-dispatch общие — шарятся через `IngestionHandlers.ResolveScopeAsync` + `IIngestionPipeline.IngestAsync`. Добавление нового формата = один `MapPost` + формат-специфичный парсер; middleware и DI не трогаются.
- Корень `/api/events/raw` освобождён под будущие нужды, не захватывается legacy-именем в Seq-стиле.

**Откатили:** идею "`/api/events/raw` в корне + всё compat подсасывать туда же" (захват корня легаси-названием), идею "`/seq-compat/*` как generic compat-namespace" (Seq-specific имя для не-Seq будущих форматов).

## 2026-04-19 — Рендер timestamps в TZ смотрящего (закрывает open question §98 spec)

**Решение:** event-row рендерит `<time class="local-time" datetime="<ISO-UTC>Z">UTC-fallback</time>`, tiny TS-pass на `DOMContentLoaded` + `htmx:afterSwap` переписывает `textContent` в локальный `YYYY-MM-DD HH:mm:ss.SSS`. UTC остаётся в `title=` для прозрачности. Без JS — видна UTC-строка. Формат статический (не culture-aware) до появления i18n-каркаса.
**Причина:** Seq-конвенция хранит UTC, `@t` тайм-зона клиента не компенсируется. Оператор в Москве смотрит логи из сервиса в UTC — мгновенный mental-convert ("+3") съедает внимание быстрее, чем рендер в локаль. Выбор "TZ смотрящего" закрывает open question из `plan.md` §98 (было "TZ смотрящего или TZ события?").
**Откатили:** статический UTC в заголовке колонки + в ячейках — заголовок теперь просто "Time".

## 2026-04-19 — UX-полировка клавиатуры и кнопок: `/` фокус, Ctrl+Enter submit, hotkey-toast, yellow-flash на .btn

**Решение:** пакет мелких подтверждений действий:
- `/` (GitHub-style) — фокус в KQL-textarea из любого места, кроме input'ов.
- Ctrl/Cmd+Enter внутри textarea — submit формы, предварительно триггерит flash на кнопке.
- Hotkey-toast в правом нижнем на 1.5с с `<kbd>` + описанием действия (`/` → focus query; Ctrl+Enter → apply). Для Ctrl+Enter toast переносится через `sessionStorage` между страницами — navigation иначе убил бы DOM до анимации.
- Жёлтый flash (`.btn-flash` CSS-animation на daisyUI `--wa/--wac` палитре, 450ms) на всех `.btn` в приложении (Apply/Reset/Save/Share/copy/модалки). Заменяет тупой scale-down daisyUI-`:active`, который воспринимался как "шрифт чуть дёрнулся".
**Причина:** live-dogfood показал, что без явного feedback'а пользователь не уверен, что шорткат сработал или кнопка нажалась (особенно Ctrl+Enter — сабмит навигации съедает любой тонкий отклик). Toast + flash — минимально навязчивое, неаффектит layout.
**Откатили:** ничего — раньше был только дефолтный daisyUI-`:active`, теперь поверх.

## 2026-04-19 — FTS5 MATCH в IN-subquery медленнее LIKE на частых словах: известная особенность SQLite
**Решение:** текущую реализацию `has` через `{rowid} IN (SELECT rowid FROM EventsFts WHERE Message MATCH ?)` оставляем в MVP. В `perf-baseline.md` задокументировано: на частом слове + `take 50` она в 100x медленнее `contains` через LIKE. Это — **не баг нашей интеграции**, а стандартный failure-mode SQLite FTS5 query-planner'а при LIMIT поверх IN-подзапроса. Оптимизация отложена до Phase D/E (когда появятся реальные use-case'ы).
**Причина:** SQLite community подтверждает (см. ссылки ниже):
- IN-подзапрос с FTS MATCH материализует **полный rowid-set** до применения внешнего LIMIT. На селективных запросах (редкое слово — 10 rowids) это быстро, на частых (90k rowids) — медленно.
- JOIN-форма (`SELECT * FROM EventsFts JOIN Events ON Events.Id = EventsFts.rowid WHERE MATCH ? LIMIT N`) в теории позволяет LIMIT протолкнуться через FTS-итератор, но query-planner часто выбирает `torrents → fts` вместо `fts → torrents` и сам становится в 380x медленнее (см. sqlite.org/forum). Поведение нестабильно, зависит от `ANALYZE` и версии SQLite.
- Первая рекомендация community — запустить `ANALYZE` на таблице; иногда единственный нужный фикс.
- Для "правильной" early-termination через FTS нужен rewrite на raw SQL (не через linq2db `[Sql.Expression]`): FTS-table как driving, явный LIMIT. Это ломает compose с другими `where` предикатами (нужен полный SQL-builder или отдельная `QueryFtsAsync` ветка на `SqliteLogStore`).

Пока — задокументировано в `perf-baseline.md` как surprise, и UI-hint пользователю "used `has` → fast on rare terms, slow on frequent" можно дать при появлении editor-warnings. Fix-варианты:
1. ~~`ANALYZE` на boot / после крупного ingest~~ — **проверено: не помогает**. `ANALYZE` на seeded 100k-фикстуре дал QueryFtsHas 8 056 μs vs 8 257 μs без — в пределах noise. Это не проблема устаревшей статистики, а структурная — `IN (SELECT rowid FROM fts WHERE MATCH)` материализует rowid-set до внешнего LIMIT, query-plan тут детерминирован и не зависит от stats.
2. Raw-SQL ветка `SqliteLogStore.QueryFtsMatchAsync(message, take)` для специфического `has + take` паттерна.
3. Word-boundary LIKE fallback при `has + take` (в transformer'е детектить take и эмитить `LIKE '% x %' OR LIKE 'x %' OR LIKE '% x'` вместо FTS subquery). Сохраняет word-boundary семантику + даёт early-exit. Но разрастается matrix sql-комбинаций.
4. Принять разницу и рекомендовать `contains` для частых слов.

**Источники:**
- [SQLite User Forum: Bad query plans from FTS5](https://sqlite.org/forum/info/e0e30e9eb1998e3c9305aea26957bec804615283969d11c1f9326a6b787526eb)
- [SQLite User Forum: JOINs with FTS5 virtual tables are very slow](https://sqlite.org/forum/info/509bdbe534f58f20)
- [phiresky/sql.js-httpvfs#10 — ORDER BY rank FTS5 performance](https://github.com/phiresky/sql.js-httpvfs/issues/10)

**Откатили:** предположение "FTS5 всегда быстрее LIKE, потому что индекс". Реальность: FTS5 выигрывает только на селективных запросах или когда LIMIT виден самой FTS-итерации. Для `| has 'common-word' | take N` при частом слове table-scan с early-exit через `contains` быстрее.

## 2026-04-19 — Benchmark-проект: подавление CA1001, CA1034, CA1050, CA1515, CA1707, CA1812, CA1822, CA1848, CA2007
**Решение:** `<NoWarn>$(NoWarn);CA1707;CA1848;CA1822;CA1034;CA1050;CA1515;CA2007;CA1812;CA1001</NoWarn>` в `benchmarks/YobaLog.Benchmarks/YobaLog.Benchmarks.csproj`. Production-код (src/**) и тесты (tests/**) по-прежнему escalate all analyzers to error — суппрессия только внутри bench-проекта.
**Причина:**
- **CA1001** — "type owns disposable field but isn't IDisposable". В BDN disposable-поля (`SqliteLogStore`, `ChannelIngestionPipeline`) чистятся в `[GlobalCleanup]`/`[IterationCleanup]`, не через `IDisposable`; BDN lifecycle-hook'ы — явная альтернатива и это стандартный паттерн для benchmark-классов.
- **CA1034** — запрет nested public types. BDN inline-generated helpers (e.g. `[Params]`-enums) иногда удобнее как nested.
- **CA1050** — все типы в namespace. `Program.cs` у BDN — top-level statements без namespace, это стандартная форма BDN-runner'а.
- **CA1515** — sealed-by-default. BDN открывает benchmark-классы через reflection (inherit для runner'а), sealed ломает runtime discovery.
- **CA1707** — underscore naming. BDN benchmarks часто используют `Method_Variant` для читаемости колонки "Method" в таблице.
- **CA1812** — "never instantiated class". Bench-классы создаются BDN-runner'ом через reflection, анализатор их не видит.
- **CA1822** — "can be static". BDN требует instance-метод для `[Benchmark]`.
- **CA1848** — LoggerMessage.Define. Bench-код не логирует; если логирует — выражается через benchmarks, которые не нужны source-gen.
- **CA2007** — `ConfigureAwait(false)`. BDN-код не использует `SynchronizationContext`, warning шумовой (как в тестах).
**Откатили:** идею подчинять bench-проект тем же правилам, что и production. Bench-код специфичен BDN и чаще спорит с "универсальными" правилами чистоты, чем с production-кодом.

## 2026-04-19 — KqlResult как loose-typed shape-container; shape-changing ops in-memory после SQL-prefix
**Решение:** для shape-changing KQL-операторов (`project`, `extend`, `count`, `summarize count() by …`) ввёден `KqlResult { IReadOnlyList<KqlColumn> Columns, IAsyncEnumerable<object?[]> Rows }` — loose-typed таблица с явными метаданными колонок. `KqlTransformer.Execute` разбивает pipeline на два куска: shape-preserving prefix (`where` / `take` / `order by` — push down в SQL через `Apply` + linq2db) и shape-changing suffix (поверх материализованного потока, в C#). Старый контракт `Apply(...) → IQueryable<EventRecord>` оставлен нетронутым для event-shape путей (viewer, retention).
**Причина:** сильно-типизированный `IQueryable<EventRecord>` не выражает результат `| project Id, Message` (анонимная 2-колонка) или `| summarize count() by Level` (int + long), а generic `Apply<T>` требовал бы runtime codegen класса под каждый запрос. Альтернатива — держать шейп статически типизированным через per-query anonymous types через linq2db — вытягивает в LINQ-to-Objects сложные выражения и ломает SQL-pushdown; вынудила бы ручной SqlBuilder с нуля. `KqlResult` — стандартный подход в DB-мире (drivers, ODBC, ADO.NET): явная schema + row-of-objects, с конкретным Type per column для сравнения значений. Потеря compile-time типов компенсирована eager validation (unknown column / unsupported expression / unknown aggregate → `UnsupportedKqlException` с actionable message на стадии `Execute`). Shape-suffix в C# (Dictionary-grouping, массивы) — приемлемо для PageSize=50 viewer'а и saved-query результатов; оптимизация в SQL GROUP BY / anonymous type projections — будущая работа, не меняет контракт.
**Откатили:** идею "generic Apply<T> с кодогенерацией" (слишком сложно); идею "один IQueryable на всё, анонимные типы через linq2db" (невозможно выразить динамический shape); идею "ни shape-changing ops в MVP" (спец §3 прямо требует `summarize count() by domain` как замену domain-specific таблицам).

## 2026-04-19 — KQL: `Level` как int rank + `LevelName` как string
**Решение:** в KQL-схеме `Level` — int от 0 (Verbose) до 5 (Fatal), поддерживает все сравнения (`==`, `!=`, `<`, `<=`, `>`, `>=`). Рядом `LevelName` — string-колонка с именем уровня, поддерживает только равенства (`==`, `!=`). Пример: `where Level >= 3` (Warning и выше), `where LevelName == 'Error'`.
**Причина:** dual-executor должен согласовываться с reference-engine (kusto-loco). Если Level строка — lexicographic ordering не совпадает с ожидаемым numeric (`'Warning' < 'Error'` по строке, но `Warning` < `Error` по rank). Делать pre-translation KQL для reference — дорого и хрупко. Два канонических канала (int rank для ordering, string name для ergonomic equality) решают без магии: оба executor'а видят одни и те же данные, KQL пишется на одной стороне. Правило спеки §1 "Level — индексируемый" сохраняется; хранение по-прежнему int в `EventRecord.Level`.
**Откатили:** идею "только string-equality на Level" (ограничивало viewer, спрятано от пользователей); идею "транслировать `Level >= 'Warning'` в int автоматически" (требовало rewrite KQL для reference executor'а, хрупко).

## 2026-04-19 — Тестовый проект: подавление CA1707, CA1848, CA1861, CA1873, CA2007
**Решение:** `<NoWarn>$(NoWarn);CA1707;CA1848;CA1861;CA1873;CA2007</NoWarn>` в `tests/YobaLog.Tests/YobaLog.Tests.csproj`. В production-коде все эти правила остаются активными (escalate to error).
**Причина:**
- **CA1707** — запрет underscore в именах. В тестах стандартная конвенция `Method_Condition_Expectation` вынуждает.
- **CA1848** — требует `LoggerMessage.Define` / source-gen вместо `logger.LogInformation(...)`. В тестах мы пишем short-lived код ради assertion'ов, source-gen boilerplate нечитаем; perf тут не важен.
- **CA1861** — `InlineData` с array-литералом "expensive". В xunit Theory нормально — выполняется один раз на тест.
- **CA1873** — "evaluation may be expensive if logging disabled". Тесты всегда logging-enabled, предупреждение ложное.
- **CA2007** — `ConfigureAwait(false)` во всех await. В xunit-тестах нет SynchronizationContext, предупреждение шумовое.
**Откатили:** идею подчинять тесты тем же правилам, что и production, ради "чистоты" — шум превышает пользу.

## 2026-04-19 — `.editorconfig`: `indent_size = 2` вместо `indent_size = tab`
**Решение:** финальная конфигурация `.editorconfig` — `indent_style = tab`, `tab_width = 2`, `indent_size = 2` (число, не `tab`).
**Причина:** `indent_size = tab` по спеке editorconfig значит "одна ступень = tab_width пробелов, пишется табом" → 1 уровень = 1 таб. `dotnet format` интерпретирует иначе: `indent_size` — это ширина ступени в символах отступа, а `indent_style = tab` → пиши это число символов табами 1-к-1. Получалось 2 таба на уровень (4 пробела отображения) вместо 1. С `indent_size = 2` + `tab_width = 2` format корректно даёт 1 таб на уровень (2 пробела). Biome тоже перестаёт падать на парсинге (он не понимает `indent_size = tab`).
**Откатили:** `indent_size = tab` — формально правильно по спеке, но нерабочая интеграция с dotnet format.

## 2026-04-19 — Biome вместо ESLint + Prettier для TS
**Решение:** линтер и форматтер для TypeScript — Biome (единый конфиг `biome.json`). ESLint+Prettier не ставим.
**Причина:** Biome — один нативный бинарник (как bun), делает и линт, и формат; в десятки раз быстрее связки ESLint+Prettier; один конфиг вместо двух, нет конфликтов правил форматирования между ними. Ложится в выбранную философию "один инструмент на задачу" (bun вместо npm+node+tsc+bundler). Покрытие TS-правил у Biome для нашего размера фронта (htmx + точечные TS-модули) более чем достаточно.
**Откатили:** цепочку ESLint + typescript-eslint + Prettier + eslint-config-prettier.

## 2026-04-19 — Bootstrap-фаза (A.0) перед кодом приложения
**Решение:** добавлена Фаза A.0 — репо-гигиена и тулинг до первой строчки application-кода. Содержит `.gitignore`/`.gitattributes`/`global.json`/`Directory.Build.props` (с `<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `<AnalysisLevel>latest-recommended</AnalysisLevel>`) / `Directory.Packages.props` / solution skeleton / `biome.json` / `tsconfig.json` со `strict` + `noUncheckedIndexedAccess` + `exactOptionalPropertyTypes` / MSBuild target для bun / CI workflow.
**Причина:** ретрофитить `Nullable enable` и warnings-as-errors после написания кода = сотни шумных правок в одном PR; добавлять `strict: true` в `tsconfig` постфактум = каскад исправлений. Greenfield — единственный момент, когда эти настройки включаются бесплатно. Полдня бутстрапа экономят дни чистки.
**Откатили:** подход "начнём с `dotnet new`, тулинг подкрутим по ходу".

## 2026-04-19 — Стиль кода: иммутабельность + функциональный подход; максимальная типизация; табы шириной 2
**Решение:** по умолчанию immutability и функциональный стиль и на бэке, и на фронте (C#: `record`, `init`-only, `IReadOnlyList`/`ImmutableArray` в API, switch expressions; TS: `const`, `readonly`, spread/map/filter). Мутация допустима только в нагруженных местах (ingestion hot path, writer loop) и не должна утекать через API. Максимальная статическая типизация — запрещены `object`/`dynamic` в C# и `any`/`unknown` в публичных API на TS (`unknown` — только на границе парсинга входа, с немедленным narrowing). `JsonElement`/`JsonNode` допустим только на границе хранилища для `Properties` и тут же разбирается в типы. Предпочитаем branded types для ID (`WorkspaceId`, `TraceId`) вместо голых `string`. Форматирование: табы, отображение 2 пробела. Зафиксировано в `.editorconfig`.
**Причина:** KQL-трансформер, query pipeline, CLEF-парсер и saved queries — все естественно ложатся на неизменяемые данные и чистые функции; упрощает dual-executor тесты и rules out целый класс багов с shared state. Строгая типизация ловит ошибки трансляции AST→SQL на компиляции, а не в рантайме; `any`/`object` быстро превращают строго типизированный query pipeline в `Dictionary<string, object>`-суп. Таб-с-шириной-2: компактный отступ без конфликтов между редакторами (каждый рендерит под себя).
**Откатили:** дефолтные VS-настройки (4 пробела), mutable-by-default стиль, либеральное использование `object`/`dynamic`/`any` для "гибкости".

## 2026-04-19 — KQL через `Kusto.Language` + `kusto-loco`, а не собственный парсер
**Решение:** query-язык — KQL, парсер — `Microsoft.Azure.Kusto.Language` (NuGet от MS), in-memory reference executor — `kusto-loco` (`KustoQueryContext`). Свой AST и Pratt-парсер не пишем.
**Причина:** готовая batteries-included экосистема — парсер, completions (`KustoCode.GetCompletions`), AST, in-memory исполнитель. В .NET аналога под Seq-диалект нет. Downgrade path безопасен: если `kusto-loco` заглохнет, переходим на голый `Kusto.Language` + свой executor, transformer'ы не меняются.
**Откатили:** идею собственного Pratt-парсера и собственного AST под Seq-подобный синтаксис.

## 2026-04-19 — Workspace ID = slug, не guid-base62
**Решение:** workspace ID — пользовательский slug, regex `^[a-z0-9][a-z0-9-]{1,39}$` (стиль Docker image names). Префикс `$` зарезервирован для системных (`$system`).
**Причина:** читаемость URL и shared links; аудит/операционка ("что за `acme-prod`") очевиднее, чем guid. Коллизии решаются пользователем, не системой.
**Откатили:** guid-base62 как исходный вариант ID.

## 2026-04-19 — `bun` вместо `npm+node` для фронт-сборки
**Решение:** TypeScript + Tailwind собираются через `bun` (встроенный bundler, TS из коробки, нативный Windows-бинарник). `package.json` рядом с `.csproj`.
**Причина:** один бинарник вместо `node+npm+tsc+bundler`; нативный Windows; встроенный TS без отдельного шага транспиляции.
**Откатили:** `npm + node + esbuild/webpack` как дефолтную цепочку.

## 2026-04-19 — `<textarea>` вместо Monaco для редактора KQL
**Решение:** редактор запросов — обычный `<textarea>`. Автокомплит — server-side: debounced `hx-post /api/kql/completions?q=...&pos=...` → `KustoCode.GetCompletions(position)` → HTML-дропдаун через htmx OOB-swap.
**Причина:** KQL-запросы короткие; Monaco — ~5 MB + web workers — overkill. Вся intelligence на бэке (один источник правды, тот же парсер что для исполнения), на фронте — 0 логики.
**Откатили:** Monaco editor + клиентский парсер KQL.

## 2026-04-19 — Dual-executor с `KustoQueryContext` как oracle
**Решение:** property-тесты query engine прогоняют одну KQL-строку через `kusto-loco`'s `KustoQueryContext` (in-memory, reference) и через production executor (Kusto AST → SQL через `linq2db`); результаты обязаны совпадать.
**Причина:** oracle от MS + NeilMacMullen — доверие выше, чем к своей реализации. Добавление нового бэкенда требует только реализации трансформера — тесты уже готовы. Без reference executor'а каждый бэкенд тестируется отдельно, одинаковые баги пропускаются.
**Откатили:** раздельные тесты на каждый бэкенд.

## 2026-04-19 — Запрет прямой зависимости YobaLog → YobaConf
**Решение:** конфигурация YobaLog — только `appsettings.json`. YobaConf как источник запрещён.
**Причина:** YobaConf будет писать свои логи в YobaLog — прямая зависимость создаст цикл в bootstrap и в рантайме. Инфраструктурные сервисы не зависят друг от друга.
**Откатили:** предполагалось тянуть конфиг из YobaConf как для остальных сервисов.

## 2026-04-19 — `$system` workspace вместо отдельного файла для self-observability
**Решение:** собственные события сервиса пишутся через обычный ingestion-путь в зарезервированный workspace `$system` (тот же формат хранения, тот же query engine, та же UI).
**Причина:** не множим хранилища и UI — инструменты уже есть. Защита от рекурсии — фильтр по category в логгер-провайдере (внутренние `YobaLog.*` категории не роутятся в user workspaces).
**Откатили:** отдельный файл/лог-формат для internal events.

## 2026-04-19 — Пропуск LiteDB, старт сразу с SQLite+FTS5
**Решение:** первый backend — SQLite + FTS5 через `linq2db`. LiteDB пропускается.
**Причина:** LiteDB и SQLite оба row-storage — промежуточный шаг без архитектурного выигрыша. FTS5 закрывает главный UX-gap (inverted index на Message) бесплатно. `linq2db` даёт общий путь трансляции KQL→SQL, который переиспользуется для DuckDB.
**Откатили:** LiteDB как самый простой первый backend.
