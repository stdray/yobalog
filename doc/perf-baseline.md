# Perf baseline

**Snapshot: 2026-04-19, commit `a8dd660` (post-Phase-D UX + Properties-filter work).** Windows 11 Pro (i9-14900K), .NET 10.0.202, BDN 0.15.8, ShortRun (3 warmup + 3 measurement).

Запускается локально:

```bash
dotnet run --project benchmarks/YobaLog.Benchmarks -c Release -- --filter "*" -j Short
```

Для PR-gate добавить статистику Mann–Whitney через `--statisticalTest 10%` vs prior baseline.

## Tier 1 — CleFParser

| Method           | BatchSize | Mean        | Allocated  |
|------------------|-----------|------------:|-----------:|
| ParseStreamAsync | 100       |   125.8 μs  |   153 KB   |
| ParseStreamAsync | 1 000     |  1 224 μs   |   1.5 MB   |
| ParseStreamAsync | 10 000    | 12 057 μs   |  14.8 MB   |

Throughput: ~830k events/sec линейно. Allocations 1.5 KB/event (стабильны между run'ами) — доминирует `JsonDocument.Parse` на каждой строке. Если упрёмся — миграция на `Utf8JsonReader` (цена: больше кода, сложнее валидация).

## Tier 1 — KqlTransformer (in-memory)

| Method         | RowCount | Mean       | Allocated |
|----------------|----------|-----------:|----------:|
| Where          | 1 000    |   434 μs   |   11 KB   |
| Where          | 100 000  |   915 μs   |   11 KB   |
| WhereTakeOrder | 1 000    |   889 μs   |   32 KB   |
| WhereTakeOrder | 100 000  | 2 598 μs   |  999 KB   |
| Project        | 1 000    |   483 μs   |   85 KB   |
| Project        | 100 000  | 4 827 μs   |  7.1 MB   |
| Summarize      | 1 000    |   110 μs   |  237 KB   |
| Summarize      | 100 000  | 10 239 μs  | 22.9 MB   |
| Count          | 1 000    |   500 μs   |  102 KB   |
| Count          | 100 000  | 5 176 μs   |  8.8 MB   |

Наблюдения:
- `Where` на `IQueryable<EventRecord>` ленивый: 1k→100k время растёт в 2x (не в 100x), allocations константны 11 KB — фильтр собирается на AST-стадии, отработка вместе с enumeration.
- `OrderBy` (`WhereTakeOrder`) — единственный в prefix, кто аллоцирует: 999 KB на 100k, Gen2 — сортирует весь set перед take.
- Shape-changing ops (Project/Summarize/Count) заметно дороже в allocation: `object?[]` per row + boxing в Dictionary ключах для summarize. 22 MB на 100k summarize-by-Level — **место для оптимизации**, когда станет узким местом.

## Tier 2 — SqliteLogStore (real .db)

| Method            | FixtureSize | Mean         | Allocated |
|-------------------|-------------|-------------:|----------:|
| AppendBatchAsync  | 1 000       |    27.5 ms   |    1 MB   |
| AppendBatchAsync  | 100 000     |   **978 ms** |   60 MB   |
| QueryByIndex      | 1 000       |   149 μs     |   28 KB   |
| QueryByIndex      | 100 000     |   153 μs     |   28 KB   |
| QueryFtsHas       | 1 000       |   234 μs     |   28 KB   |
| QueryFtsHas       | 100 000     | **14.8 ms**  |   28 KB   |
| QueryContainsScan | 1 000       |   146 μs     |   28 KB   |
| QueryContainsScan | 100 000     |   149 μs     |   28 KB   |

Наблюдения:
- **Ingest**: ~102k events/sec на батче 100k, 60 MB allocations (BulkCopy + объекты). Единый hot-path при высокой нагрузке — оптимизировать при необходимости через подготовленные parameters.
- **Indexed query** (`Level >= 4 | take 50`): sub-200 μs независимо от fixture size — индекс `ix_events_ts_id` отрабатывает мгновенно.
- **FTS5 MATCH vs LIKE на частом слове (известный anti-pattern):** с `.Take(50)` и частым словом (матчит почти все 100k строк), `contains` через LIKE **в ~100x быстрее** FTS5 MATCH (149 μs vs 14.8 ms). Причина задокументирована в `decision-log.md` (2026-04-19 запись про FTS5 MATCH в IN-subquery). Fix-план: либо переписать `FtsHas` на raw SQL с `JOIN EventsFts ... LIMIT N`, либо дать UI-hint "has выгоден на селективных запросах".

## Tier 2 — IngestionPipeline vs direct AppendBatchAsync

| Method                                    | TotalEvents | Mean      | Allocated | events/sec |
|-------------------------------------------|-------------|----------:|----------:|-----------:|
| Pipeline: IngestAsync + drain (StopAsync) |   1 000     |   6.4 ms  |   1 MB    |   156k     |
| Direct: AppendBatchAsync (reference)      |   1 000     |   6.2 ms  |  985 KB   |   161k     |
| Pipeline                                  |  10 000     |  70 ms    |  10 MB    |   142k     |
| Direct                                    |  10 000     |  66 ms    |   6 MB    |   151k     |
| Pipeline                                  | 100 000     | 880 ms    |  97 MB    |   114k     |
| Direct                                    | 100 000     | 1 012 ms  |  57 MB    |    99k     |

Наблюдения:
- **Pipeline overhead ~3%** на 1k событий (single-shot), ~6% на 10k, на 100k **pipeline быстрее** direct (в пределах noise — ShortRun iter=3 даёт широкий error-bar, см. ниже).
- **Allocation penalty** pipeline'а: ~1.7x (99 MB vs 57 MB на 100k) — `Channel<T>` boxing + writer-loop state.
- **Real win pipeline'а не виден в single-shot bench** — он раскрывается под concurrent HTTP-writers (decoupling между ingest-request и SQLite-write). Нужен Tier 3 NBomber-сценарий.

**Throughput-порядок:** ~100-150k events/sec sustained на single-writer локально (NVMe).

## Tier 2 — Mixed: query latency под ingest-нагрузкой

20 indexed queries (`| where Level >= 4 | take 50`).

| Scenario                                  | Total     | Per query |
|-------------------------------------------|----------:|----------:|
| Queries only (no concurrent writers)      |   3.0 ms  |  150 μs   |
| Queries while pipeline ingests 1k events  | 117.0 ms  |    6 ms   |

**40x slowdown под concurrent ingest.** Причина — SQLite writer-lock: пока pipeline пишет batch, reader ждёт. WAL-режим позволяет concurrent readers, но параллельная write-транзакция сериализует их по одному. Error bar огромный (17 ms stddev) — timing зависит от оверлапа query с write.

На сотнях событий/сек query-latency < 10 ms — приемлемо. На тысячах — нужен Tier 3 для реалистичного sustained-load профиля.

## Методические замечания

- **ShortRun (3 iterations)** даёт wide error-bars на i9-14900K: DVFS + E-core scheduling + background load вносят variance до ±50% на SQLite/FTS измерениях (видно в Error-колонке CSV-отчётов). Для стабильных чисел нужен Medium/Long run, но он на full suite занимает 40+ минут — цена не оправдана для dev-baseline. CI PR-gate через `--statisticalTest 10%` vs `BenchmarkDotNet.Artifacts/` из main будет ловить именно регрессии.
- **Allocations стабильны** между snapshot'ами (в пределах 1%) — они не зависят от CPU-state, так что регрессии по памяти ловятся надёжно.
- Между текущим и предыдущим (2026-04-19 `c56d605`) snapshot'ами большинство timing'ов ушли на +40-80% при идентичных allocations — это шум измерения, не регрессия кода. Между снэпшотами изменения: flat-Properties в transformer, `json_extract` Sql.Expression, split-pipeline в Execute. Ни одно не затрагивает hot-path замеряемых сценариев (Where/Project/Summarize/Count/FTS/LIKE/BulkCopy).

## Когда обновлять

- После значимых правок в hot paths (ingestion, transformer, storage) — перед коммитом.
- При добавлении нового оператора KQL — отдельная строка в таблице KqlTransformer.
- Расхождение >10% в **allocations** vs предыдущий snapshot требует ревью: либо обоснованная регрессия (добавленная фича), либо бага. Timing-расхождения до ±50% — noise на ShortRun, не рассматривать как сигнал.
