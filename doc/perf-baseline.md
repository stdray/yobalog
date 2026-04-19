# Perf baseline

**Snapshot: 2026-04-19, commit `c56d605` (+ docs-only after).** Windows 11 Pro, .NET 10.0.202, BDN 0.15.8, ShortRun (3 warmup + 3 measurement).

Запускается локально:

```bash
dotnet run --project benchmarks/YobaLog.Benchmarks -c Release -- --filter "*" -j Short
```

Для PR-gate добавить статистику Mann–Whitney через `--statisticalTest 10%` vs prior baseline.

## Tier 1 — CleFParser

| Method           | BatchSize | Mean       | Gen0       | Allocated  |
|------------------|-----------|-----------:|-----------:|-----------:|
| ParseStreamAsync | 100       |    78 μs   |    8.3     |   153 KB   |
| ParseStreamAsync | 1 000     |   739 μs   |   81.1     |   1.5 MB   |
| ParseStreamAsync | 10 000    | 7 506 μs   |  820.3     |  14.8 MB   |

Throughput: ~1.35M events/sec линейно. Allocations ~1.5 KB/event — доминирует `JsonDocument.Parse` на каждой строке. Если упрёмся — миграция на `Utf8JsonReader` (цена: больше кода, сложнее валидация).

## Tier 1 — KqlTransformer (in-memory)

| Method         | RowCount | Mean       | Allocated |
|----------------|----------|-----------:|----------:|
| Where          | 1 000    |   298 μs   |   11 KB   |
| Where          | 100 000  |   520 μs   |   11 KB   |
| WhereTakeOrder | 1 000    |   580 μs   |   32 KB   |
| WhereTakeOrder | 100 000  | 1 392 μs   |  999 KB   |
| Project        | 1 000    |   315 μs   |   85 KB   |
| Project        | 100 000  | 2 407 μs   |  7.1 MB   |
| Summarize      | 1 000    |    50 μs   |  237 KB   |
| Summarize      | 100 000  | 5 964 μs   | 22.9 MB   |
| Count          | 1 000    |   319 μs   |  102 KB   |
| Count          | 100 000  | 2 655 μs   |  8.8 MB   |

Наблюдения:
- `Where` на `IQueryable<EventRecord>` ленивый: 1k→100k время растёт в 2x (не в 100x), allocations константны 11 KB — фильтр собирается на AST-стадии, отработка вместе с enumeration.
- `OrderBy` (`WhereTakeOrder`) — единственный в prefix, кто аллоцирует: 999 KB на 100k, Gen2 — сортирует весь set перед take.
- Shape-changing ops (Project/Summarize/Count) заметно дороже в allocation: `object?[]` per row + boxing в Dictionary ключах для summarize. 22 MB на 100k summarize-by-Level — **место для оптимизации**, когда станет узким местом.

## Tier 2 — SqliteLogStore (real .db)

| Method            | FixtureSize | Mean        | Allocated |
|-------------------|-------------|------------:|----------:|
| AppendBatchAsync  | 1 000       |   27 ms     |    1 MB   |
| AppendBatchAsync  | 100 000     | **830 ms**  |   60 MB   |
| QueryByIndex      | 1 000       |   79 μs     |   28 KB   |
| QueryByIndex      | 100 000     |   81 μs     |   28 KB   |
| QueryFtsHas       | 1 000       |  137 μs     |   28 KB   |
| QueryFtsHas       | 100 000     | **8 257 μs**|   28 KB   |
| QueryContainsScan | 1 000       |   78 μs     |   28 KB   |
| QueryContainsScan | 100 000     |   78 μs     |   28 KB   |

Наблюдения:
- **Ingest**: ~120k events/sec на батче 100k, 60 MB allocations (BulkCopy + объекты). Единый hot-path при высокой нагрузке — оптимизировать при необходимости через подготовленные parameters.
- **Indexed query** (`Level >= 4 | take 50`): sub-100 μs независимо от fixture size — индекс `ix_events_ts_id` отрабатывает мгновенно.
- **🔥 FTS5 MATCH vs LIKE на частом слове:** с `.Take(50)` и словом, матчащим почти все строки (100k), **`contains` через LIKE быстрее FTS5 MATCH в 100x** (78 μs vs 8 мс). Причина: `LIKE` с `Take(50)` делает early-exit после 50-го match в table scan; FTS5 MATCH сначала материализует полный rowid-set из inverted index, потом join с Events, потом LIMIT. Для редких слов (`has 'rare-term'`) FTS5 будет быстрее. Открытый вопрос — переписать `FtsHas` на `SELECT ... FROM EventsFts ORDER BY rank LIMIT N` + JOIN, либо дать UI-hint что `has` выгоден только на селективных запросах.

## Когда обновлять

- После значимых правок в hot paths (ingestion, transformer, storage) — перед коммитом.
- При добавлении нового оператора KQL — отдельная строка в таблице KqlTransformer.
- Расхождение >10% vs предыдущий snapshot требует ревью: либо обоснованная регрессия (добавленная фича), либо бага.
