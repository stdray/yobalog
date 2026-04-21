# AGENTS.md

Guidance for coding agents working in this repository.

## Build entry points

- **Local:** `./build.sh --target=Test` (bash) or `pwsh ./build.ps1 -Target Test` (PowerShell). Bootstrap restores Cake + GitVersion tools, then runs the Cake task.
- **Cake tasks:** Clean → Restore → Version (GitVersion) → Build → Test → Docker → DockerSmoke → DockerPush. Standalone tasks: `E2ETest` (Playwright, needs `pwsh bin/.../playwright.ps1 install chromium` first — ~200MB kept off fast lane) and `Dev` (watcher loop, see below). Locally most useful: `--target=Test` (unit only, no Docker), `--target=E2ETest` (browser-backed), `--target=Docker` (builds image), `--target=DockerPush --dockerPush=true` (requires `GHCR_USERNAME` + `GHCR_TOKEN` env or prior `docker login`).
- **Dev loop:** `./build.sh --target=Dev` / `pwsh ./build.ps1 -Target Dev` runs `bun run dev` (ts + css watchers via concurrently) and `dotnet watch` in parallel, both streaming to the current terminal. Ctrl+C kills both process trees. Replaces the older two-window `run_dev.ps1`.
- **CI:** `test` and `e2e` jobs run in parallel on every push/PR; both must pass before `publish` (Docker build + smoke + push to ghcr.io) runs on main pushes and the `deploy` tag. `e2e` uploads `tests/YobaLog.E2ETests/bin/**/artifacts/*.zip` on failure so Playwright traces are inspectable without re-running locally.
- **Deploy:** **manual tag `deploy` only**. `git tag deploy && git push origin deploy --force` (force needed because the tag gets re-used). Main-push publishes the image but does NOT SSH-deploy — prevents accidental prod updates on every merge. Deploy job needs secrets `DEPLOY_HOST`, `DEPLOY_USERNAME`, `DEPLOY_PASSWORD`, `GHCR_DEPLOY_USERNAME`, `GHCR_DEPLOY_TOKEN`, `YOBALOG_ADMIN_USERNAME`, `YOBALOG_ADMIN_PASSWORD`.

## Status: pre-code, spec-stage

There is no code yet — only design docs under `doc/`. No `.csproj`, no `package.json`, no build/test/lint commands. The first implementation step is the Phase A skeleton described in `doc/plan.md`.

## Documents — what goes where

The design is split across three files; keep them that way:

- **`doc/spec.md`** — the specification. §1–§9 cover backend, integrations, query engine, UI, frontend build, storage layout, query-layer invariants, self-observability, and localization. §10 is the `ILogStore` contract. No progress, no checkboxes, no task tracking.
- **`doc/plan.md`** — phases A–E, dual-executor test strategy, pre-Phase-A test coverage, and open questions. This is where checkboxes and progress live.
- **`doc/decision-log.md`** — every architectural decision with date / decision / reason / what was rolled back. **Newest entries go on top.** When you make or propose an architectural change, add an entry here; don't bury the reasoning in a commit message.
- **`doc/tech-debt.md`** — живой реестр техдолга. Полные аудиты оформляются секциями `## Аудит N — YYYY-MM-DD`, закрытые пункты зачёркиваются с ссылкой на commit. Новые долги, замеченные при работе — дописываются в соответствующую секцию сразу, не копятся в голове.

When editing: spec changes go to `spec.md`, progress updates to `plan.md`, and any decision that changes direction gets a new `decision-log.md` entry.

## Target stack (planned, not yet scaffolded)

