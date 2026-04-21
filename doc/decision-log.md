# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

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
