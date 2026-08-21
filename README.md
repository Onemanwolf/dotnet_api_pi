# DotNetApiPi

A .NET 10 (net10.0) Web API scaffolded with **Clean Architecture**,
**Domain-Driven Design (DDD)**, and clean-coding principles. The domain is
intentionally **generic / domain-agnostic** — a `Resource` aggregate — so the
scaffold demonstrates good structure, invariants, and lifecycle transitions
without being tied to a specific business domain.

## Highlights

- **Clean Architecture layering** — dependencies flow inward only
  (`Api → Application → Domain`; `Infrastructure → Application + Domain`).
- **DDD** — value objects, an aggregate root, invariants, domain events, and a
  versioned aggregate for optimistic concurrency.
- **CQRS-style use cases** — explicit command/query handlers (no MediatR
  dependency).
- **Dependency-light** — EF Core + SQLite or the MongoDB driver; no MediatR /
  FluentValidation.
- **Pluggable storage** — the same `IResourceRepository` contract is
  implemented twice (EF Core/SQLite and MongoDB); switch with one config value.
- **Paged reads** — `GET /api/resources` is bounded (`page` / `pageSize`) with
  totals in response headers.
- **Optimistic concurrency** — single-resource reads carry an `ETag`; every
  mutating request must send `If-Match` (428 if missing, 412 if stale).
- **Unified error contract** — RFC 7807 `application/problem+json` for every
  error, with stable `type` URIs and a `correlationId` member;
  `X-Correlation-Id` on every response.
- **Operational hardening** — `/health` endpoint, per-caller rate limiting
  (disabled in Development), explicit CORS origin allowlist, structured
  request logging, and opt-in OpenTelemetry.
- **Real EF Core migrations** — the SQLite schema is managed by
  `dotnet-ef` migrations applied at startup, not `EnsureCreated`.
- **Transactional outbox** — domain events are written to the same MongoDB
  transaction as the aggregate write (Mongo storage provider only — the
  SQLite/EF path dispatches events in-process, without the outbox), then
  relayed to Kafka (at-least-once; consumers dedupe on the stable
  `x-event-id` header / `eventId` field).
- **Docker** — multi-stage `Dockerfile` on .NET 10 images (runs as the
  unprivileged `app` user, `curl`-based healthcheck) + `docker-compose.yml`
  (API + single-node MongoDB replica set + Apache Kafka 4 + a logging
  consumer for `resource-events`).
- **Central Package Management** — all package versions live in
  `Directory.Packages.props`; a CI gate restores, builds with
  `-warnaserror`, and tests on push/PR (`.github/workflows/ci.yml`).
- **Tested at every layer** — 4 test projects: domain units, application
  handlers, infrastructure (real in-memory SQLite + document mapping), and
  API integration tests over an in-process test server.

## Architecture

```
Api  ->  Application  ->  Domain
                 ^
Infrastructure -+
```

```
src/
├── DotNetApiPi.Domain         # Entities, value objects, invariants, domain events (no dependencies)
├── DotNetApiPi.Application    # Use cases (CQRS commands/queries), DTOs, mapping, event subscriber contract
├── DotNetApiPi.Infrastructure # EF Core (SQLite) + MongoDB repositories, migrations, initializer, event dispatcher
└── DotNetApiPi.Api           # ASP.NET controllers, middleware, Program.cs wiring
```

| Layer | Responsibility | Knows about |
|-------|----------------|-------------|
| **Domain** | Entities, value objects, invariants, domain events | nothing else |
| **Application** | Use cases (CQRS), DTOs, mapping, `IDomainEventDispatcher`, `IDomainEventSubscriber<T>` | Domain |
| **Infrastructure** | Persistence (EF Core / MongoDB), migrations, initializer, event dispatch | Domain + Application |
| **Api** | Controllers, middleware, DI wiring | Application + Infrastructure |

