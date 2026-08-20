# DotNetApiPi

A .NET 9 Web API scaffolded with **Clean Architecture**, **Domain-Driven Design (DDD)**,
and clean-coding principles. The domain is intentionally **generic / domain-agnostic** —
a `Resource` aggregate — so the scaffold demonstrates good structure, invariants, and
lifecycle transitions without being tied to a specific business domain.

## Highlights

- **Clean Architecture layering** — dependencies flow inward only
  (`Api → Application → Domain`; `Infrastructure → Application + Domain`).
- **DDD** — value objects, an aggregate root, invariants, and domain events.
- **CQRS-style use cases** — explicit command/query handlers (no MediatR dependency).
- **Dependency-light** — EF Core + SQLite or the MongoDB driver; no MediatR /
  FluentValidation.
- **Pluggable storage** — the same `IResourceRepository` contract is
  implemented twice (EF Core/SQLite and MongoDB); switch with one config value.
- **Operational concerns** — `/health` endpoint, `X-Correlation-Id` request
  correlation, RFC 7807 `problem+json` errors, and a global fixed-window rate
  limiter (disabled in Development).
- **Docker** — multi-stage `Dockerfile` (runs as the unprivileged `app` user,
  `curl`-based healthcheck) + `docker-compose.yml` (API + MongoDB).
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
├── DotNetApiPi.Application    # Use cases (CQRS commands/queries), DTOs, mapping, IDomainEventDispatcher
├── DotNetApiPi.Infrastructure # EF Core (SQLite) + MongoDB repositories, initializer, event dispatcher
└── DotNetApiPi.Api           # ASP.NET controllers, middleware, Program.cs wiring
```

| Layer | Responsibility | Knows about |
|-------|----------------|-------------|
| **Domain** | Entities, value objects, invariants, domain events | nothing else |
| **Application** | Use cases (CQRS), DTOs, mapping, `IDomainEventDispatcher` | Domain |
| **Infrastructure** | Persistence (EF Core / MongoDB), initializer, dispatcher | Domain + Application |
| **Api** | Controllers, middleware, DI wiring | Application + Infrastructure |

The **repository interface lives in the Domain layer** (dependency inversion): the
domain declares `IResourceRepository`, and the infrastructure provides the
implementation — EF Core + SQLite by default, or MongoDB through the driver
(selected at runtime from configuration).

### Domain model (the generic aggregate)

- **`Resource`** — aggregate root. Private constructor + a `Create` factory; a
  `Draft → Active → Archived` state machine with invariants enforced by the
  aggregate itself (`Activate`/`Archive` throw `DomainException` on illegal
  transitions; validation caps: name ≤ 256 chars, description ≤ 2048 chars,
  ≤ 50 tags of ≤ 64 chars each).
- **`ResourceName`** — value object; blank/over-long input throws
  `DomainInputException` (mapped to HTTP 400).
- **`ResourceTag`** — value object; lower-cased/trimmed token, `IEquatable`
  value equality.
- **`ResourceStatus`** — enum (`Draft`, `Active`, `Archived`). Serialized on
  the wire as a stable **string** (e.g. `"Draft"`), never as a number.
- **Domain events** — e.g. `ResourceCreatedEvent` (carries the occurrence
  timestamp, injectable via `TimeProvider` for tests), dispatched after
  `SaveChanges` by the unit of work.

### Exceptions

| Exception | Meaning | HTTP status |
|-----------|---------|-------------|
| `DomainInputException` | Invalid client input (blank name, over-long fields) | 400 |
| `ResourceNotFoundException` | Unknown resource id | 404 |
| `DomainException` | Illegal state transition / invariant conflict | 409 |
| anything else | Unexpected failure (logged with full exception + correlation id) | 500 |

All error responses are RFC 7807 `application/problem+json` with stable
machine-readable `type` URIs (`https://dotnet-api-pi.example/errors/…`).

## Run

Local (SQLite, the default):

```bash
dotnet build
dotnet run --project src/DotNetApiPi.Api
```

The API listens on the port defined in
`src/DotNetApiPi.Api/Properties/launchSettings.json` (default
`http://localhost:5181`). On startup it creates the SQLite database
(`dotnet_api_pi.db`) and, in the Development environment, exposes Swagger at
`/swagger`. The rate limiter is disabled in Development.

### Tests

