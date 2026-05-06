# План: Агентно-ориентированные доработки YobaLog

## Контекст

Агенты (claude code, opencode, pi-mono, etc.) получают выделенный API-ключ и через него:
- Создают workspace под задачу расследования (lazy, при первом ingest)
- Пишут структурированные логи через CLEF
- Читают логи через KQL → JSON (машинный API)
- Делятся интерактивными KQL-ссылками с человеком

---

## 1. Wildcard-ключ с creation window

### Модель данных

```
ApiKeyRecord (существующая таблица ApiKeys, новые колонки):
  IsWildcard         INTEGER NOT NULL DEFAULT 0
  CanCreate          INTEGER NOT NULL DEFAULT 0
  CreateWindowHours  INTEGER NOT NULL DEFAULT 0
```

| Поле | Значение | Смысл |
|------|----------|-------|
| `IsWildcard` | 1 | Ключ не привязан к одному workspace |
| `CanCreate` | 1 | Разрешено ленивое создание workspace |
| `CreateWindowHours` | 4 | Окно создания от `CreatedAt` ключа, часов |

**Жизненный цикл:**
1. Админ создаёт ключ: `IsWildcard=true`, `CanCreate=true`, `CreateWindowHours=4`
2. До `CreatedAt + 4h` — агент может создавать workspace через ingest
3. После `CreatedAt + 4h` — создание запрещено (403), но read/write в существующие workspace работает
4. Read (query) и write (ingest) работают всегда, независимо от окна

### ApiKeyValidation

```csharp
public sealed record ApiKeyValidation(
    bool IsValid,
    bool IsWildcard,        // ← новое
    bool CanCreate,         // ← новое
    DateTimeOffset? CreateDeadline,  // ← null если CanCreate=false или окно не задано
    WorkspaceId? Scope,
    string? Reason)
{
    public static ApiKeyValidation Valid(WorkspaceId scope) => new(true, false, false, null, scope, null);
    public static ApiKeyValidation Wildcard(bool canCreate, DateTimeOffset? deadline) =>
        new(true, true, canCreate, deadline, null, null);
    public static ApiKeyValidation Invalid(string reason) => new(false, false, false, null, null, reason);
}
```

### Файлы

| Файл | Что |
|------|-----|
| `Core/Auth/ApiKeyValidation.cs` | Добавить `IsWildcard`, `CanCreate`, `CreateDeadline`, factory `Wildcard()` |
| `Core/Auth/ApiKeyOptions.cs` | Конфиг: `"Workspace": "*"` → wildcard в `ConfigApiKeyStore` |
| `Core/Auth/ConfigApiKeyStore.cs` | `Workspace == "*"` → `ApiKeyValidation.Wildcard()` |
| `Core/Auth/Sqlite/ApiKeyRecord.cs` | `IsWildcard`, `CanCreate`, `CreateWindowHours` поля |
| `Core/Auth/Sqlite/SqliteApiKeyStore.cs` | Чтение/запись новых полей, rebuild кеша, create-deadline проверка |
| `Core/Auth/Sqlite/SqliteApiKeySchema.cs` | `ALTER TABLE ApiKeys ADD COLUMN ... IF NOT EXISTS` ×3 |
| `Web/Pages/Admin/ApiKeys.cshtml` | UI: чекбоксы Wildcard / CanCreate, поле CreateWindowHours |
| `Web/ts/admin.ts` | JS: отправка `isWildcard`, `canCreate`, `createWindowHours` |

---

## 2. Workspace metadata

### Модель данных

```
WorkspaceRecord (существующая таблица Workspaces, новые колонки):
  Description  TEXT NOT NULL DEFAULT ''
  Agent        TEXT NOT NULL DEFAULT ''
  GroupName    TEXT NOT NULL DEFAULT ''
```

| Поле | Источник | Обязательность |
|------|----------|:---:|
| `Description` | `?description=` в ingest-запросе | Да (400 если нет при создании) |
| `Agent` | `Title` поля ключа (`ApiKeyRecord.Title`) | Автоматически |
| `GroupName` | `?group=` в ingest-запросе, fallback = `Agent` | Нет |

### WorkspaceInfo

```csharp
public sealed record WorkspaceInfo(
    WorkspaceId Id,
    DateTimeOffset CreatedAt,
    string Description,
    string Agent,
    string GroupName);
```

