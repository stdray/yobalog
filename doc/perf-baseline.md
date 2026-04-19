# Perf baseline

Запускается локально:

```bash
dotnet run --project benchmarks/YobaLog.Benchmarks -c Release -- --filter "*" -j Short
```

`-j Short` = 3 warmup + 3 measurement per config. Для PR-gate добавить статистику Mann–Whitney через `--statisticalTest 10%` vs prior baseline.

## Tier 1 — CleFParser (`--filter "*CleFParserBenchmark*"`)

| Method           | BatchSize | Mean | Allocated per op |
|------------------|-----------|------|------------------|
| ParseStreamAsync | 100       | TBD  | TBD              |
| ParseStreamAsync | 1 000     | TBD  | TBD              |
| ParseStreamAsync | 10 000    | TBD  | TBD              |

Dry-run (smoke-only, не baseline): 100 → 12ms/153KB; 1 000 → 17ms/1.5MB; 10 000 → 69ms/15MB. `JsonDocument.Parse` per line — главная статья аллокаций; оптимизация на `Utf8JsonReader` — если станет узким местом.

## Tier 1 — KqlTransformer (`--filter "*KqlTransformerBenchmark*"`)

| Method         | RowCount | Mean | Allocated |
|----------------|----------|------|-----------|
| Where          | 1 000    | TBD  | TBD       |
| Where          | 100 000  | TBD  | TBD       |
| WhereTakeOrder | 1 000    | TBD  | TBD       |
| WhereTakeOrder | 100 000  | TBD  | TBD       |
| Project        | 1 000    | TBD  | TBD       |
| Project        | 100 000  | TBD  | TBD       |
| Summarize      | 1 000    | TBD  | TBD       |
| Summarize      | 100 000  | TBD  | TBD       |
| Count          | 1 000    | TBD  | TBD       |
| Count          | 100 000  | TBD  | TBD       |

## Tier 2 — SqliteLogStore (`--filter "*SqliteLogStoreBenchmark*"`)

| Method            | FixtureSize | Mean | Allocated |
|-------------------|-------------|------|-----------|
| AppendBatchAsync  | 1 000       | TBD  | TBD       |
| AppendBatchAsync  | 100 000     | TBD  | TBD       |
| QueryByIndex      | 1 000       | TBD  | TBD       |
| QueryByIndex      | 100 000     | TBD  | TBD       |
| QueryFtsHas       | 1 000       | TBD  | TBD       |
| QueryFtsHas       | 100 000     | TBD  | TBD       |
| QueryContainsScan | 1 000       | TBD  | TBD       |
| QueryContainsScan | 100 000     | TBD  | TBD       |

## Когда обновлять

- После значимых правок в hot paths (ingestion, transformer, storage) — перед коммитом.
- При добавлении нового оператора KQL — отдельная строка в таблице KqlTransformer.
- Расхождение >10% vs предыдущий snapshot требует ревью: либо обоснованная регрессия (добавленная фича), либо бага.
