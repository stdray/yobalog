# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

## 2026-04-21 — Caddy on host as HTTPS terminator; yobalog deploys first

**Решение:** HTTPS для yobalog реализуется через **Caddy**, установленный на shared хост как systemd-сервис. Caddy на `:443` терминирует TLS и реверс-прокси'т на `127.0.0.1:8082`, куда биндится контейнер yobalog. Deploy независим от других сервисов стека — собственный CI (`build.cake --target=DockerPush` + SSH → `docker run -d -p 127.0.0.1:8082:8080`). Никакого docker-compose поверх. Зеркалит решение yobaconf (`D:\my\prj\yobaconf\doc\decision-log.md` запись того же дня, commit `3f06f3e`) — один host, общий Caddy, каждый проект деплоится сам по себе.

**Host-port convention (единая для всех проектов на shared хосте):**
- `127.0.0.1:8080` — yobapub (существующий, до-Caddy эра, остаётся)
- `127.0.0.1:8081` — yobaconf
- `127.0.0.1:8082` — **yobalog (этот репо)**
- Следующие свободные — для новых сервисов

Таблица дублируется в `infra/Caddyfile.fragment` каждого HTTP-serving проекта (комментарий сверху). Локальный source-of-truth для "какой порт у этого сервиса"; центральный `/etc/caddy/Caddyfile` на хосте — глобальная картина.

**Yobalog идёт первым.** Из всех сервисов стека yobalog первый выкатывается под Caddy, поэтому **one-time host bootstrap живёт в этом репо** (`doc/deploy.md`, bullet в `plan.md`): `apt install caddy`, начальный Caddyfile из `infra/Caddyfile.fragment`, firewall на 80/443, `systemctl enable caddy`. Последующие проекты (yobaconf — следующий в очереди) в своих deploy-doc'ах пропускают host-setup и сразу переходят к "добавить fragment в центральный Caddyfile + `caddy reload`".

**SSE-специфика yobalog (это отличие от yobaconf).** Yobalog имеет live-tail через `GET /api/ws/{id}/tail` — Server-Sent Events. Caddy по-умолчанию буферизирует response body у reverse-proxy upstream'ов (batching для throughput). Для SSE это ломает streaming: клиент не получает события, пока Caddy не накопит буфер. Фикс — `flush_interval -1` в блоке `reverse_proxy` (отключает буферизацию, каждый flush из upstream'а идёт клиенту сразу). Обязательно в `infra/Caddyfile.fragment`. В идеале покрывается E2E-тестом (yobalog + Caddy в testcontainers → SSE-connect → проверка incremental-стриминга); если Caddy в test env слишком тяжёл — fallback на "trusted by default, covered manually on first deploy" (bullet в plan).

**Rejected alternatives:**
- **nginx + certbot** (текущий yobapub-паттерн). Ручной server-блок + managed-by-Certbot-секция + cron на renewal. 20-30 строк конфига на сервис vs 3 строки Caddy. yobapub остаётся как есть — live-and-let-live, мигрируется при следующем пересетапе.
- **Traefik.** Docker-label discovery плохо ложится на "каждый проект независимо делает `docker run`" — лейблы рассыпаны по CI-скриптам разных репо. Для static 5-service setup — overkill. Caddy ~15MB vs Traefik ~30MB.
- **In-process `LettuceEncrypt` NuGet.** Cert-renewal требует рестарт процесса (brief downtime каждые 60 дней). Per-service cert state не шарится между апгрейдами контейнера — каждый redeploy терял бы cache и делал fresh ACME challenge.
- **Cloudflare edge-TLS.** Free-tier покрывает, но привязывает к DNS-терминированному Cloudflare-домену — вендор-лок на бесплатный-пока тариф.
- **Caddy в контейнере с `--network host`.** Работает, но добавляет ещё одну docker-единицу в lifecycle. systemd-Caddy — установка один раз, видит `localhost:*` других сервисов без разговоров о docker-networks.

**Откатили:**
- docker-compose для HTTPS-оркестрации — противоречит independent-lifecycle паттерну.