The **repository interface lives in the Domain layer** (dependency inversion):
the domain declares `IResourceRepository`, and the infrastructure provides the
implementation — EF Core + SQLite by default, or MongoDB through the driver
(selected at runtime from configuration).

### Domain model (the generic aggregate)

- **`Resource`** — aggregate root. Private constructor + a `Create` factory; a
  `Draft → Active → Archived` state machine with invariants enforced by the
  aggregate itself (`Activate`/`Archive` throw `DomainException` on illegal
  transitions; **archived resources are immutable** — every mutator is guarded
  and throws `DomainException`, HTTP 409). Validation caps: name ≤ 256 chars,
  description ≤ 2048 chars, ≤ 50 tags of ≤ 64 chars each.
- **`Version`** — the aggregate's optimistic-concurrency token. Starts at 0
  and is incremented by exactly one per actual state change; no-op writes
  (e.g. renaming to the same name) do not bump it, so unchanged resources keep
  a stable `ETag`. The persistence layers use it as a compare-and-swap guard.
- **`ResourceName`** — value object; blank/over-long input throws
  `DomainInputException` (mapped to HTTP 400).
- **`ResourceTag`** — value object; lower-cased/trimmed token, `IEquatable`
  value equality.
- **`ResourceStatus`** — enum (`Draft`, `Active`, `Archived`). Serialized on
  the wire as a stable **string** (e.g. `"Draft"`), never as a number.
- **Domain events** — e.g. `ResourceCreatedEvent` (carries the occurrence
  timestamp, injectable via `TimeProvider` for tests), dispatched after
  `SaveChanges` by the unit of work to
  `IDomainEventSubscriber<TEvent>` subscribers (one built-in:
  `ResourceCreatedEventLogSubscriber`, which logs resource creations).

### Exceptions

| Exception | Meaning | HTTP status |
|-----------|---------|-------------|
| `DomainInputException` | Invalid client input (blank name, over-long fields, out-of-range paging) | 400 |
| — (controller) | Missing `If-Match` → 428; malformed `If-Match` value → 400 (problem+json, checked before any handler runs) | 400/428 |
| `ResourceNotFoundException` | Unknown resource id | 404 |
| `DomainException` | Illegal state transition / invariant conflict (e.g. activating an active resource, mutating an archived one) | 409 |
| `ResourceConcurrencyException` | Optimistic-concurrency conflict: the `If-Match` version no longer matches the stored aggregate | 412 |
| anything else | Unexpected failure (logged with full exception + correlation id) | 500 |

All error responses — including model-binding failures and rate-limit
rejections — are RFC 7807 `application/problem+json` with stable
machine-readable `type` URIs
(`https://dotnet-api-pi.example/errors/…`) and a `correlationId` extension
member. A missing `If-Match` header is rejected before any handler runs, with
428 Precondition Required (also problem+json).

## Run

Local (SQLite, the default):

```bash
dotnet build
dotnet run --project src/DotNetApiPi.Api
```

The API listens on the port defined in
`src/DotNetApiPi.Api/Properties/launchSettings.json` (default
`http://localhost:5181`). On startup it applies any pending EF Core
migrations to the SQLite database (`dotnet_api_pi.db`) and, in the
Development environment, exposes Swagger at `/swagger`. The rate limiter is
disabled in Development.

> **Legacy databases:** a `dotnet_api_pi.db` created before the migration
> switch (via `EnsureCreated`) has no `__EFMigrationsHistory` table and
> cannot be reconciled by `Migrate`. Delete the file (plus its `-wal`/`-shm`
> side files) once; the initializer recreates the schema from the migrations
> on the next start.

### Tests

```bash
dotnet test
```