### GetOrCreateAsync

```csharp
// SqliteWorkspaceStore
async ValueTask<WorkspaceInfo> GetOrCreateAsync(
    WorkspaceId id,
    string description,
    string agent,
    string groupName,
    CancellationToken ct)
{
    // 1. Пробуем прочитать существующий
    var existing = await GetAsync(id, ct);
    if (existing is not null)
        return existing;

    // 2. Создаём — атомарно, ловим SQLITE_CONSTRAINT при гонке
    try
    {
        return await CreateAsync(id, description, agent, groupName, ct);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
    {
        return (await GetAsync(id, ct))!;
    }
}
```

### Файлы

| Файл | Что |
|------|-----|
| `Core/Admin/WorkspaceInfo.cs` | `Description`, `Agent`, `GroupName` поля |
| `Core/Admin/WorkspaceRecord.cs` | `Description`, `Agent`, `GroupName` колонки |
| `Core/Admin/Sqlite/SqliteWorkspaceStore.cs` | `GetOrCreateAsync`, обновлённый `CreateAsync` |
| `Core/Admin/Sqlite/SqliteAdminSchema.cs` | `ALTER TABLE Workspaces ADD COLUMN ...` ×3 |

---

## 3. `?workspace=` в ingest + creation-window check

### Ingest handler

```
POST /api/v1/ingest/clef?workspace=nemerle-macrophase&description=debugging+MacroPhase
X-Seq-ApiKey: <wildcard-key>
```

Логика `ResolveScopeAsync` (обновлённая):

```csharp
static async Task<(WorkspaceId? Scope, string? Error)> ResolveScopeAsync(
    HttpContext ctx, IApiKeyStore apiKeys, IWorkspaceStore workspaces, CancellationToken ct)
{
    var token = ctx.Request.Headers["X-Seq-ApiKey"].FirstOrDefault()
        ?? ctx.Request.Query["apiKey"].FirstOrDefault();

    var validation = await apiKeys.ValidateAsync(token, ct);

    if (!validation.IsValid)
        return (null, "invalid api key");

    if (!validation.IsWildcard)
        return (validation.Scope, null);   // старый путь без изменений

    // Wildcard: читаем workspace из query
    var wsParam = ctx.Request.Query["workspace"].FirstOrDefault();
    if (string.IsNullOrEmpty(wsParam))
        return (null, "wildcard key requires ?workspace=");

    if (!WorkspaceId.TryParse(wsParam, out var ws))
        return (null, $"invalid workspace name: {wsParam}");

    // Проверяем — существует ли уже?
    var existing = await workspaces.GetAsync(ws, ct);
    if (existing is not null)
        return (existing.Id, null);

    // Не существует — можно ли создать?
    if (!validation.CanCreate)
        return (null, "workspace not found and key cannot create");

    if (validation.CreateDeadline is { } deadline && DateTimeOffset.UtcNow > deadline)
        return (null, "creation window expired; use existing workspace");

    // Создаём
    var description = ctx.Request.Query["description"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(description))
        return (null, "description required for workspace creation");

    var groupName = ctx.Request.Query["group"].FirstOrDefault() ?? agentFromKey;
    var info = await workspaces.GetOrCreateAsync(ws, description, agentFromKey, groupName, ct);
    return (info.Id, null);
}
```

### Файлы

| Файл | Что |
|------|-----|
| `Web/IngestionHandlers.cs` | Обновлённая `ResolveScopeAsync`, проброс workspace-параметров |

---

## 4. GET/POST `/api/v1/query` — KQL → JSON

### Endpoint

```
GET /api/v1/query?workspace=nemerle-lsp&kql=events+|+where+Level+>=+3&cursor=....
X-Seq-ApiKey: <key>
→ 200 JSON
```

```
POST /api/v1/query
Content-Type: application/json
{"workspace":"nemerle-lsp","kql":"events | where Level >= 3","cursor":"..."}
→ 200 JSON
```

### Response (event-shaped запрос: без project/extend/summarize)

