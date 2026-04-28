# YobaLog admin API (`/v1/admin/*`)

JSON endpoints for scripting / automation against the YobaLog admin surface. Mirrors the
operations available through the Razor admin UI (`/admin/workspaces`,
`/ws/{id}/admin/api-keys`, `/admin/retention`) so scripts can prepare workspaces, mint
ingest keys, and configure retention without clicking through the UI.

Spec: `doc/spec.md` §3 "Admin". Decision rationale: `doc/decision-log.md` 2026-04-28
"Admin API: personal admin tokens".

## Auth — personal admin tokens

Every `/v1/admin/*` request carries a personal admin token. Tokens are owned by a User
(per-user; multi-token by design — separate token per machine / script). Self-service
CRUD lives at `/admin/profile` in the UI.

Three equivalent transports:

| Transport     | Header / param                  | Notes                                    |
|---------------|---------------------------------|------------------------------------------|
| `Authorization` | `Authorization: Bearer <token>` | HTTP standard; primary.                  |
| Custom header | `X-YobaLog-AdminToken: <token>` | Mirrors `X-Seq-ApiKey` naming.           |
| Query         | `?adminToken=<token>`           | Fallback for curl quick-tests.           |

If both `Authorization: Bearer` **and** `X-YobaLog-AdminToken` arrive with **different
values**, the request is rejected with `400 ambiguous_auth` — the server refuses to guess
which one was intended. Same value in both is accepted.

```bash
export YOBALOG_ENDPOINT="https://yobalog.example.com"
export YOBALOG_ADMIN_TOKEN="<22-char token from /admin/profile>"
alias yl="curl -sSf -H 'Authorization: Bearer '$YOBALOG_ADMIN_TOKEN -H 'Content-Type: application/json'"
```

## Endpoints

### `PUT /v1/admin/workspaces` — create-if-absent

Idempotent on `id`. Returns `201` on create, `200` on no-op.

```json
{ "id": "yobapub" }
```

`id` matches `^[a-z0-9][a-z0-9-]{1,39}$`. The `$`-prefix is reserved for system
workspaces and is rejected with `400 bad_request`.

Response:

```json
{ "id": "yobapub", "createdAt": "2026-04-28T10:15:23Z" }
```

### `GET /v1/admin/workspaces` — list

Returns a JSON array of `{ id, createdAt }`. The `$system` workspace is hidden.

### `GET /v1/admin/workspaces/{id}` — fetch one

`200` with `{ id, createdAt }` or `404 not_found`.

### `DELETE /v1/admin/workspaces/{id}` — drop

Hard-delete: removes the workspace catalog entry, drops `<ws>.meta.db` and the data DB
along with their wal/shm siblings. `204` on success, `404` if absent.

### `PUT /v1/admin/workspaces/{ws}/api-keys` — mint an ingest key

Returns the **plaintext exactly once** — the only chance to capture it.

```json
{ "title": "yobapub-server" }
```

Response (`201`):

```json
{
  "id": "abc...",
  "prefix": "AbcDeF",
  "plaintext": "AbcDeF...22charsXX",
  "title": "yobapub-server",
  "createdAt": "2026-04-28T10:15:24Z"
}
```

### `GET /v1/admin/workspaces/{ws}/api-keys` — list (no plaintexts)

Returns active keys without `plaintext`. Use the `prefix` field for human identification.

### `DELETE /v1/admin/workspaces/{ws}/api-keys/{id}` — soft-delete

`204` / `404`. Soft-deleted keys reject ingestion immediately.

### `GET /v1/admin/workspaces/{ws}/retention` — list policies

Yobalog retention is per-`(workspace, savedQuery)` — a workspace can have multiple
policies, each pinned to a saved query name. Returns a JSON array:

```json
[
  { "savedQuery": "errors-only", "retainDays": 90 },
  { "savedQuery": "noisy-debug", "retainDays": 7 }
]
```

### `PUT /v1/admin/workspaces/{ws}/retention` — upsert one policy

```json
{ "savedQuery": "errors-only", "retainDays": 90 }
```

The saved query must already exist in the workspace (create it via the workspace KQL
form first). `200` with the upserted body on success.

### `DELETE /v1/admin/workspaces/{ws}/retention/{savedQuery}` — drop a policy

`204` / `404`.

## Use cases

### Self-host quickstart — workspace + ingest key + retention via bash

```bash
export YOBALOG_ENDPOINT="https://yobalog.example.com"
export YOBALOG_ADMIN_TOKEN="<22-char token from /admin/profile>"
alias yl="curl -sSf -H 'Authorization: Bearer '$YOBALOG_ADMIN_TOKEN -H 'Content-Type: application/json'"

# Create the workspace for a new consumer.
yl -X PUT "$YOBALOG_ENDPOINT/v1/admin/workspaces" -d '{"id":"yobapub"}'

# Mint an ingest API-key. Plaintext is in `.plaintext` and never returnable again.
NEW=$(yl -X PUT "$YOBALOG_ENDPOINT/v1/admin/workspaces/yobapub/api-keys" \
  -d '{"title":"yobapub-server"}')
INGEST_KEY=$(echo "$NEW" | jq -r .plaintext)

# Retention: a saved query "errors-only" must already exist in the workspace.
yl -X PUT "$YOBALOG_ENDPOINT/v1/admin/workspaces/yobapub/retention" \
  -d '{"savedQuery":"errors-only","retainDays":60}'
```

This is the bash equivalent of clicking through `/admin/workspaces` →
`/ws/yobapub/admin/api-keys` → `/admin/retention` and unblocks zero-touch
`docker compose up` self-host bundles.

### Rotate an ingest API-key without downtime

```bash
WS=yobapub

# 1. Issue a new key.
NEW=$(yl -X PUT "$YOBALOG_ENDPOINT/v1/admin/workspaces/$WS/api-keys" \
  -d '{"title":"yobapub-server (2026-Q2)"}')
NEW_PLAINTEXT=$(echo "$NEW" | jq -r .plaintext)

# 2. Roll the new plaintext into the consumer's runtime config and wait for redeploy.
# 3. Soft-delete the old key.
OLD_ID=...   # find via GET /v1/admin/workspaces/$WS/api-keys
yl -X DELETE "$YOBALOG_ENDPOINT/v1/admin/workspaces/$WS/api-keys/$OLD_ID"
```

## Errors

| Code                       | When                                                                                         |
|----------------------------|----------------------------------------------------------------------------------------------|
| `400 bad_request`          | malformed JSON, invalid workspace id slug, missing required field, non-positive `retainDays` |
| `400 ambiguous_auth`       | `Authorization: Bearer` and `X-YobaLog-AdminToken` carry different values                    |
| `401 unauthorized`         | missing token, unknown token, soft-deleted token                                             |
| `404 not_found`            | workspace / api-key / retention policy / saved query absent                                  |

## Rate limit / lifecycle

No rate limit in MVP (single-owner pet-scale). Tokens have no automatic expiry — revoke
explicitly through `/admin/profile` or by deleting the user (cascade hard-deletes all of
their tokens, decision-log 2026-04-28).