```bash
dotnet test
```

| Project | What it covers |
|---------|----------------|
| `DotNetApiPi.Domain.Tests` | Value objects, aggregate invariants, state machine, domain events |
| `DotNetApiPi.Application.Tests` | Command/query handlers, error mapping |
| `DotNetApiPi.Infrastructure.Tests` | Repository round-trips + unit-of-work + event dispatch against a real in-memory SQLite `ApiDbContext`; Mongo document mapping |
| `DotNetApiPi.Api.Integration.Tests` | Full pipeline over an in-process test server: endpoints, problem+json, correlation ids, rate limiting, `/health` |

Integration tests run the real `Program` composition root against a private
per-test SQLite file, so they never touch your local `dotnet_api_pi.db`.

### Docker (API + MongoDB)

```bash
docker compose up -d --build
```

| Service | Image | Host port |
|---------|-------|-----------|
| `api`   | built from `src/DotNetApiPi.Api/Dockerfile` (multi-stage, aspnet:9.0, non-root `app` user) | 8090 → 8080 |
| `mongo` | `mongo:8` (data persisted in the `mongo-data` volume) | 27018 → 27017 |

The API container is configured for the **Mongo** storage provider via
`Storage__Provider=mongo` and
`Storage__MongoConnectionString=mongodb://mongo:27017`. Both containers carry
healthchecks (`/health` probe via `curl` for the API; `mongosh ping` for Mongo)
and `docker compose ps` only reports the API as *healthy* once startup,
including persistence initialization, has completed.

> **Security note:** the compose MongoDB runs with **no authentication** and
> exposes a host port — intended for local development only. Do not run it on
> an untrusted network.

Swagger (Development only): `http://localhost:8090/swagger`; the Mongo shell:
`mongosh --port 27018`. Host ports are mapped to 8090/27018 because 8080 and
27017 are commonly taken locally — adjust the `ports:` entries in
`docker-compose.yml` if needed. Stop with `docker compose down` (add `-v` to
also delete the Mongo data).

### Storage providers

The persistence backend is selected from the `Storage` configuration section
(`appsettings.json`, overridable with `Storage__*` environment variables):

| Key | Default | Description |
|-----|---------|-------------|
| `Storage:Provider` | `sqlite` | `sqlite` or `mongo` |
| `Storage:SqliteConnectionString` | `Data Source=dotnet_api_pi.db` | Used by the SQLite provider |
| `Storage:MongoConnectionString` | `mongodb://localhost:27017` | Used by the Mongo provider |
| `Storage:MongoDatabaseName` | `dotnet_api_pi` | Mongo database name |

Both providers implement the same `IResourceRepository` contract, so the API
behaviour is identical; only the storage engine changes. (The API's local
development profile still defaults to SQLite, so `dotnet run` works with no
external services.)

## Endpoints

| Method | Route | Description |
|--------|-------|-------------|
| GET    | `/api/resources` | List all resources |
| GET    | `/api/resources/{id}` | Get a resource by id (404 if missing) |
| POST   | `/api/resources` | Create a resource (201 + `Location`) |
| PUT    | `/api/resources/{id}` | Update a resource |
| POST   | `/api/resources/{id}/activate` | Move `Draft → Active` |
| POST   | `/api/resources/{id}/archive` | Move `Active → Archived` |
| DELETE | `/api/resources/{id}` | Delete a resource (204) |
| GET    | `/health` | Liveness probe (always 200 once the process is up) |

### Cross-cutting response headers

- **`X-Correlation-Id`** — present on every response. Echoed from the request
  if you send one; otherwise generated. It is also included in every log line
  for that request (including failures), so support issues can be traced
  server-side.
- **Rate limiting** — outside Development, a global fixed-window limiter
  (100 requests/minute) returns RFC 7807 `429` with a `Retry-After` header
  when exhausted.

### Sample

```bash
BASE=http://localhost:5181

# Create (201; body includes the stable string "status")
curl -s -X POST "$BASE/api/resources" -H "Content-Type: application/json" \
  -d '{"name":"Alpha","description":"first","tags":["red","green"]}'

# Activate, then archive (illegal transitions return 409 problem+json)
curl -s -X POST "$BASE/api/resources/{id}/activate"
curl -s -X POST "$BASE/api/resources/{id}/archive"

# Health
curl -s "$BASE/health"
```
