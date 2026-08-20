# Progress — .NET 9 Web API Scaffold (Clean Architecture + DDD)

**Status: COMPLETE** — the solution builds clean (0 warnings, 0 errors) with
**two storage providers** (EF Core/SQLite and MongoDB) behind the same
`IResourceRepository` contract, and is containerized via Docker Compose. Both
paths are smoke-tested end-to-end.

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
- `Common/AggregateRoot.cs` — holds domain events, `AddDomainEvent` / `ClearDomainEvents`
- `ValueObjects/ValueObject.cs` — base value object (value equality via `==`/`!=`, `Members()`)
- `ValueObjects/ResourceName.cs` — non-empty, trimmed; throws `DomainException`
- `ValueObjects/ResourceTag.cs` — lower-cased token, `IEquatable<ResourceTag>`
- `Enums/ResourceStatus.cs` — `Draft=0, Active=1, Archived=2`
- `Events/IDomainEvent.cs`, `Events/ResourceCreatedEvent.cs`
- `Exceptions/DomainException.cs`
- `Entities/Resource.cs` — aggregate root: private ctor + `Create` factory; `Rename`,
  `SetDescription`, `Activate`, `Archive`, `AddTag`, `SetTags`; invariants (Draft→Active→Archived)
- `Repositories/IResourceRepository.cs` — repository interface in the Domain (dependency inversion)

### Application (`DotNetApiPi.Application`) — depends on Domain
- `Common/` — `ICommand`/`IQuery` markers, generic `ICommandHandler`/`IQueryHandler`,
  `IDomainEventDispatcher`, `Unit`
- `Common/Exceptions/` — `ResourceNotFoundException`, `ValidationException`
- `Dtos/ResourceDto.cs`, `Mapping/ResourceMapper.cs`
- `Commands/` — Create / Update / Activate / Archive / Delete + handlers
- `Queries/` — GetResourceById / GetAllResources + handlers
- `DependencyInjection.cs` — `AddApplication()`

### Infrastructure (`DotNetApiPi.Infrastructure`) — depends on Application + Domain
- `Persistence/ApiDbContext.cs` — SQLite `DbContext`; configures `Resource` via value
  converters (value objects + enum persisted inline); dispatches domain events on save
- `Repositories/ResourceRepository.cs` — implements `IResourceRepository`
- `EventHandlers/DomainEventDispatcher.cs` — implements `IDomainEventDispatcher`
- `DependencyInjection.cs` — `AddInfrastructure(connectionString)`

### Api (`DotNetApiPi.Api`) — depends on Application + Infrastructure
- `Controllers/ResourceController.cs` — full CRUD + `/activate` + `/archive`
- `Dtos/` — `CreateResourceRequest`, `UpdateResourceRequest`
- `Middleware/ExceptionHandlingMiddleware.cs` — maps exceptions to HTTP (problem+json)
- `Program.cs` — DI wiring, SQLite `EnsureCreated`, Swagger, seed

## Key decisions
- **Generic `Resource` aggregate** (domain-agnostic) demonstrating value objects, invariants,
  lifecycle transitions and domain events.
- **SQLite** so the API runs with no external server; schema created via `EnsureCreated`.
- **CQRS without MediatR** — explicit `ICommandHandler`/`IQueryHandler` resolved via DI.
- **No FluentValidation** — validation lives in value objects / the domain; `ValidationException`
  surfaced by the middleware.
- **Repository + event-dispatcher interfaces live in Domain / Application** (dependency inversion).
- **Value objects persisted via `ValueConverter`s** so the aggregate maps to a single table.

## Notable fixes made during the build
- `Resource.cs` private ctor field initializer `Name = ResourceName.Of(string.Empty)` threw
  on construction (empty name is invalid) — removed the initializer (the ctor sets `Name`).
- Repository `OrderBy(r => r.Name.Value)` (value object) could not translate to SQL —
  changed to order by `Id`.
- Update handler was *adding* tags (`AddTag`) instead of replacing — added `SetTags` on the
  aggregate and used it in the update handler.
- `EntityTypeBuilder`/`IEntityTypeConfiguration` namespace resolution in EF Core 9 — entity
  configuration was inlined into `DbContext.OnModelCreating` via the fluent API.

## Smoke test results (live API on http://localhost:5181)
- POST create → 201 (resource with id, name, description, status=Draft, tags)
- GET all → 200 list
- GET by id → 200
- PUT update → 200 (name updated, tags replaced)
- POST activate → 200 (status=Active)
- POST archive → 200 (status=Archived)
- DELETE → 204; subsequent GET → 404
- Empty name → 409 Conflict (problem+json)
- Non-existent id → 404 (problem+json)

## MongoDB storage provider + Docker (completed)

**Status: COMPLETE** — full CRUD flow verified against the live
MongoDB-backed API in Docker; the Guid serialization issue is resolved using
the approach documented in the official 3.x driver docs (see below).

### What was added

- **Domain** (`DotNetApiPi.Domain`)
  - `Resource.Reconstitute(...)` — `internal` factory so the persistence layer
    can rebuild aggregates from stored state without bypassing invariants or
    raising domain events (application code still goes through `Create` /
    the repository).
  - `AssemblyInfo.cs` — `[assembly: InternalsVisibleTo("DotNetApiPi.Infrastructure")]`
    so only the infrastructure assembly can use the reconstitution factory.