| Project | What it covers |
|---------|----------------|
| `DotNetApiPi.Domain.Tests` | Value objects, aggregate invariants, state machine, version semantics, domain events |
| `DotNetApiPi.Application.Tests` | Command/query handlers (including expected-version preconditions and paging validation), error mapping |
| `DotNetApiPi.Infrastructure.Tests` | Repository round-trips, paging, unit-of-work, concurrency behaviour + event dispatch against a real in-memory SQLite `ApiDbContext`; Mongo document mapping |
| `DotNetApiPi.Api.Integration.Tests` | Full pipeline over an in-process test server: endpoints, paging, ETag/If-Match flows, problem+json, correlation ids, rate limiting, CORS, `/health` |

Integration tests run the real `Program` composition root against a private
per-test SQLite file, so they never touch your local `dotnet_api_pi.db`.

## Endpoints

| Method | Route | Success | Errors |
|--------|-------|---------|--------|
| GET    | `/api/resources?page={n}&pageSize={n}` | 200 + `X-Total-Count` / `X-Total-Pages` headers | 400 (out-of-range paging) |
| GET    | `/api/resources/{id}` | 200 + `ETag` header | 404 |
| POST   | `/api/resources` | 201 + `Location` | 400 |
| PUT    | `/api/resources/{id}` — requires `If-Match` | 200 + new `ETag` | 400, 404, 412, 428 |
| POST   | `/api/resources/{id}/activate` — requires `If-Match` | 200 + new `ETag` | 400, 404, 409, 412, 428 |
| POST   | `/api/resources/{id}/archive` — requires `If-Match` | 200 + new `ETag` | 400, 404, 409, 412, 428 |
| DELETE | `/api/resources/{id}` — requires `If-Match` | 204 | 400, 404, 412, 428 |
| GET    | `/health` | 200 (exempt from rate limiting) | — |

The list response body is a bare JSON array of resources; paging parameters:
`page` (1-based, default 1) and `pageSize` (default 20, maximum 100). Results
are ordered deterministically by resource identity, so pages do not shift
under concurrent inserts.

### Optimistic concurrency (ETag / If-Match)

- Single-resource responses carry `ETag: "<version>"` (a strong validator
  mirroring the `version` field in the JSON body).
- Every mutating endpoint **requires** the `If-Match` header:
  - missing header → **428** Precondition Required
  - stale version (someone else wrote first) → **412** Precondition Failed
  - malformed value → **400** Bad Request
  - `If-Match: *` is accepted (RFC 7232 wildcard: proceed without a version
    check)
- `version` only bumps on actual state changes, so re-sending an unchanged
  body does not invalidate other clients' ETags.

### Sample

```bash
BASE=http://localhost:5181

# Create (201; body includes the stable string "status" and the "version")
curl -s -X POST "$BASE/api/resources" -H "Content-Type: application/json" \
  -d '{"name":"Alpha","description":"first","tags":["red","green"]}'

# List one page (body: bare array; totals in headers)
curl -s -D- "$BASE/api/resources?page=1&pageSize=20" | grep -iE '^(x-total|HTTP)'

# Read one resource and capture its ETag
ETAG=$(curl -s -D- -o /dev/null "$BASE/api/resources/{id}" | tr -d '\r' \
  | grep -i '^etag:' | cut -d' ' -f2)

# Update with the ETag (200 on success)
curl -s -X PUT "$BASE/api/resources/{id}" -H "Content-Type: application/json" \
  -H "If-Match: $ETAG" -d '{"name":"Alpha 2","description":"second","tags":["blue"]}'

# Stale ETag -> 412 Precondition Failed (problem+json)
curl -s -X PUT "$BASE/api/resources/{id}" -H "Content-Type: application/json" \
  -H "If-Match: $ETAG" -d '{"name":"too late","description":"second","tags":["blue"]}'

# Missing If-Match -> 428 Precondition Required
curl -s -X PUT "$BASE/api/resources/{id}" -H "Content-Type: application/json" \
  -d '{"name":"no etag","description":"second","tags":["blue"]}'

# Activate, then archive (both need If-Match; illegal transitions return
# 409 problem+json)
curl -s -X POST "$BASE/api/resources/{id}/activate" -H "If-Match: $ETAG"
curl -s -X POST "$BASE/api/resources/{id}/archive"  -H "If-Match: $NEW_ETAG"

# Health
curl -s "$BASE/health"
```

