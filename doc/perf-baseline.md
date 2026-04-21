# Perf baseline

**Snapshot: 2026-04-21, commit `b157dc3` (post-Phase-D + KQL-completions partial + allowlist).** Windows 11 Pro (i9-14900K), .NET 10.0.6 / SDK 10.0.202, BDN 0.15.8, ShortRun (3 warmup + 3 measurement).

Запускается локально:

```bash
dotnet run --project benchmarks/YobaLog.Benchmarks -c Release -- --filter "*" -j Short
```

Для PR-gate добавить статистику Mann–Whitney через `--statisticalTest 10%` vs prior baseline.

## Tier 1 — CleFParser

| Method           | BatchSize | Mean       | Allocated |
|------------------|-----------|-----------:|----------:|
| ParseStreamAsync | 100       |    76.1 μs |   153 KB  |
| ParseStreamAsync | 1 000     |   733 μs   |   1.5 MB  |
| ParseStreamAsync | 10 000    | 7 503 μs   |  14.8 MB  |

Throughput: ~1.3M events/sec линейно (в этом прогоне выше чем предыдущий baseline; причина — обновление Kusto/linq2db хотелось бы подтвердить, но измерения консистентны). Allocations 1.5 KB/event неизменны — доминирует `JsonDocument.Parse` на каждой строке. Если упрёмся — миграция на `Utf8JsonReader` (цена: больше кода, сложнее валидация).

## Tier 1 — KqlTransformer (in-memory)

| Method         | RowCount | Mean       | Allocated |
|----------------|----------|-----------:|----------:|
| Where          | 1 000    |   303 μs   |   11 KB   |
| Where          | 100 000  |   513 μs   |   11 KB   |
| WhereTakeOrder | 1 000    |   573 μs   |   32 KB   |
| WhereTakeOrder | 100 000  | 1 671 μs   |  999 KB   |
| Project        | 1 000    |   316 μs   |   85 KB   |
| Project        | 100 000  | 2 210 μs   |  7.3 MB   |
| Summarize      | 1 000    |    50 μs   |  237 KB   |
| Summarize      | 100 000  | 5 457 μs   | 23.4 MB   |
| Count          | 1 000    |   324 μs   |  102 KB   |
| Count          | 100 000  | 2 574 μs   |  9.0 MB   |

Наблюдения:
- `Where` на `IQueryable<EventRecord>` остаётся ленивым: 1k→100k время растёт в ~1.7x (не в 100x), allocations константны 11 KB — фильтр собирается на AST-стадии, отработка вместе с enumeration.
- `OrderBy` (`WhereTakeOrder`) — единственный в prefix, кто аллоцирует: 999 KB на 100k, Gen2 — сортирует весь set перед take.
- Shape-changing ops (Project/Summarize/Count) заметно дороже в allocation: `object?[]` per row + boxing в Dictionary ключах для summarize. 23 MB на 100k summarize-by-Level — **место для оптимизации**, когда станет узким местом.

## Tier 2 — SqliteLogStore (real .db)

| Method            | FixtureSize | Mean         | Allocated |
|-------------------|-------------|-------------:|----------:|
| AppendBatchAsync  | 1 000       |    27.0 ms   |    1 MB   |
| AppendBatchAsync  | 100 000     |   **937 ms** |   60 MB   |
| QueryByIndex      | 1 000       |    76 μs     |   28 KB   |
| QueryByIndex      | 100 000     |    79 μs     |   28 KB   |
| QueryFtsHas       | 1 000       |   133 μs     |   27 KB   |
| QueryFtsHas       | 100 000     |  **8.2 ms**  |   27 KB   |
| QueryContainsScan | 1 000       |    75 μs     |   28 KB   |
| QueryContainsScan | 100 000     |    78 μs     |   28 KB   |

Наблюдения:
- **Ingest**: ~107k events/sec на батче 100k, 60 MB allocations (BulkCopy + объекты). Единый hot-path при высокой нагрузке.
- **Indexed query** (`Level >= 4 | take 50`): sub-100 μs независимо от fixture size — индекс `ix_events_ts_id` отрабатывает мгновенно.
- **FTS5 MATCH vs LIKE на частом слове (известный anti-pattern):** с `.Take(50)` и частым словом (матчит почти все 100k строк), `contains` через LIKE **в ~100x быстрее** FTS5 MATCH (78 μs vs 8.2 ms). Причина задокументирована в `decision-log.md` (2026-04-19 запись про FTS5 MATCH в IN-subquery). Fix-план: либо переписать `FtsHas` на raw SQL с `JOIN EventsFts ... LIMIT N`, либо дать UI-hint "has выгоден на селективных запросах".

