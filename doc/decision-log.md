# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

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