**Открытые вопросы для first-time host bootstrap:**
- Централизация Caddyfile: (1) отдельный infra-репо с concat-скриптом из fragment'ов; (2) hand-edit `/etc/caddy/Caddyfile` на сервере; (3) Ansible/скрипт в одном из репо. Решится в момент реального deploy; до того fragment'ы лежат в каждом проекте как reference.
- Forwarded-headers wiring в ASP.NET (`UseForwardedHeaders` c `KnownProxies = { IPAddress.Loopback }` перед `UseHttpsRedirection`) — bullet в Phase A, закроется при первом реальном деплое.
- Caddy access-log в `/var/log/caddy/yobalog.access.log` (JSON, локальная ротация). После Phase F (OTLP-ingestion) — ingest'ится в yobalog через shipper / прямой OTLP push. Приятная симметрия: yobalog самоингестит свои собственные access-log'и.

---

## 2026-04-21 — OpenTelemetry integration: scope, cost, архитектурные решения

**Статус: архитектурные решения зафиксированы; кода пока нет.** `plan.md` Phase F/G/H — источник истины для sequencing и sub-task'ов; в `spec.md` черновые proposal-комментарии `<!-- OTel proposal -->` остаются в виде комментариев до первого Phase-F коммита, который промоутит их в основной текст.

**Проблема.** Modern .NET-app hygiene 2026 включает OpenTelemetry. yobalog уникально спозиционирован — он САМ log store, поэтому OTel пересекает нас с двух сторон:
- **Ingest side:** OTel-enabled клиенты должны уметь писать в yobalog без адаптерного кода.
- **Emit side:** операции yobalog (ingestion / query / retention) должны эмитить OTel-спаны для self-observability.

Анализировали три направления независимо; каждое стоит на своих ногах, не all-or-nothing.

### Направление 1 — OTLP ingestion (HTTP/Protobuf для logs): Phase F

**Решение: Phase F.** Минимальная единица реальной ценности — любой OTel-enabled .NET / Go / Python / JS app становится yobalog-писателем без изменений в коде.