## Tier 2 — IngestionPipeline vs direct AppendBatchAsync

| Method                                    | TotalEvents | Mean      | Allocated | events/sec |
|-------------------------------------------|-------------|----------:|----------:|-----------:|
| Pipeline: IngestAsync + drain (StopAsync) |   1 000     |   6.0 ms  |   1 MB    |   168k     |
| Direct: AppendBatchAsync (reference)      |   1 000     |   6.0 ms  |  985 KB   |   167k     |
| Pipeline                                  |  10 000     |  71 ms    |  10 MB    |   140k     |
| Direct                                    |  10 000     |  51 ms    |   6 MB    |   196k     |
| Pipeline                                  | 100 000     | 836 ms    |  97 MB    |   120k     |
| Direct                                    | 100 000     | 819 ms    |  57 MB    |   122k     |

Наблюдения:
- **Pipeline overhead** ≈ noise на 1k, +40% на 10k, +2% на 100k (в пределах ±50% ShortRun error-bar). Асимптотика сходится с direct path.
- **Allocation penalty** pipeline'а: ~1.7x (97 MB vs 57 MB на 100k) — `Channel<T>` boxing + writer-loop state.
- **Real win pipeline'а не виден в single-shot bench** — он раскрывается под concurrent HTTP-writers (decoupling между ingest-request и SQLite-write). Нужен Tier 3 NBomber-сценарий.

**Throughput-порядок:** ~120-170k events/sec sustained на single-writer локально (NVMe).

## Tier 2 — Mixed: query latency под ingest-нагрузкой

20 indexed queries (`| where Level >= 4 | take 50`).

| Scenario                                  | Total     | Per query |
|-------------------------------------------|----------:|----------:|
| Queries only (no concurrent writers)      |   1.6 ms  |   80 μs   |
| Queries while pipeline ingests 1k events  | 106.3 ms  |   5.3 ms  |

**~66x slowdown под concurrent ingest.** Причина — SQLite writer-lock: пока pipeline пишет batch, reader ждёт. WAL-режим позволяет concurrent readers, но параллельная write-транзакция сериализует их по одному. Error bar огромный (22 ms stddev) — timing зависит от оверлапа query с write.

На сотнях событий/сек query-latency < 10 ms — приемлемо. На тысячах — нужен Tier 3 для реалистичного sustained-load профиля.

## Методические замечания

- **ShortRun (3 iterations)** даёт wide error-bars на i9-14900K: DVFS + E-core scheduling + background load вносят variance до ±50% на SQLite/FTS измерениях (видно в Error-колонке CSV-отчётов). Для стабильных чисел нужен Medium/Long run, но он на full suite занимает 40+ минут — цена не оправдана для dev-baseline. CI PR-gate через `--statisticalTest 10%` vs `BenchmarkDotNet.Artifacts/` из main будет ловить именно регрессии.
- **Allocations стабильны** между snapshot'ами (в пределах 1%) — они не зависят от CPU-state, так что регрессии по памяти ловятся надёжно.
- Между текущим (`b157dc3`) и предыдущим (`a8dd660`) snapshot'ами **timing'и ушли на 20-50% быстрее** при идентичных allocations — больше похоже на .NET/Kusto обновление hot-path, чем регрессию. Изменения кода между snapshot'ами: DB-backed admin (workspaces/users/api-keys/retention), KQL completion allowlist, dot-space fix, HTML-в-.cs рефакторинг. Ни одно не затрагивает hot-path замеряемых сценариев.

## Когда обновлять

- После значимых правок в hot paths (ingestion, transformer, storage) — перед коммитом.
- При добавлении нового оператора KQL — отдельная строка в таблице KqlTransformer.
- Расхождение >10% в **allocations** vs предыдущий snapshot требует ревью: либо обоснованная регрессия (добавленная фича), либо бага. Timing-расхождения до ±50% — noise на ShortRun, не рассматривать как сигнал.