## Cross-cutting behaviour

### Error contract (RFC 7807)

Every error — domain/application exceptions, model-binding failures, and
rate-limit rejections — is a single `application/problem+json` shape:

```json
{
  "type": "https://dotnet-api-pi.example/errors/precondition-failed",
  "title": "Precondition failed",
  "status": 412,
  "detail": "…",
  "correlationId": "…"
}
```

`type` is a stable URI per error class (e.g. `…/errors/bad-request`,
`…/errors/not-found`, `…/errors/conflict`, `…/errors/precondition-failed`,
`…/errors/internal-server-error`, `…/errors/too-many-requests`). Model-binding
errors additionally carry an RFC 7807 `errors` extension with per-property
messages. Raw input-formatter exception messages (which embed .NET type names)
are only surfaced in Development; production 400s use the standard wording.

### Correlation ids

`X-Correlation-Id` is present on every response — echoed from the request if
you send one, otherwise generated (32-hex). It is included in every log line
for that request (including failures and error responses), so support issues
can be traced server-side.

### Rate limiting

Outside Development, a fixed-window limiter rejects requests that exceed the
caller's budget with `429` problem+json and a `Retry-After` header (60s).

- **Per caller** — each remote IP gets its own budget (100 requests/minute),
  so one noisy client cannot lock out everyone else; requests without a
  resolvable address share an `anonymous` partition.
- **`/health` is exempt** — the liveness probe must stay reachable while
  limiting is active.
- **Queueing disabled** — once a caller's window budget is exhausted the
  request is rejected immediately (no queue).
- **Disabled in Development** — local tooling and integration tests are not
  throttled.

### CORS and authentication posture

CORS uses an explicit origin allowlist from the `Cors:AllowedOrigins`
configuration section (e.g. env `Cors__AllowedOrigins__0=https://admin.example.com`).
An empty or absent list allows **no** cross-origin requests (same-origin
only); the policy never falls back to a wildcard. For allowed origins, all
methods and headers are permitted.

Authentication is intentionally **absent** by design: this scaffold is not an
internet-facing product yet, and half-built auth middleware would create a
false sense of security. The current abuse mitigations are per-caller rate
limiting and the unified error contract (no stack traces or internal type
names in responses). Add real authentication (e.g. OIDC bearer tokens) before
exposing the API publicly.

### Observability

- **Structured logging** — one structured request log line per request
  (method, path, status, duration) plus the correlation id on every log line.
- **OpenTelemetry** — W3C trace-context propagation is active for every
  request (ASP.NET Core + HttpClient instrumentation). Traces and metrics are
  collected in-process and exported over OTLP only when explicitly enabled:
  set `Otel:Enabled=true` (env `Otel__Enabled=true`) or `OTEL_ENABLED=true`
  to enable; the endpoint comes from `Otel:Exporter:Otlp:Endpoint`
  (env `OTEL_EXPORTER_OTLP_ENDPOINT`), default `http://localhost:4317`.
  Metrics include the standard .NET runtime / ASP.NET Core / HTTP client
  instrumentation plus the outbox relay counters (see “Outbox operations”).
- **Domain events** — the unit of work dispatches aggregate domain events to
  `IDomainEventSubscriber<TEvent>` subscribers after the write commits. The
  built-in `ResourceCreatedEventLogSubscriber` logs resource creations;
  register additional subscribers in DI to receive any event type. Subscriber
  failures are logged and do not fail the committing request.

## Storage providers

The persistence backend is selected from the `Storage` configuration section
(`appsettings.json`, overridable with `Storage__*` environment variables):

