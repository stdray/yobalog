# YobaLog — Локальное performance-тестирование

Сопроводительная заметка к [`spec.md`](spec.md). Unit, integration, dual-executor и архитектурные property-тесты — покрыты основной спекой (`§10`) и уже написаны. **Эта заметка — про performance локально**: как мерить, чем мерить, что мерить в первую очередь.

---

## Философия

- **Performance — тоже тесты.** Каждое утверждение про "быстро", "мало памяти", "не тормозит" — должно иметь измеряемый порог (ms, events/sec, MB), иначе регрессия пройдёт незамеченной.
- **Бенчмарки защищают архитектурные решения.** Когда спека основана на предположении ("Channels быстрее наивной записи", "FTS5 дешёвле custom inverted index") — пишется бенч, который это предположение валидирует. Если оказалось ложным — спеку правим.
- **Три tier'а, три инструмента.** Не всё — BenchmarkDotNet. Разные вопросы требуют разных измерителей.

---

## Tier 1 — Микро (BenchmarkDotNet)

Изолированные hot paths. `[MemoryDiagnoser]`, Gen0/1/2 attribution, статистическая строгость с warmup.

Что покрыть:
- CLEF-парсер — throughput (MB/s), allocations/event
- KQL→SQL transformer — ms/query на типовых формах (where/take/project/summarize)
- Channels push path — allocations, lock contention

---

## Tier 2 — Компонентный (BDN + real SQLite)

BDN оборачивает тот же `ILogStore`, что integration-тесты. Реальный `.db` файл создаётся в `[GlobalSetup]`.

Что покрыть:
- `AppendBatchAsync` — events/sec с FTS5 и без, с разным числом expression-indexes
- `QueryKqlAsync` / `QueryKqlResult` — latency на 100k / 1M фикстурах
- FTS5 MATCH vs full-scan `Contains()` — прямое сравнение
- Стоимость каждого expression-index на `json_extract`

---

## Tier 3 — Load (NBomber, локально)

Долгие сценарии, реальный HTTP. Запускаются руками на локалке перед важными изменениями в ingestion-пути.

Что покрыть:
- HTTP ingestion endpoint под N concurrent клиентов (100, 1k, 10k)
- Query latency под ingestion-шумом (параллельный читатель)
- Sustained event rate, при котором `p95 query latency` остаётся в разумных пределах

---

## Memory footprint — отдельная тема

**Главный sell-point YobaLog — ≤100 MB RAM.** Это SLO, не opinion.

Инструмент — **`dotnet-counters`** (runtime diagnostic tool из .NET SDK).

Measure points:
- Idle (сервис поднят, 0 трафика) — ожидаем <50 MB
- Baseline load (100 events/sec) — ожидаем <80 MB
- Peak load (10k events/sec) — ожидаем <100 MB
- После retention-прохода — ожидаем возврат к baseline (если нет — утечка в retention)

Метрики:
- `process-working-set` (MB) — реальный RSS процесса
- `gc-gen-0-count`, `gc-gen-1-count`, `gc-gen-2-count` per minute (линейный рост gen2 = утечка)
- `gc-heap-size` (managed куча)

**BDN тут не помощник** — `MemoryDiagnoser` мерит allocations, `dotnet-counters` — working set процесса. Это разные вещи.

---

## Приоритетные бенчмарки

Защищают архитектурные предположения из `spec.md`. Если любой провалится — в спеку нужно вносить правки **до** того, как реализация зацементируется.

1. **Channels + single-writer vs наивная конкурентная запись.** Открытый вопрос №1 в спеке — подтвердить экспериментом, что Channels реально выигрывают на многопоточном ingestion. Tier 2.
2. **Стоимость FTS5 при insert.** Ожидается ~2x plain insert. Замер на реальных CLEF-событиях — если >3x, делаем FTS5 синхронизацию асинхронной. Tier 2.
3. **Write amplification от expression-indexes.** Замер на 0, 1, 5, 10, 20 индексов `json_extract`. Устанавливает "max indexes per workspace" рамку в UI. Tier 2.
4. **KQL transformer overhead.** Чтобы dual-executor тесты (референс `KustoQueryContext` vs production) не раздувались по времени. Tier 1.
5. **Shape-changing операторы in-memory.** `project`/`summarize`/`count` сейчас выполняются в C# после материализации prefix'а — надо измерить overhead на 100k/1M строк.

---

## Tooling

| Задача | Инструмент |
|---|---|
| Micro benchmarks | BenchmarkDotNet (+ MemoryDiagnoser) |
| Real DB benchmarks | BDN + временные SQLite-файлы, cleanup в `[GlobalCleanup]` |
| Memory profiling | `dotnet-counters` (working set), `dotnet-trace` (CPU profile), PerfView (deep) |
| HTTP load | NBomber |
| Fake event data | Bogus + детерминированные seed'ы (воспроизводимость) |

---

## Антипаттерны

- ❌ **Микро-бенчи через `Stopwatch`.** Нет warmup'а, нет статистики, нет Gen0/1/2 attribution. Только BDN.
- ❌ **Memory через `GC.GetTotalMemory`.** BDN MemoryDiagnoser точнее для allocations; `dotnet-counters` — для RSS. Разные инструменты для разных вопросов.
- ❌ **Одинаковые фикстуры для integration и perf-тестов.** Perf нужны реалистичные размеры (100k+ events), иначе не ловит проблемы масштаба.
- ❌ **Бенчмарки без зафиксированных thresholds.** "Тест прогнался" без метрики — не perf-тест. Всегда нужен измеряемый порог в baseline.
- ❌ **Переусложнение до зрелой реализации.** Tier 3 NBomber, memory footprint на 10k events/sec — не нужны, пока нет hot path'а. Tier 1 + Tier 2 — достаточно для начала.

---

## Baseline

Актуальные числа — в [`perf-baseline.md`](perf-baseline.md) (обновляется при значимых изменениях; сравнение с предыдущим запуском = regression check).

---

## CI

**Не забыть**: когда-нибудь настроить — Tier 1 и Tier 2 BDN в PR-gate'е со сравнением с baseline'ами (регрессия >10% → fail), Tier 3 NBomber + memory footprint в nightly, GitHub Action `benchmark-action/github-action-benchmark` для визуализации истории. Пока — всё локально, руками.
