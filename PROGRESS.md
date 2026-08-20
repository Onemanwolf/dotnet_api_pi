# Progress — .NET 10 Web API Scaffold (Clean Architecture + DDD)

**Status: audit-hardened.** Two storage providers (EF Core/SQLite and MongoDB)
behind the same `IResourceRepository` contract, containerized via Docker
Compose, paged reads, optimistic concurrency (ETag/If-Match), a unified RFC
7807 error contract, and an independent test suite at every layer. Verify the
current state with `dotnet test` (local) or the CI gate (push/PR) — the docs
no longer assert build results in prose.

## What was built

A domain-agnostic Web API around a generic **`Resource`** aggregate, structured with
Clean Architecture (dependencies flow inward only) and DDD patterns.

```
Api  ->  Application  ->  Domain
                 ^
Infrastructure -+
```

## Layers

### Domain (`DotNetApiPi.Domain`) — no external dependencies
- `Common/BaseEntity.cs` — `BaseEntity<TId>` with `Id`
- `Common/AggregateRoot.cs` — caches domain events, `ClearDomainEvents` via the
  internal `IClearableDomainEvents` (public read through `IHasDomainEvents`)
- `ValueObjects/ValueObject.cs` — base value object (value equality via
  `==`/`!=`, correct hash-code folding, `Members()`)
- `ValueObjects/ResourceName.cs` — non-empty, trimmed, ≤ 256 chars; invalid
  input throws `DomainInputException` (HTTP 400)
- `ValueObjects/ResourceTag.cs` — lower-cased/trimmed token ≤ 64 chars,
  `IEquatable<ResourceTag>`
- `Enums/ResourceStatus.cs` — `Draft=0, Active=1, Archived=2`
- `Events/IDomainEvent.cs`, `Events/ResourceCreatedEvent.cs` (occurrence time
  injectable via `TimeProvider`)
- `Exceptions/DomainException.cs`, `Exceptions/DomainInputException.cs`
  (client-input failures — HTTP 400; state conflicts stay
  `DomainException` — HTTP 409)
- `Entities/Resource.cs` — aggregate root: private ctor + `Create` factory;
  `Rename`, `SetDescription`, `Activate`, `Archive`, `AddTag`, `SetTags`;
  invariants (Draft→Active→Archived, archived resources immutable);
  `Version` optimistic-concurrency token (bumps only on actual state
  changes); `Reconstitute` (internal) restores persisted state incl. version
- `Repositories/IResourceRepository.cs` — repository interface in the Domain
  (dependency inversion), incl. `GetPageAsync(page, pageSize)`

### Application (`DotNetApiPi.Application`) — depends on Domain
- `Common/` — `ICommand`/`IQuery` markers, generic `ICommandHandler`/`IQueryHandler`,
  `IDomainEventDispatcher`, `IDomainEventSubscriber<TEvent>`, `ConcurrencyPreconditions`,
  `Unit`
- `Common/Exceptions/` — `ResourceNotFoundException`,
  `ResourceConcurrencyException` (HTTP 412)
- `Dtos/ResourceDto.cs` (incl. `Version`), `Dtos/PagedResult.cs`,
  `Mapping/ResourceMapper.cs`
- `Commands/` — Create / Update / Activate / Archive / Delete + handlers
  (mutating commands carry an `ExpectedVersion` precondition)
- `Queries/` — GetResourceById / GetAllResources (paged) + handlers
- `DependencyInjection.cs` — `AddApplication()`

### Infrastructure (`DotNetApiPi.Infrastructure`) — depends on Application + Domain
- `Persistence/ApiDbContext.cs` — SQLite `DbContext`; configures `Resource`
  via value converters (`Version` as an EF concurrency token); dispatches
  domain events on save
- `Migrations/` — real EF Core migrations (`InitialCreate`); applied at
  startup by the initializer (replaces `EnsureCreated`)
- `Design/DesignTimeApiDbContextFactory.cs` — `dotnet ef` design-time entry
- `Persistence/PersistenceOptions.cs`, `Persistence/StorageProvider.cs` —
  provider selection + connection settings from the `Storage` section
- `Persistence/Mongo/ResourceDocument.cs`, `ResourceDocumentMapper.cs` —
  Mongo document model + `Reconstitute`-based mapping (Guids serialized via
  `[BsonGuidRepresentation(Standard)]` per driver 3.x)