| Key | Default | Description |
|-----|---------|-------------|
| `Storage:Provider` | `sqlite` | `sqlite` or `mongo` |
| `Storage:SqliteConnectionString` | `Data Source=dotnet_api_pi.db` | Used by the SQLite provider |
| `Storage:MongoConnectionString` | `mongodb://localhost:27017` | Used by the Mongo provider — include credentials when the server requires them, e.g. `mongodb://user:pass@host:27017?authSource=admin` |
| `Storage:MongoDatabaseName` | `dotnet_api_pi` | Mongo database name |
| `Kafka:BootstrapServers` | *(empty)* | Comma-separated broker list (e.g. `kafka:19092` in compose). When empty, the outbox relay is not started — events stay `Pending` in the outbox collection |
| `Kafka:Topic` | `resource-events` | Topic the outbox relay publishes to |
| `Kafka:MessageTimeoutMs` | `30000` | Broker message timeout: hard deadline (incl. retries) for one produce. Bounds the worst-case per-message publish delay — the claim lease must outlive the batch drain (`ceil(BatchSize / PublishConcurrency)` rounds × this timeout) |
| `Outbox:PollIntervalMs` | `1000` | Relay poll interval |
| `Outbox:BatchSize` | `50` | Max events claimed per poll |
| `Outbox:PublishConcurrency` | `8` | Max in-flight publishes (per-resource ordering preserved) |
| `Outbox:MaxAttempts` | `5` | Publish attempts before an event is marked `Dead` |
| `Outbox:LeaseSeconds` | `240` | Claim lease; expired leases are reclaimable (crash recovery). Must be ≥ the worst-case batch drain (`ceil(BatchSize / PublishConcurrency)` × `Kafka:MessageTimeoutMs` — 75 s at the defaults); if it isn't, the relay logs a warning at startup and a slow broker can cause a concurrent relay to double-publish (at-least-once tolerates the duplicate) |
| `Outbox:BaseRetryDelayMs` | `5000` | Base of the exponential retry backoff |

Both providers implement the same `IResourceRepository` contract, so the API
behaviour is identical; only the storage engine changes. (The API's local
development profile still defaults to SQLite, so `dotnet run` works with no
external services.) The SQLite provider applies EF Core migrations at startup
(see `src/DotNetApiPi.Infrastructure/Migrations`); the Mongo provider creates
its collection lazily on first write.

## Docker (API + MongoDB + Kafka)

```bash
docker compose up -d --build
```

| Service | Image | Host port |
|---------|-------|-----------|
| `api` | built from `src/DotNetApiPi.Api/Dockerfile` (multi-stage on .NET 10 images: `sdk:10.0` → `aspnet:10.0`, non-root `app` user) | 8090 → 8080 |
| `consumer` | built from `src/DotNetApiPi.Consumer/Dockerfile` — consumes `resource-events` and logs every message (content + metadata) to Docker logs | — |
| `mongo` | `mongo:8`, single-node replica set `rs0` **with authentication** (keyFile + root user from `MONGO_USER`/`MONGO_PASSWORD`, defaults `root`/`devpass123`); loopback-only ports | 27018 → 27017 |
| `kafka` | `apache/kafka:4.3.1` (KRaft-only, single broker+controller node; topic `resource-events` pre-created by `kafka-init`) | 29092 → 29092 (external listener for host tooling) |

One-shot helpers: `mongo-keyfile-init` (generates the replica-set keyFile
once into a named volume — an authenticated replica set requires one),
`mongo-init` (runs `rs.initiate` as the root user and waits for a writable
primary; idempotent) and `kafka-data-init` (fixes volume ownership before
the KRaft bootstrap). All services carry healthchecks and ordered
`depends_on`, so the stack converges without manual steps; `docker compose
ps` reports the API *healthy* only once startup — including Mongo index
creation and the outbox relay startup — has completed.