**Путь: зеркалим Seq-овский surface.** `POST /ingest/otlp/v1/logs` с заголовком `X-Seq-ApiKey`. yobalog IS Seq-compatible; пользователи, которые сегодня направляют OTel-exporter в Seq, должны сменить только URL base и ничего больше. Плюсом — alias `POST /v1/logs` для OTel-клиентов, которые хардкодят стандартный путь (см. resolved question #2).

**Протокол: только HTTP/Protobuf.** HTTP/JSON (~1-2 % реальных деплоев; Seq тоже skip'нул) и gRPC (HTTP/2 + Kestrel config + reverse-proxy-hostile) отложены в Phase F+1 если появится спрос. Совпадает с нашим Dockerfile (HTTP-only на 8080).

**Маппинг OTLP LogRecord → CLEF** (OTel Logs proto v1.5.0):

| OTLP                                            | CLEF                          | Notes                                                                                     |
|-------------------------------------------------|-------------------------------|-------------------------------------------------------------------------------------------|
| `time_unix_nano` (fixed64)                      | `@t`                          | ÷ 1_000_000 → ms. Если 0, fallback к `observed_time_unix_nano`. Если оба 0 — reject.      |
| `severity_number` (SeverityNumber enum 1-24)    | `@l`                          | 1-4→Verbose, 5-8→Debug, 9-12→Information, 13-16→Warning, 17-20→Error, 21-24→Fatal.        |
| `severity_text` (string)                        | `Properties["severity_text"]` | Храним raw; может отличаться от числа (кастомные уровни).                                 |
| `body` (AnyValue)                               | `@m`                          | string_value → как есть. int/double/bool → ToString. array/map → System.Text.Json-сериализация. |
| `attributes` (list\<KeyValue>)                  | `Properties`                  | Flatten в плоский namespace (spec §1).                                                    |
| `resource.attributes`                           | `Properties`                  | Merge с attributes; при конфликте ключа **resource побеждает** (deployment identity).     |
| `trace_id` (bytes[16])                          | `@tr`                         | Hex-encode → 32-char lowercase. All-zero = absent, skip.                                  |
| `span_id` (bytes[8])                            | `@sp`                         | Hex-encode → 16-char lowercase. All-zero = absent, skip.                                  |
| `event_name` (string, новое в 1.5)              | `@mt`                         | Если непусто — хранится как message template name.                                        |
| `dropped_attributes_count`                      | `Properties["otlp_dropped"]`  | Skip если 0; диагностический сигнал иначе.                                                |
| `flags` (fixed32)                               | `Properties["otlp_flags"]`    | W3C trace flags; держим только non-zero.                                                  |

**Workspace routing:** как в Seq-compat. Заголовок `X-Seq-ApiKey` резолвится через `CompositeApiKeyStore` → целевой workspace. `service.name` из resource-attributes падает в `Properties` для фильтрации, НО **не для routing'а** (multi-tenant модель остаётся явной, защищаемся от attribute-injection hops через workspace'ы).

**Effort:** 3-5 d. `OpenTelemetry.Proto` NuGet (first-party от OTel, v1.5.0) даёт скомпилированные Protobuf-типы на границе парсера. Новый `IOtlpLogParser` + endpoint handler + shared `IIngestionPipeline.IngestAsync` + 5-10 compat-тестов с реальным OTel-exporter'ом из .NET / Python клиента (паттерн `WinstonSeqCompatTests` — external process эмитит, мы ассертим state store).

### Направление 2 — Self-emission (yobalog как OTel-клиент): Phase G

**Решение: Phase G, после F.** Self-emission пишет в `$system` workspace через custom exporter — чище строить поверх codebase, который уже понимает OTLP-shape.

**Пакеты** (apr 2026, OTel .NET 1.15.x line):
- `OpenTelemetry` 1.15.x — core.
- `OpenTelemetry.Extensions.Hosting` 1.15.x — `AddOpenTelemetry()` DI integration.
- `OpenTelemetry.Instrumentation.AspNetCore` 1.15.1 — auto-trace incoming HTTP (built-in .NET 8+ metrics).
- `OpenTelemetry.Instrumentation.Http` 1.15.x — auto-trace outgoing HttpClient (стоит ничего, пригодится в будущем).
- **`OpenTelemetry.Instrumentation.Sqlite` не существует** (ни official, ни community). SQLite-writes инструментируются вручную через `ActivitySource` на границе `SqliteLogStore.AppendBatchAsync` (см. Note ниже).

**Destination: custom exporter → `$system`.** `BaseExporter<Activity>` мапит завершённый Activity → `LogEventCandidate { Properties.Kind="span" }`, flatten'ит `parent_id / name / duration / start_unix_ns / status_code` в Properties. Пишет через `ILogStore.AppendBatchAsync` напрямую в `$system` — тот же паттерн, что `SystemLoggerProvider`, минует `IIngestionPipeline` чтобы не рекурсить на собственных pipeline-Activity.

**Бюджет hot-path overhead'а** (из `perf-baseline.md`):
- `ActivitySource.StartActivity()` без listener'а: ~10 ns → можно сыпать свободно.
- С listener'ом, новый Activity: 0.5-2 μs → ок на batch granularity.
- **НЕ эмитим span per event внутри `ChannelIngestionPipeline.WriteLoop`.** На 100k events/sec (текущий throughput SqliteLogStore) per-event overhead составит 50-200 ms/sec = 5-20 % CPU только на трейсинг. Инструментируем только boundary батчей.
- Safe targets: ingestion-батч (~1 span / 1k events), KQL-запрос (1 span / request — сейчас 300 μs - 5 ms, overhead <1 %), SQLite BulkCopy (1 span / batch), retention sweep per workspace.
- ASP.NET Core auto-instrumentation покрывает HTTP-endpoints по дефолту; явно skip'аем `/health` / `/version` чтобы load-balancer pings не раздули span-churn.

**Effort:** 1-2 d. Пакеты + `AddOpenTelemetry()` в `YobaLogApp` + именованные `ActivitySource`-ы на batch-точках (`YobaLog.Ingestion`, `YobaLog.Query`, `YobaLog.Retention`, `YobaLog.Storage.Sqlite`) + custom exporter + BDN regression-тест (baseline vs +OTel).

### Направление 3 — Trace ingestion + UI: Phase H

**Решение: Phase H, отложено indefinite если F не наберёт traction.** Trace-support строим только если ingest-logs докажет свою ценность — если никто не шлёт логи через OTLP, никто и спаны не шлёт.

**Анализ вариантов хранения:**

| Вариант                                              | Pro                                                                                                                  | Con                                                                                                                                                                                                                                                | Вердикт       |
|------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------|
| (a) Rich-log: `Events.Kind='span'` + всё остальное в Properties | Переиспользуем Events-таблицу, один KQL-target.                                                                    | `Duration` / `ParentSpanId` / `Kind` / `Status` живут в Properties JSON → неудобный KQL (`where json_extract(PropertiesJson,'$.Duration') > 100`). Schema врёт: Events заточен под лог-текст; спаны имеют структуру.                                            | **Отклонён**  |
| (b1) Отдельная таблица `Spans` внутри `{workspace}.logs.db` | Schema соответствует реальности: `(SpanId PK, TraceId, ParentSpanId, Name, Kind, StartUnixNs, EndUnixNs, StatusCode, AttributesJson, EventsJson, LinksJson)`. Индекс `(TraceId, StartUnixNs)` для waterfall. Один SQLite-файл per workspace. | Общий writer-lock с Events → spans-ingest блокирует лог-запросы под нагрузкой. Tier 2 mixed-benchmark в `perf-baseline.md` уже показывает ~66× slowdown query-latency при concurrent ingest в одном workspace DB — traces-ingest bursts это усугубят. Asymmetric retention (logs 30d / spans 7d) требует schema-version conditionals внутри одного файла. | **Отклонён**  |
| (b2) Отдельный файл `{workspace}.traces.db`          | Всё из (b1) **плюс**: независимый writer-lock (spans-ingest не блокирует лог-запросы), независимый `DeleteOlderThanAsync` для асимметричного retention'а, независимый VACUUM / compaction, нулевой storage-overhead когда Phase H не включена (файл не создаётся), продолжает существующий паттерн `.logs.db` / `.meta.db`. | +1 file handle per workspace (тривиально). Нет cross-table JOIN'ов — но они и не нужны (waterfall идёт в spans-only, `log-by-trace_id` идёт в events-target; два KQL-target'а, два запроса).                                       | **Рекомендовано** |
| (c) Полностью скипнуть traces                        | Zero scope.                                                                                                          | Направления 1 + 2 уже покрывают половину OTel-surface; скипнуть traces = trace-waterfall UI не выйдет никогда.                                                                                                                                      | **Fallback**  |

**Обоснование: файл, не таблица.** Решающий фактор — измеренные 66× query-latency penalty при concurrent ingest в `perf-baseline.md` (Tier 2 mixed-workload). Эта штрафная рядом — прямое следствие SQLite'овского single-writer-lock per DB file. Спаны в том же файле = trace-ingest bursts измеримо блокируют log-read latency; спаны в отдельном файле = два writer-пути механически изолированы. Асимметричный retention и conditional-Phase-H-delivery — меньшие выигрыши поверх.

**Новый контракт `ISpanStore`** (симметричен `ILogStore`, scope = спаны):

```csharp
public interface ISpanStore
{
    ValueTask CreateWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
    ValueTask DropWorkspaceAsync(WorkspaceId workspaceId, CancellationToken ct);
    ValueTask AppendBatchAsync(WorkspaceId workspaceId, IReadOnlyList<SpanRecord> batch, CancellationToken ct);
    IAsyncEnumerable<Span> QueryKqlAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct);
    Task<KqlResult> QueryKqlResultAsync(WorkspaceId workspaceId, KustoCode kql, CancellationToken ct);
    // Specialized hot-path for the waterfall view: one trace_id → indexed lookup of all spans.
    // Could be expressed as KQL `spans | where TraceId == '...'` but that routes through the
    // transformer for no gain; this method is direct SQL.
    ValueTask<IReadOnlyList<Span>> GetByTraceIdAsync(WorkspaceId workspaceId, string traceId, CancellationToken ct);
    ValueTask<long> DeleteOlderThanAsync(WorkspaceId workspaceId, DateTimeOffset cutoff, CancellationToken ct);
}
```

`SqliteSpanStore` живёт в `src/YobaLog.Core/Tracing/Sqlite/`, открывает `{workspace}.traces.db`, владеет собственной `SpansFts5`-virtual-таблицей если text-search по `Name` спана окажется оправдан. `KqlTransformer` роутит по target'у: `events` → `ILogStore`, `spans` → `ISpanStore`. Dual-executor тесты расширяются — reference-executor `kusto-loco` крутится над `IEnumerable<Span>`.

**Асимметрия retention'а.** `RetentionOptions` обзаводится sibling'ом: `DefaultSpansRetainDays` отдельно от `DefaultRetainDays`, `SystemSpansRetainDays` отдельно от `SystemRetainDays`. Per-workspace `SpanRetentionPolicy` записи в `$system.meta.db` (тот же паттерн, что `RetentionPolicies` для логов). Default если не задан — зеркало default'а логов. Операторы тюнят trace-heavy workload'ы через `/admin/retention` — типичный ask «логи 30 дней, трейсы 7».

**Sequencing Phase G ↔ Phase H для self-emitted спанов.** Phase G должен куда-то писать спаны, но мы не хотим блокировать его на Phase H. Двухступенчатый план:

1. **Phase G (standalone):** self-emission пишет спаны как rich-logs в `$system.logs.db` под `Properties.Kind = "span"`, с flatten-ом `parent_span_id / duration_ns / start_unix_ns / status_code` в Properties. KQL-запросы по ним неудобны (cons варианта (a)), но это приемлемый interim — self-emitted traces суть self-observability-костыль, не user-facing feature.
2. **Phase H (когда придёт):** `ISpanStore` ship'ится → миграция на первом старте копирует rich-log спаны из `$system.logs.db` в `$system.traces.db`, затем переключает Phase G's exporter на `ISpanStore.AppendBatchAsync(WorkspaceId.System, ...)` напрямую. `Properties.Kind="span"` строки в Events перестают появляться после одноразового прогона миграции. Phase G при этом остаётся независимо поставляемым (ценность для self-debugging yobalog'а сама по себе), без потери данных когда trace-UI наконец выйдет.

**UI: trace waterfall** — Razor partial `_TraceWaterfall.cshtml`, принимает `TraceId`, вытягивает спаны в порядке `start_unix_ns`, рендерит `<div>`-bars с width ∝ duration, indent по depth-in-parent-tree. Hover-tooltip на span показывает attributes / events / status. ~200 LOC Razor + ~100 LOC TS (event-delegated hover). **Без D3 / vis.js** — держим dependency-footprint плоским, соответствует позиции "htmx + DaisyUI, без тяжёлого клиентского фреймворка".

**KQL-расширения:** `spans | where Duration > 100ms | order by StartTime`. Новые колонки на `spans`-target'е: `Duration` (int ms, computed из `EndUnixNs - StartUnixNs`), `ParentSpanId`, `Kind`, `Status`. Transformer: `ApplyEventQuery` генерализуется до `ApplyQuery(target ∈ {events, spans})`; dual-executor тесты +10-15 spans-cases. Новых KQL-операторов не вводится.

**Service map / aggregate views — explicit defer.** Граф зависимостей по спанам требует `GROUP BY service.name + resource.attributes`-style rollups; огромный UX surface, минимальная ценность для self-hosted single-service observability. Документируется как «out of MVP».

**Effort:** 7-10 d. Контракт `ISpanStore` + `SqliteSpanStore` + `.traces.db` schema + `linq2db`-mapping + OTLP-traces Protobuf-парсер + `spans`-target branch в KQL transformer'е + waterfall Razor partial + span-details panel + миграция rich-log→Spans для Phase G.

### Отвергнутые альтернативы

- **«Jaeger-only для трейсов»** — фрагментирует observability по двум store'ам и двум UI. Нарушает принцип yobalog = self-hosted + single-pane-of-glass.
- **«Seq-native `Seq.OpenTelemetry.Exporter` для client-side emit»** — этот пакет суть CLIENT-side exporter, отправляет из OTel-enabled app'а В Seq-овский OTLP-ingester. Не применим на receive-стороне; мы строим receiver.
- **«Ингестить metrics (OTLP Metrics)»** — yobalog = log/trace store. Metrics — территория Prometheus/Grafana, совсем другая storage-форма (counters / gauges / histograms → time-series), совсем другой query-surface. Жёсткое «нет»; документировано как hard-scope в §1 spec-proposal'е. Будущая 2026.1-поддержка OTLP-metrics в Seq — НЕ сигнал копировать; у них это отдельный metrics-UX, навешенный сбоку.
- **«Делать все три направления параллельно»** — scope creep, нет feedback loop. F сам по себе даёт measurable value (Seq-compat + OTLP-compat = крупнейший протокольный surface среди self-hosted log-store'ов).

### Resolved questions (решения зафиксированы 2026-04-21)

1. **Источник Protobuf-типов: `OpenTelemetry.Proto` NuGet.** Proto-generated DTO — wire-boundary escape от инварианта «max static typing», в том же классе, что `JsonElement` в CLEF-парсере. Инвариант не применяется если типы остаются на границе парсера и маппятся в наши immutable domain-типы до того, как data уходит глубже. Компиляция из source добавила бы `protoc` build-step + транзитивный toolchain-dep без выигрыша. **Правило реализации:** proto-DTO живут только в parser-слое (`OtlpLogsHandler.cs` / `OtlpTraceHandler.cs`), никогда не утекают в контракты `ILogStore` / `ISpanStore`. Защищается границей Core-проекта (proto-типы в `YobaLog.Web`, Core не ссылается на NuGet).

2. **Exposиmo оба: `/ingest/otlp/v1/logs` и `/v1/logs`.** Первый — зеркало Seq, нулевая фрикция для существующих OTel→Seq пайплайнов. Второй — OTel-standard path для клиентов с `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=https://host/v1/logs`, работает из коробки. Оба резолвятся в один handler. **Test requirement:** compat-тест ассертит идентичные `202`-ответы на один и тот же payload через оба пути.

3. **gRPC отложен в Phase F+1.** HTTP/Protobuf — default во всех major OTel SDK'ах в 2026; gRPC opt-in через `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`. Требование HTTP/2 натыкается на reverse-proxy quirks (CloudFlare / nginx config), плюс `Grpc.AspNetCore` — отдельная зависимость. YAGNI до первого конкретного запроса. Revisit по первому concrete use-case'у; placeholder-endpoint не делаем.

4. **Activity emission gate'ится на `!IsEnvironment("Testing")`.** Зеркалит `UseHttpsRedirection`-паттерн. Тесты, которые специально хотят ассертить emission (например, регрессия на конкретные ActivitySource-имена), заводят локальный `ActivityListener { ShouldListenTo = s => s.Name.StartsWith("YobaLog") }` внутри fixture'а — explicit opt-in, scoped к тесту.

**Заметка про SQLite-инструментацию (Phase G detail).** `OpenTelemetry.Instrumentation.SqlClient` покрывает `System.Data.SqlClient`; `linq2db` + SQLite идёт через `Microsoft.Data.Sqlite`, который `.SqlClient` не видит. Ни официального, ни community NuGet'а `Instrumentation.Sqlite` не существует. Phase G добавляет manual `ActivitySource`-спаны на границе `SqliteLogStore.AppendBatchAsync` / `QueryKqlAsync` / `DeleteOlderThanAsync` — ~10-15 строк тривиального кода, Activity-tag-и `db.system=sqlite`, `db.operation=<verb>`, `db.statement` опускаем (слишком verbose для LogStore hot-path — `QueryKqlAsync` и так пишет KQL в отдельный лог).

### Рекомендация

**Phase F (OTLP-logs ingest) первым, с большим отрывом.** Минимальный surface, максимальный real-world payoff — любой OTel-enabled .NET / Go / Python / JS app становится yobalog-писателем. Прямое расширение существующего ingestion-пайплайна: тот же `ILogStore`, тот же `CompositeApiKeyStore`, тот же workspace-routing, ~80 % shared инфраструктуры тестов с Serilog / Winston-compat сьютами.

Phase G (self-emission) gate'ится на F landing: он пишет В workspace, который только что выучил OTLP-shape данных — dogfood-принцип.

Phase H (traces + waterfall UI) gate'ится на G proving useful: если telemetry-emission остаётся тихой, span-storage остаётся гипотетическим. Может жить как deferred plan-bullet бесконечно.

**Non-decision для записи:** OTLP-metrics ingest НЕ шипим, никогда. yobalog = logs + (опционально, позже) traces. Metrics — не наш дом.

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