- `Repositories/ResourceRepository.cs` (EF), `Repositories/MongoResourceRepository.cs`
  (driver) — same unit-of-work semantics; the Mongo UoW processes aggregates
  one at a time (write → dispatch only that aggregate's events), replacements
  are filtered on `Id` **and** `Version` (stale → `ResourceConcurrencyException`);
  the residual non-atomicity across aggregates is documented (multi-document
  transactions would need a replica set)
- `EventHandlers/DomainEventDispatcher.cs` — resolves
  `IDomainEventSubscriber<TEvent>` per event type from DI; subscriber
  failures are logged and do not fail the request
- `EventHandlers/ResourceCreatedEventLogSubscriber.cs` — first real subscriber
  (logs resource creations)
- `IInfrastructureInitializer.cs`, `SqliteInfrastructureInitializer.cs`
  (`MigrateAsync`), `MongoInfrastructureInitializer.cs`
- `DependencyInjection.cs` — `AddInfrastructure(PersistenceOptions)` branches
  on the provider; Mongo client/database/collection are singletons, the
  repository (unit of work) is scoped

### Api (`DotNetApiPi.Api`) — depends on Application + Infrastructure
- `Controllers/ResourceController.cs` — CRUD + `/activate` + `/archive`;
  bounded paging on GET; `ETag`/`If-Match` enforcement (428/412/400)
- `Dtos/` — `CreateResourceRequest`, `UpdateResourceRequest`
- `Middleware/ExceptionHandlingMiddleware.cs` — maps exceptions to the
  unified RFC 7807 contract (400/404/409/412/500), re-attaches
  `X-Correlation-Id` after `Response.Clear()`
- `Middleware/CorrelationIdMiddleware.cs` — echoes/generates
  `X-Correlation-Id`, scopes it into logging, one structured request log line
- `RateLimiting/RateLimitKeys.cs` — per-caller partition key (remote IP,
  `anonymous` fallback)
- `Results/ProblemJsonResult.cs` — writes problem+json directly (model-binding
  + rate-limit rejections follow the same contract)
- `Program.cs` — DI wiring, provider-neutral startup
  (`IInfrastructureInitializer`), Swagger (Development only), CORS allowlist,
  per-caller rate limiting (`/health` exempt, disabled in Development),
  conditional HTTPS redirection, OpenTelemetry (trace context always; OTLP
  export opt-in)

## Key decisions
- **Generic `Resource` aggregate** (domain-agnostic) demonstrating value objects, invariants,
  lifecycle transitions, domain events, and optimistic concurrency.
- **SQLite** so the API runs with no external server; schema managed by real
  EF Core **migrations** applied at startup.
- **CQRS without MediatR** — explicit `ICommandHandler`/`IQueryHandler` resolved via DI.
- **No FluentValidation** — validation lives in value objects / the domain;
  input failures surface as `DomainInputException` (400), state conflicts as
  `DomainException` (409).
- **Repository + event interfaces in Domain / Application** (dependency inversion).
- **Value objects persisted via `ValueConverter`s** so the aggregate maps to a single table.
- **One error contract** — model-binding failures, rate-limit rejections, and
  exceptions all emit the same problem+json shape (stable `type` URIs,
  `correlationId` member, `X-Correlation-Id` header).
- **No authentication by design** (documented posture); mitigations are
  per-caller rate limiting and the non-leaking error contract.

## Audit-driven repair (2026-08-20)

An independent audit of the scaffold produced findings F-01…F-21; the repairs
landed in the following commits (findings addressed, one commit per lane):

| Findings | Commit | What changed |
|----------|--------|--------------|
| F-13, F-15 | `74b0280` | Retargeted all projects to **.NET 10 LTS** (`net10.0`); **Central Package Management** (`Directory.Packages.props`); added **CI gate** (restore / build `-warnaserror` / test on push+PR); `.gitignore` + untracked build artifacts/databases from the index |
| F-04, F-05 | `503e04e` | **Archived resources are immutable** (all mutators guarded → 409); tag blob limit aligned with the persistence column (50 × 64 fits the 4096-char column) with boundary round-trip tests |
| F-02, F-03, F-06, F-07, F-17, F-18 | `4693637` | `/health` **exempt from rate limiting**; rate limiting **partitioned per caller** (per-IP budget, `anonymous` fallback); **correlation id on error responses** (header + `correlationId` problem member); **unified RFC 7807 contract** (model-binding + rate-limit rejections included, stable `type` URIs); input-formatter exception messages suppressed outside Development; dead `ValidationException` removed |
| F-08, F-09, F-12, F-19 | `45fccb3` | **Optimistic concurrency end-to-end** (`Resource.Version`, `ETag`, mandatory `If-Match` → 428/412/400, EF concurrency token + Mongo version-filtered replace); **bounded paging** (`page`/`pageSize`, max 100, totals in headers, deterministic order); Mongo **unit-of-work ordering per aggregate** (write → dispatch, residual-risk documented); **real EF Core migrations** (design-time factory + `dotnet-ef` tool manifest + `MigrateAsync` at startup) |
| F-14, F-20, F-21 | `abd1dfd` | **CORS pinned to an explicit origin allowlist** (empty = same-origin only; auth posture documented); **Mongo credentials** externalized in compose (`MONGO_USER`/`MONGO_PASSWORD`, `.env.example`, authenticated healthcheck); **OpenTelemetry** (trace context always on, OTLP export opt-in) + **real domain-event subscribers** (`IDomainEventSubscriber<T>`, `ResourceCreatedEventLogSubscriber`) |
| F-16 | docs + `2ceab39` | **Docs regenerated from code** (this pass — README/PROGRESS describe the code as it is; no build-status assertions in prose); Dockerfile moved to `sdk:10.0`/`aspnet:10.0` and copies `Directory.Packages.props` so CPM restore works in the image |

Findings confirmed already fixed before the audit runs (test isolation, the
case-sensitive domain assertion, the non-root container) were verified and
left in place.

### Verification entry points (current state)

- `dotnet test -c Release` — the four test projects (unit + integration).
- `docker compose up -d --build` — Mongo-backed stack; then probe
  `/health`, create → `If-Match` update → stale `ETag` (expect 412),
  missing `If-Match` (expect 428), and `X-Total-Count` on the list endpoint.
- CI (`.github/workflows/ci.yml`) — the same restore/build/test gate on every
  push and pull request.

## Notable fixes made during the build (historical)
- `Resource.cs` private ctor field initializer `Name = ResourceName.Of(string.Empty)` threw
  on construction (empty name is invalid) — removed the initializer (the ctor sets `Name`).
- Repository `OrderBy(r => r.Name.Value)` (value object) could not translate to SQL —
  changed to order by `Id`.
- Update handler was *adding* tags (`AddTag`) instead of replacing — added `SetTags` on the
  aggregate and used it in the update handler.
- `ValueObject.GetHashCode` was missing parentheses in the hash fold — fixed the precedence.
- `EntityTypeBuilder`/`IEntityTypeConfiguration` namespace resolution in EF Core — entity
  configuration is inlined into `DbContext.OnModelCreating` via the fluent API.

## MongoDB storage provider + Docker

**Status: COMPLETE** — full CRUD flow verified against the live
MongoDB-backed API in Docker; the Guid serialization issue is resolved using
the approach documented in the official 3.x driver docs (see below).

### What was added

- **Domain** (`DotNetApiPi.Domain`)
  - `Resource.Reconstitute(...)` — `internal` factory so the persistence layer
    can rebuild aggregates from stored state (including the version) without
    bypassing invariants or raising domain events.
  - `AssemblyInfo.cs` — `[assembly: InternalsVisibleTo("DotNetApiPi.Infrastructure")]`
    so only the infrastructure assembly can use the reconstitution factory.
- **Infrastructure** (`DotNetApiPi.Infrastructure`)
  - `MongoDB.Driver` 3.11.0 package (version centralized in
    `Directory.Packages.props`).
  - `Persistence/StorageProvider.cs` — `Sqlite` (default) | `Mongo`.
  - `Persistence/PersistenceOptions.cs` — bound from the `"Storage"` config
    section (provider, both connection strings, Mongo database name).
  - `Persistence/Mongo/ResourceDocument.cs` — Mongo document model (`_id`
    Guid, name, description, status as string, tags as string array, version).
  - `Persistence/Mongo/ResourceDocumentMapper.cs` — document ↔ aggregate
    mapping via `Resource.Reconstitute` (no domain invariants bypassed, no
    events raised on load).
  - `Repositories/MongoResourceRepository.cs` — implements
    `IResourceRepository` with the same unit-of-work semantics as the EF
    implementation: staged aggregates are written one aggregate at a time
    (insert / version-guarded replace / delete) and domain events are
    dispatched per aggregate after its write succeeds.
  - `DependencyInjection.cs` — `AddInfrastructure(PersistenceOptions)`
    branches on the provider. Mongo client/database/collection are singletons
    (thread-safe handles); the repository (unit of work) is scoped.
- **Api** (`DotNetApiPi.Api`)
  - `Program.cs` — storage is selected from the `Storage` config section
    (env vars `Storage__Provider=mongo`, `Storage__MongoConnectionString`,
    `Storage__MongoDatabaseName`); the SQLite initializer applies migrations,
    Mongo creates its collection lazily on first write.
  - `appsettings.json` — `Storage` section with SQLite defaults plus a
    local-Mongo default (`mongodb://localhost:27017`).
- **Docker**
  - `src/DotNetApiPi.Api/Dockerfile` — multi-stage (sdk:10.0 build →
    aspnet:10.0 runtime), copies `Directory.Packages.props` for CPM restore,
    framework-dependent publish, `curl` for the healthcheck, non-root `app`
    user, listens on 8080.
  - `.dockerignore` — keeps bin/obj/.db/.git/.vs out of the build context.
  - `docker-compose.yml` — `api` (built from the Dockerfile, configured for
    the Mongo provider with a credentialed connection string) + `mongo`
    (`mongo:8`, `MONGO_INITDB_ROOT_*` credentials, data volume, authenticated
    `mongosh ping` healthcheck; api waits for `service_healthy`).
  - `.env.example` — documents the `MONGO_USER`/`MONGO_PASSWORD` defaults.

### Host port mappings (local machine conflicts)

- API: host **8090** → container **8080** (other local stacks own 8080).
- Mongo: host **27018** → container **27017** (other local stacks own 27017).
  Inside the compose network the API always uses
  `mongodb://mongo:27017` regardless of the host mapping.

### Issues hit so far

- **MongoDB.Driver 3.x is a breaking rewrite.** Lambda filters no longer
  implicitly convert to `FilterDefinition<T>` — use
  `Builders<T>.Filter.Eq(...)`; `ReplaceOneAsync` requires explicit
  `ReplaceOptions`; `InsertManyAsync` requires explicit `InsertManyOptions`;
  LINQ reads go through `collection.AsQueryable()`.
- **DI factory overload** — `AddSingleton(provider => new MongoClient(...))`
  registers the concrete `MongoClient`, not `IMongoClient` → runtime
  `InvalidOperationException`. Fixed with `AddSingleton<IMongoClient>(...)`.
- **Guid representation (resolved).** Driver 3.x (CSHARP-2930) defaults to
  `GuidRepresentation.Unspecified`, and `GuidSerializer` throws
  `BsonSerializationException` on any read/write. Per the official 3.x docs,
  the fix for automapped POCOs is the `[BsonGuidRepresentation]` attribute on
  each Guid property — applied `GuidRepresentation.Standard` (the recommended
  format for new deployments) to `ResourceDocument.Id`, verified with a
  serialization roundtrip and the live API.

### Possible next steps

- Outbox pattern for at-least-once domain-event delivery (subscriber failures
  are currently logged, not retried).
- Mongo multi-document transactions (requires a replica set) to make the
  cross-aggregate unit of work atomic.
- Real authentication (OIDC) before exposing the API publicly.
- Testcontainers-based tests for the Mongo provider path.

## Run

Local (SQLite, default):

```bash
dotnet build
dotnet run --project src/DotNetApiPi.Api
# Swagger UI: http://localhost:5181/swagger (Development)
```

Docker (API + MongoDB):

```bash
docker compose up -d --build
# API: http://localhost:8090  (Swagger at /swagger)
# Mongo shell: mongosh --port 27018 -u <user> -p <password> --authenticationDatabase admin
docker compose down   # add -v to delete the mongo data volume
```