- .NET 10 monolith, Razor Pages SSR + htmx (+ jQuery, optional Alpine.js).
- SQLite + FTS5 via `linq2db` as the first `ILogStore` backend. DuckDB later (blocked on linq2db#5451).
- KQL as the query language: parser = `Microsoft.Azure.Kusto.Language`, in-memory reference executor = `kusto-loco` (`KustoQueryContext`). No custom parser, no custom AST.
- Frontend build: TypeScript + Tailwind via `bun` (not npm+node). `package.json` lives next to `.csproj`; Release builds invoke `bun run build` from an MSBuild target.

## Hard invariants (easy to violate — read before coding)

- **Seq-compatible ingestion.** Endpoint `POST /api/events/raw`, CLEF (NDJSON), API keys via `X-Seq-ApiKey` header or `?apiKey=`. Don't diverge from Seq semantics unless the spec already does (only query language does).
- **`ILogStore` takes `KustoCode` AST, not strings.** Query, Count, and `DeleteAsync(predicate)` all accept the parsed Kusto AST. Don't add SQL/string overloads — backends translate the AST themselves.
- **Cursor pagination only.** Composite `(@t, Id)` cursor, opaque bytes in `QueryOptions.Cursor`. No offset pagination on event tables. No `.ToList()` before filtering. No load-all → LINQ filter (explicit antipattern — see `spec.md` §7).
- **All filters translate to native backend queries**, never to in-memory LINQ. Full-text on `Message` must translate to SQLite FTS5 MATCH.
- **Dual-executor property tests are mandatory** for the query engine: every KQL string runs through `kusto-loco`'s `KustoQueryContext` (reference) and the production executor; results must match.
- **No YobaLog → YobaConf dependency.** Configuration is `appsettings.json` only. YobaConf will log into YobaLog, so the reverse dependency creates a cycle.
- **`$system` workspace for self-observability.** Internal categories (`YobaLog.Ingestion.*`, `YobaLog.Query.*`, `YobaLog.Retention.*`) must be filtered in the logger provider so they never route into user workspaces.
- **Workspace IDs** match `^[a-z0-9][a-z0-9-]{1,39}$`. `$`-prefix reserved for system workspaces. No renaming in v1.
- **Properties field is unindexed by default.** Per-workspace allowlist via `ILogStore.DeclareIndexAsync` is the only way to index a path. Non-indexed filters are allowed but must surface a "full scan" warning in the UI.
- **Localization from day one.** All user-facing strings go through `IStringLocalizer`. No hardcoded strings in Razor/code. While the i18n scaffold isn't built, **all user-facing strings are literal English ASCII** — the CI has a non-ASCII check over `ts/` and `Pages/` that fails the build on Cyrillic or other non-ASCII chars in those files. Comments in `.cs` under `src/YobaLog.Core` and `tests/` are exempt.
- **UI test selectors: `data-testid` required, text-matching forbidden on chrome.** Display strings are localization targets (§9 above) — a test that matches them breaks on first translation. Same for CSS classes (styling concern, subject to DaisyUI/Tailwind refactors) and accessible-name roles (`GetByRole(Name = "Apply")` also reads the localized label). Rules for UI tests (Playwright .NET):
    - Every element a test interacts with or asserts on gets a stable `data-testid="<kebab-slug>"` in the Razor markup. Slugs are English, domain-specific, stable across locales (`kql-apply`, `events-row`, `api-key-reveal`).
    - Locate via `page.GetByTestId(…)`. **Forbidden** for chrome: `GetByText`, `GetByRole(Name = …)`, `GetByPlaceholder`, CSS class selectors (`.btn-primary`, `.alert-error`) — all re-couple tests to display strings or styling.
    - `HasText = …` allowed **only** inside a testid-scoped locator, and **only** for asserting user-generated data content (event messages, saved query names, API-key titles) — never UI chrome.
    - ARIA roles without a name filter (`GetByRole(AriaRole.Table)`) acceptable when unambiguous on the page, but testid preferred.
- **No HTML / UI templates in `.cs` files.** Razor (`.cshtml` partials + `IRazorPartialRenderer`) owns all markup. Building HTML in a `StringBuilder` from a `.cs` service makes classes invisible to Tailwind's JIT scan (purge drops them silently — we hit this twice on `shadow-lg`/`z-20` in the old KQL completions renderer). If an endpoint needs to return HTML, route it through a Razor partial.
- **Frontend build is Release-only.** Debug doesn't invoke `bun` from MSBuild — use `./build.sh --target=Dev` (or a manual `bun run dev`) for the watcher loop. This keeps `dotnet watch` and `bun --watch` from racing on `wwwroot/` output and avoids Rider's lingering Web process blocking the bun-build output-copy step.

## Coding style

- **Immutability and functional approach by default**, both backend and frontend. In C#: `record`/`readonly record struct` over classes, `init`-only properties, `ImmutableArray<T>`/`IReadOnlyList<T>` over `List<T>` in APIs, pure functions over stateful services where practical, `switch` expressions over mutation. In TypeScript: `const` everywhere, `readonly` on types, `ReadonlyArray<T>`, spread/map/filter over `push`/splice. Mutation is allowed only where it's load-bearing (hot paths in ingestion, Channels-based writer loop) — and must be local, not leaked through APIs.
- **Arrow/expression-bodied style when it fits.** C#: expression-bodied members (`=>`) for methods/properties/ctors that are a single expression (`public int Count => items.Length;`, `public static Foo Parse(string s) => TryParse(s, out var f) ? f : throw ...;`); use `switch` expressions over `switch` statements. TypeScript: arrow functions for callbacks and module-level helpers (`const add = (a: number, b: number) => a + b;`); reserve `function` for cases that need hoisting or `this` binding. Don't force arrow style when the body legitimately has multiple statements — readability wins over uniformity.
- **Omit implicit access modifiers.** Don't write the language default: `internal` on top-level types, `private` on class/struct members and constructors, `public` on interface members. `class Foo` instead of `internal class Foo`; `string _name;` instead of `private string _name;`; constructors in a `public` record struct stay unmarked when private is intended. Always write `public`/`protected`/`internal` when they differ from the default. Same principle for TypeScript: don't write the default (`public` on class members is implicit — omit it).
- **Maximum static typing — no escape hatches.** C#: no `object`, no `dynamic`; use generics, discriminated-union-style `record` hierarchies, or `OneOf<>`-style types. The only place `JsonElement`/`JsonNode` is acceptable is the `Properties` dynamic bag at the storage boundary — and it must be parsed into typed shapes before flowing deeper. TypeScript: `strict: true`, no `any`, no `unknown` in public APIs (use it only at runtime-validation boundaries — e.g. parsing network responses — and narrow immediately via a type guard or schema). Prefer discriminated unions and branded types over string primitives for IDs (`WorkspaceId`, `TraceId`).
- **Formatting:** indent with **tabs**, rendered as **2 spaces wide**. Enforced via `.editorconfig` at the repo root — don't override per-editor. Final newline, UTF-8, trimmed trailing whitespace. LF line endings in source files (`.gitattributes` will normalize).

## Working style for this repo

- When the user asks about "next step" or "what to do," check `doc/plan.md` — phases are the source of truth for ordering.
- Russian is fine in conversation and in decision-log entries; user-facing code strings are English ASCII (see spec §9).
- This is a greenfield project — don't add backwards-compatibility shims, feature flags, or `// TODO: remove` comments. The spec and decision log are how we remember why things exist.
- **Plan update goes in the same commit as the feature.** When you complete or meaningfully shift a phase / bullet from `doc/plan.md`, update the file in the same commit. A `.githooks/pre-commit` hook warns when `src/**` changes without a corresponding `doc/plan.md` diff — enable hooks once per clone:

  ```
  git config core.hooksPath .githooks
  ```

## Commit convention (synchronised with yobaconf)

Both repos follow Conventional Commits for consistency.

- Subject: type(scope): short description, ≤ 72 chars, imperative, no period.
    - Types in use: feat, fix, refactor, test, style, docs, chore, build.
    - Scopes (indicative): core, web, kql, admin, e2e, css, schema, bootstrap, docs, deps.
- Body (markdown): bolded section headers when a commit touches multiple concerns. Explain why + tricky bits, not what the diff already shows. End with test-totals footer when tests were run: Totals: N unit + M E2E = X green, R/R stable runs. Co-Authored-By footer when AI-assisted.
- ASCII-only in commit bodies.