```json
{
  "columns": ["Timestamp","Level","LevelName","Message","MessageTemplate","Exception","TraceId","SpanId","EventId","Properties"],
  "rows": [
    ["2026-05-06T12:00:00.000Z","Information","Info","LookupSymbol found: 3","LookupSymbol found: {Count}","","trace-abc","span-1",null,{"SourceContext":"GlobalEnv","SymbolId":"MacroPhase","Count":3}]
  ],
  "cursor": "AAECAwQFBgcICQoLDA0ODw==",
  "truncated": false
}
```

### Response (shape-changing запрос: project/extend/summarize)

```json
{
  "columns": ["Timestamp","Message","Count"],
  "rows": [
    ["2026-05-06T12:00:00.000Z","LookupSymbol found: 3",3]
  ],
  "cursor": null,
  "truncated": false
}
```

Columns и rows зависят от KQL — `project`/`extend`/`summarize` меняют форму. Cursor = null для shape-changing (нет пагинации).

**Формат:**
- `columns` — список имён колонок, зависит от KQL
- `rows[i][j]` — `null` для SQL NULL, строка для текста, число для чисел, ISO-8601 для Timestamp, JSON-объект для `Properties`
- `Properties` — всегда есть в event-shaped ответах, значение — JSON-объект всех кастомных свойств события
- `cursor` — непрозрачный base64, агент не парсит, передаёт как есть для следующей страницы (`null` = больше нет)
- `truncated` — true если `take` обрезал результат

**Ограничения:**
- Все реализованные операторы KQL работают (`where`, `order by`, `take`, `project`, `extend`, `summarize`, `count`)
- `take` ограничен `MaxRows` из `ShareOptions` (10k default)

### Файлы

| Файл | Что |
|------|-----|
| `Web/YobaLogApp.cs` | `MapGet` + `MapPost` `/api/v1/query` |
| `Web/QueryResponse.cs` | `record QueryResponse(ImmutableArray<string> Columns, ImmutableArray<ImmutableArray<JsonElement?>> Rows, string? Cursor, bool Truncated)` |
| `Web/QueryRequest.cs` | `record QueryRequest(string Workspace, string Kql, string? Cursor)` |

---

## 5. Интерактивные KQL share-ссылки

### Модель

```csharp
public sealed record KqlShareLink(
    string Id,              // ShortGuid
    WorkspaceId Workspace,  // какой workspace
    string Kql,             // KQL запрос
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
```

Хранится в `$system.meta.db` (кросс-workspace).

### SQLite schema

```sql
CREATE TABLE IF NOT EXISTS KqlShareLinks (
    Id          TEXT PRIMARY KEY,
    Workspace   TEXT NOT NULL,
    Kql         TEXT NOT NULL,
    CreatedAtMs INTEGER NOT NULL,
    ExpiresAtMs INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_kqlsharelinks_expires ON KqlShareLinks(ExpiresAtMs);
```

### Endpoints

| Endpoint | Auth | Что |
|----------|------|-----|
| `POST /api/v1/share` | API key | Создать KQL-share `{ "workspace": "slug", "kql": "...", "ttlHours": 24 }` → `{ "url": "https://host/share/kql/abc123" }` |
| `GET /share/kql/{id}` | anonymous | Razor-страница: KQL textarea (editable) + таблица событий |
| `GET /share/kql/{id}/rows?kql=...&cursor=...` | anonymous | htmx-фрагмент `_RowsFragment` для infinite scroll |
| `POST /api/ws/{id}/share?type=kql` | cookie | Создать из UI (рядом с текущей Share TSV кнопкой) |

### Share-страница (`ShareKql.cshtml`)

Минимальная Razor Page:
- KQL textarea с предзаполненным запросом — **редактируемая**: человек меняет фильтры, операторы, порядок, исследует дальше. Все реализованные операторы KQL доступны (`where`, `order by`, `take`, `project`, `extend`, `summarize`, `count`).
- Таблица событий через `_RowsFragment` partial (переиспользуем)
- Infinite scroll через htmx sentinel (как на `/ws/{id}`)
- Без фильтр-чипов, без live-tail, без админки — только read-only просмотр
- Кнопка "Copy link" для шеринга URL
- При изменении KQL в textarea → htmx-запрос к `/share/kql/{id}/rows?kql=...` → сервер выполняет новый KQL и возвращает `_RowsFragment`

### Файлы