- **Infrastructure** (`DotNetApiPi.Infrastructure`)
  - `MongoDB.Driver` 3.11.0 package.
  - `Persistence/StorageProvider.cs` — `Sqlite` (default) | `Mongo`.
  - `Persistence/PersistenceOptions.cs` — bound from the `"Storage"` config
    section (provider, both connection strings, Mongo database name).
  - `Persistence/Mongo/ResourceDocument.cs` — Mongo document model (`_id`
    Guid, name, description, status as string, tags as string array).
  - `Persistence/Mongo/ResourceDocumentMapper.cs` — document ↔ aggregate
    mapping via `Resource.Reconstitute` (no domain invariants bypassed, no
    events raised on load).
  - `Repositories/MongoResourceRepository.cs` — implements
    `IResourceRepository` with the same unit-of-work semantics as the EF
    implementation: `AddAsync`/`RemoveAsync` stage changes, read methods load
    aggregates into the unit of work, and `SaveChangesAsync` performs the
    inserts/replaces/deletes, then dispatches and clears domain events.
  - `DependencyInjection.cs` — `AddInfrastructure(PersistenceOptions)`
    branches on the provider; the old `AddInfrastructure(connectionString)`
    overload is kept as a SQLite convenience. Mongo client/database/collection
    are singletons (thread-safe handles); the repository (unit of work) is
    scoped.
- **Api** (`DotNetApiPi.Api`)
  - `Program.cs` — storage is selected from the `Storage` config section
    (env vars `Storage__Provider=mongo`, `Storage__MongoConnectionString`,
    `Storage__MongoDatabaseName`); `EnsureCreated` only runs for SQLite
    (Mongo creates its collection lazily on first write).
  - `appsettings.json` — `Storage` section with SQLite defaults plus a
    local-Mongo default (`mongodb://localhost:27017`).
- **Docker**
  - `src/DotNetApiPi.Api/Dockerfile` — multi-stage (sdk:9.0 build →
    aspnet:9.0 runtime), framework-dependent publish, listens on 8080.
  - `.dockerignore` — keeps bin/obj/.db/.git/.vs out of the build context.
  - `docker-compose.yml` — `api` (built from the Dockerfile, configured for
    the Mongo provider) + `mongo` (`mongo:8`, data volume, `mongosh` ping
    healthcheck; api waits for `service_healthy`).

### Host port mappings (local machine conflicts)

- API: host **8090** → container **8080** (`eventstore` already owns 8080).
- Mongo: host **27018** → container **27017** (`customer-service-mongo`
  already owns 27017). Inside the compose network the API always uses
  `mongodb://mongo:27017` regardless of the host mapping.

### Issues hit so far

- **MongoDB.Driver 3.x is a breaking rewrite.** Lambda filters no longer
  implicitly convert to `FilterDefinition<T>` — use
  `Builders<T>.Filter.Eq(...)`; `ReplaceOneAsync` requires explicit
  `ReplaceOptions` (a bare `null` is ambiguous with `UpdateOptions`);
  `InsertManyAsync` requires explicit `InsertManyOptions` (nullable is fine);
  LINQ reads go through `collection.AsQueryable()` (`using MongoDB.Driver.Linq`).
- **DI factory overload** — `AddSingleton(provider => new MongoClient(...))`
  registers the concrete `MongoClient`, not `IMongoClient` → runtime
  `InvalidOperationException`. Fixed with `AddSingleton<IMongoClient>(...)`.
- **Guid representation (resolved).** Driver 3.x (CSHARP-2930) defaults to
  `GuidRepresentation.Unspecified`, and `GuidSerializer` throws
  `BsonSerializationException` on any read/write. 3.x removed the 2.x
  workarounds (`MongoUri`, `MongoClientSettings.GuidRepresentation`,
  `BsonDefaults.GuidRepresentation`). Per the official 3.x docs, the fix for
  automapped POCOs is the `[BsonGuidRepresentation]` attribute on each Guid
  property — applied `GuidRepresentation.Standard` (the recommended format for
  new deployments) to `ResourceDocument.Id`, verified with a serialization
  roundtrip and the live API.

### Smoke test results (docker compose, Mongo provider, live API on :8090)

- POST create → 201 (id, name, description, status=Draft, tags)
- GET all → 200; GET by id → 200
- PUT update → 200 (name updated, tags replaced)
- POST activate → 200; POST archive → 200
- POST archive again → 409 (illegal transition, problem+json)
- Empty name → 409 (problem+json)
- DELETE → 204; subsequent GET → 404
- Domain events dispatched after save (`ResourceCreatedEvent` logged)
- Raw document verified in Mongo: `{_id: UUID(...), Name, Description,
  Status: 'Draft', Tags: [...]}` (collection `Resources`, db `dotnet_api_pi`)

### Possible next steps

- An xunit test project (domain invariants, value objects, handlers, and a
  Mongo roundtrip using Testcontainers).
- An API container healthcheck (the aspnet image ships no `curl`) and Mongo
  seeding/migrations if the scaffold grows beyond the sample aggregate.
- The compose stack is left running for inspection: `docker compose down -v`
  stops it and deletes the Mongo data.

## Run

Local (SQLite, default):

```bash
dotnet build
dotnet run --project src/DotNetApiPi.Api
# Swagger UI: http://localhost:5181/swagger
```

Docker (API + MongoDB):

```bash
docker compose up -d --build
# API: http://localhost:8090  (Swagger at /swagger)
# Mongo shell: mongosh --port 27018
docker compose down   # add -v to delete the mongo data volume
```