The API container is configured for the **Mongo** storage provider
(`Storage__Provider=mongo`, connection string
`mongodb://root:devpass123@mongo:27017/?replicaSet=rs0&authSource=admin` —
override with `MONGO_USER`/`MONGO_PASSWORD` in a `.env`, see
`.env.example`) and publishes to Kafka
(`Kafka__BootstrapServers=kafka:19092`, topic `resource-events`).

> **Security note:** the dev stack runs an authenticated single-node MongoDB
> replica set (keyFile + root user, development credentials by default) and
> Kafka without SASL, and its ports are exposed on loopback only. This is
> an intentional local-development scaffold. For anything beyond localhost,
> use real secrets (never the defaults), a multi-node replica set, and
> Kafka SASL/SCRAM — or run the API against your own broker.

Event flow: an API write commits the aggregate **and** its domain events to
the `outbox_events` collection in one MongoDB transaction; the outbox relay
claims rows (oldest first, lease-based), publishes to Kafka keyed by resource
id (per-resource ordering), and marks rows `Published` with the resulting
partition/offset. The `consumer` service is a reference consumer (group
`dotnet-api-pi-logger`) that prints every message — topic, partition, offset,
key, headers (`x-event-id`), timestamp and body — as one JSON line:

```bash
docker compose logs -f consumer
```

Swagger (Development only): `http://localhost:8090/swagger`; the Mongo shell
(authenticated): `mongosh --port 27018 -u root -p devpass123 --authenticationDatabase admin`;
Kafka tooling on the host: `kcat -b localhost:29092 -C -t resource-events`.
Host ports are mapped to 8090/27018/29092 because 8080, 27017 and 9092 are
commonly taken locally — adjust the `ports:` entries in `docker-compose.yml`
if needed. Stop with `docker compose down` (add `-v` to also delete the
Mongo/Kafka data).

### Outbox operations (Mongo provider)

The outbox exists **only** for the Mongo storage provider: the SQLite/EF
path has no multi-document transactions, so it dispatches domain events
in-process (log subscriber) and never touches `outbox_events` or Kafka.

- **Published rows age out.** A partial TTL index deletes rows in
  `Published` status 7 days after `publishedAtUtc` (replay/audit horizon);
  `Pending`, `Publishing` and `Dead` rows are never touched by TTL.
- **Dead rows.** After `Outbox:MaxAttempts` failed publishes (default 5,
  exponential backoff) a row becomes `Dead` (`status: 3`, with `lastError`
  set). Nothing else will retry it — replay it on purpose, e.g. when the
  broker is back:

  ```js
  // in mongosh, as the configured user:
  db.outbox_events.updateMany(
    { status: 3 },
    { $set: { status: 0, attempts: 0, claimableAtUtc: new Date() }, $unset: { lastError: "" } })
  // the relay picks the rows up within one poll interval.
  ```

  Replayed events are re-delivered to Kafka; consumers must tolerate this
  (dedupe on `x-event-id` / `eventId` — delivery is at-least-once by design).
- **Metrics** (OpenTelemetry; exported when `Otel:Enabled` is set): the relay
  emits three counters, each tagged by the stable `event.type` wire name —
  `dotnet_api_pi.outbox.published` (events published to Kafka and recorded as
  `Published`), `dotnet_api_pi.outbox.failed_attempts` (failed attempts that
  went back to `Pending` with backoff) and `dotnet_api_pi.outbox.dead` (rows
  that exhausted the retry budget — alert on this one; it is the operator
  action item above). Dead transitions are also logged at `Critical`.
- **Indexes** (created by the initializer): `status_claimableAtUtc`
  (serves the relay's claim query, filter + sort, index-only) and
  `resourceId` (per-resource lookups).

## Versioning & CI

- All projects target `net10.0`; package versions are managed centrally in
  `Directory.Packages.props` (Central Package Management) — individual
  `.csproj` files carry no version attributes.
- The CI gate (`.github/workflows/ci.yml`) runs on push to `main` and on
  pull requests: restore, `dotnet build -c Release --no-restore -warnaserror`,
  `dotnet test -c Release --no-build`.