| Файл | Что |
|------|-----|
| `Core/Sharing/KqlShareLink.cs` | Record |
| `Core/Sharing/IKqlShareLinkStore.cs` | Interface |
| `Core/Sharing/Sqlite/SqliteKqlShareLinkStore.cs` | SQLite impl |
| `Core/Sharing/Sqlite/SqliteKqlShareLinkSchema.cs` | DDL |
| `Web/Pages/ShareKql.cshtml` + `.cshtml.cs` | Anonymous Razor Page |
| `Web/YobaLogApp.cs` | share endpoints |
| `Web/ts/admin.ts` | "Share KQL" кнопка |
| `Web/Pages/Workspace.cshtml` | "Share KQL" кнопка |
| `Core/Retention/RetentionService.cs` | Sweep `KqlShareLinks` |
| `Core/Storage/WorkspaceBootstrapper.cs` | Инжекция `IKqlShareLinkStore` |

---

## 6. UI: группировка workspace'ов на главной

### Главная страница (`/`)

Было: плоский список `WorkspaceInfo` карточек.
Станет: сгруппированный по `GroupName`, каждая группа — collapsible section.

### Макет

```
┌─ nemerle-compiler · 2 ──────────────────────┐
│  nemerle-macrophase                          │
│  debugging MacroPhase lookup failure         │
│  2026-05-06 10:05 · 3,200 events             │
│                                               │
│  nemerle-lsp                                  │
│  VSCode server diagnostics                    │
│  2026-05-05 22:10 · 12,100 events             │
└───────────────────────────────────────────────┘

┌─ ci · 1 ─────────────────────────────────────┐
│  ci-10346                                     │
│  Build #10346 regression analysis             │
│  2026-05-06 08:00 · 450 events                │
└───────────────────────────────────────────────┘

┌─ Ungrouped ───────────────────────────────────┐
│  $system                                       │
└───────────────────────────────────────────────┘
```

### Технически

- `WorkspaceInfo` уже содержит `GroupName`
- `Index.cshtml.cs.OnGetAsync` — читает все workspace, группирует по `GroupName`
- `Index.cshtml` — рендерит группы с `<details open>` (collapsible)
- `$system` всегда в "Ungrouped", исключён из счётчиков групп

### Workspace-страница

В заголовке `/ws/{id}` показывать:
```
nemerle-macrophase
debugging MacroPhase.BeforeInheritance lookup failure  ← description
agent: opencode · group: nemerle-compiler               ← meta строка
```

### Файлы

| Файл | Что |
|------|-----|
| `Web/Pages/Index.cshtml.cs` | Группировка по `GroupName` |
| `Web/Pages/Index.cshtml` | Новый layout с группами |
| `Web/Pages/Workspace.cshtml` | Description + Agent + Group в заголовке |

---

## 7. Порядок реализации

```
Блок I:   Wildcard key model + workspace metadata + миграция БД
            ↓
Блок II:  ?workspace= ingestion + GetOrCreateAsync + creation-window check
            ↓
Блок III: GET/POST /api/v1/query (JSON query API)
Блок IV:  Interactive KQL share
Блок V:   UI: папки на главной + description в workspace + admin-форма wildcard
```

Блоки I+II — зависимые (II требует I).
Блоки III, IV, V — независимы друг от друга после I+II.

---

## 8. За пределами scope

| Что | Почему |
|-----|--------|
| Индексы Properties (`DeclareIndexAsync` / allowlist) | Не нужно для агентных расследований |
| ALTER TABLE ADD COLUMN для свойств | `json_extract` + индекс уже работает |
| OTLP traces в share | Отдельная задача, не блокирует агентов |
| Nemerle SDK (`YobaLog.Debug(...)`) | Делается в проекте nemerle, не в yobalog |
| `X-YobaLog-Workspace` заголовок | `?workspace=` в query string покрывает все сценарии |
| Сохранённые запросы для агентов | GET/POST `/api/v1/query` покрывает |
| Кнопка "reset creation window" | Пока нет — админ создаёт новый ключ |

## 9. Тестирование

- **Unit:** `ApiKeyValidation` новые factory methods, `SqliteApiKeyStore` wildcard-валидация + create-deadline, `GetOrCreateAsync` race condition
- **Integration:** wildcard-ingest → 201 + workspace created, повторный ingest → 200, create window expired → 403, без description → 400
- **E2E:** query API JSON round-trip, KQL share ссылка открывается анонимно, главная страница с папками
