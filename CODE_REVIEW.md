# Code review — DotNetApiPi (.NET 9 Web API, Clean Architecture / DDD)

## 1. Executive summary

- **Solid, textbook Clean-Architecture scaffold.** Dependency direction is enforced by project references, the aggregate root is well-encapsulated, and CQRS is implemented without pulling in MediatR. This is a nicer starter than most.
- **Two persistence providers behind one repository contract (EF/SQLite and MongoDB)** with matching unit-of-work semantics — a genuinely non-trivial piece of work, and it's done cleanly (`InternalsVisibleTo` + `Resource.Reconstitute` is a legitimate way to keep invariants intact on load).
- **REST semantics are wrong in one place**: domain-invariant failures on client input are mapped to `409 Conflict` instead of `400 Bad Request`.
- **One real domain bug**: `ValueObject.GetHashCode` has an operator-precedence bug that makes hashing unreliable / non-deterministic for value objects with `null` members.
- **Operational maturity is thin**: no auth, no HTTPS redirect, no CORS, no health checks, no rate limiting, no request logging middleware, Swagger exposed in every environment, and the container runs as `root`.
- **Test coverage is only Application-layer handler tests via Moq.** No Domain tests, no Infrastructure tests (Mongo/EF round-trip), no API/integration tests.

Overall maturity: **advanced scaffold**, not production-ready.

## 2. What's done well

- **Dependency direction is watertight.** [src/DotNetApiPi.Domain/DotNetApiPi.Domain.csproj](src/DotNetApiPi.Domain/DotNetApiPi.Domain.csproj) has zero project/package references; Application → Domain only; Infrastructure → Domain + Application; Api → Application + Infrastructure. Textbook Clean.
- **Aggregate design.** [src/DotNetApiPi.Domain/Entities/Resource.cs](src/DotNetApiPi.Domain/Entities/Resource.cs) uses `sealed class`, private constructor + `Create` factory, private setters, `ImmutableArray<ResourceTag>` for the collection, and enforces the `Draft → Active → Archived` state machine with `DomainException`.
- **Reconstitution pattern is properly scoped.** [src/DotNetApiPi.Domain/Entities/Resource.cs](src/DotNetApiPi.Domain/Entities/Resource.cs#L98) makes `Reconstitute` `internal`, and [src/DotNetApiPi.Domain/AssemblyInfo.cs](src/DotNetApiPi.Domain/AssemblyInfo.cs) grants access only to `DotNetApiPi.Infrastructure`. Application code cannot bypass invariants.
- **Repository interface lives in Domain** — proper dependency inversion.
- **Explicit CQRS** without MediatR: [src/DotNetApiPi.Application/Common/ICommandHandler.cs](src/DotNetApiPi.Application/Common/ICommandHandler.cs), [src/DotNetApiPi.Application/Common/IQueryHandler.cs](src/DotNetApiPi.Application/Common/IQueryHandler.cs). Handlers are focused and depend only on `IResourceRepository`.
- **Mongo repository mirrors EF unit-of-work semantics** with a proper insert/update/delete change-set. Domain events are dispatched then cleared after write, matching the EF path.
- **Guid representation problem was solved the correct 3.x way** — `[BsonGuidRepresentation(GuidRepresentation.Standard)]`.
- **Tests exercise handlers through mocked repositories using the aggregate's real behavior** — not by hand-fabricating aggregate state.
- **`.dockerignore` is present** and excludes `bin/`, `obj/`, `*.db*`, `.git`, `.vs`.
- Every handler is `sealed`, does `ArgumentNullException.ThrowIfNull`, uses `ConfigureAwait(false)`, and accepts `CancellationToken`.

## 3. Findings by severity

### 🔴 Critical / bugs / security

**1. `ValueObject.GetHashCode` operator-precedence bug**
- File: [src/DotNetApiPi.Domain/ValueObjects/ValueObject.cs](src/DotNetApiPi.Domain/ValueObjects/ValueObject.cs#L27)
- The line `accumulator * 31 + member?.GetHashCode() ?? 0` binds as `(accumulator * 31 + member?.GetHashCode()) ?? 0` because `+` has higher precedence than `??`. When `member` is null, the whole `+` result is null → the accumulator collapses to `0` for the rest of the fold. Every value object with a `null` member (or that follows one) will hash to `0`. It also boxes to `int?` throughout.
- Fix: `accumulator * 31 + (member?.GetHashCode() ?? 0)`, or better, `HashCode.Combine(...)`.

**2. Domain-invariant failures on client input are returned as `409 Conflict`, not `400 Bad Request`**
- Files: [src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs](src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs#L44), [src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs](src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs#L42)
- `POST /api/resource` with `"name": ""` throws `DomainException("A resource must have a name.")` in the value object → mapped to `409`. Client sent malformed input; that's a `400`. `409` should be reserved for state-transition conflicts (`Activate` on an already-archived resource).
- Fix: split invariant violations into "input validation" (400) vs "state conflict" (409), or introduce a dedicated `InvalidInputException` at the value-object boundary and map that to 400.

**3. Domain invariants missing length caps that persistence enforces**
- Files: [src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs](src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs), [src/DotNetApiPi.Domain/Entities/Resource.cs](src/DotNetApiPi.Domain/Entities/Resource.cs#L143), [src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs](src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs#L59)
- EF caps `Name` at 256, `Description` at 2048, `Tags` blob at 2048. The aggregate accepts strings of any length. A 5-KB name is a valid domain object but crashes at `SaveChangesAsync` with a store-level error → 500. Mongo has no such cap at all → silently stores unbounded input.
- Fix: enforce max length in `ResourceName` and `Resource.SetDescription`.

**4. `ExceptionHandlingMiddleware` has no catch-all for unexpected exceptions**
- File: [src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs](src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs#L41)
- Only `DomainException`, `ResourceNotFoundException`, `ValidationException` are caught. Any other exception bubbles to the host (in Production, empty 500 with no body). None of the *handled* exceptions are logged — you lose the stack in prod.
- Fix: add `catch (Exception ex)` that logs and returns `500` problem+json, and log every branch with `ILogger`.

**5. Container runs as root**
- File: [src/DotNetApiPi.Api/Dockerfile](src/DotNetApiPi.Api/Dockerfile#L26)
- No `USER` directive. `mcr.microsoft.com/dotnet/aspnet:9.0` supports a non-root `app` user.
- Fix: `USER app` before `ENTRYPOINT`, or `USER $APP_UID`.

**6. Swagger exposed in all environments**
- File: [src/DotNetApiPi.Api/Program.cs](src/DotNetApiPi.Api/Program.cs#L48)
- `UseSwagger()` / `UseSwaggerUI()` are unconditional. Standard practice is to guard on `app.Environment.IsDevelopment()`.

**7. No authentication, authorization, HTTPS redirection, CORS, or rate limiting**
- Verified via search: no `UseAuthorization`, `UseAuthentication`, `AddAuthorization`, `AddCors`, `UseHttpsRedirection`, or health checks anywhere in `src/`. Must be added before Internet exposure.

### 🟠 Significant / architectural concerns

**8. API project references EF Core directly**
- File: [src/DotNetApiPi.Api/DotNetApiPi.Api.csproj](src/DotNetApiPi.Api/DotNetApiPi.Api.csproj#L9)
- `Program.cs` uses EF types via `context.Database.EnsureCreated()` and pulls in `Microsoft.EntityFrameworkCore` + `Design` + `Sqlite`. Leaks EF Core into the API/composition-root layer even though Infrastructure already owns it.
- Fix: move the `EnsureCreated` call behind an `IInfrastructureInitializer` in Infrastructure and drop the EF package references from the API.

**9. Mongo repository uses `FindSync` inside an async method**
- File: [src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs](src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs#L82)
- `_collection.FindSync(filter).FirstOrDefaultAsync(ct)` performs synchronous I/O on the thread-pool, defeating the point of the async path.
- Fix: `_collection.Find(filter).FirstOrDefaultAsync(cancellationToken)`.

**10. No transactional consistency across a Mongo `SaveChangesAsync`**
- File: [src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs](src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs#L119)
- Insert-then-replace-then-delete each run as independent commands. Failure between them leaves the aggregate partially persisted. Domain events dispatched after only partial success.
- Fix: use a Mongo session/transaction (requires replica set) or document the limitation.

**11. Domain events dispatched inside `SaveChangesAsync` — event-handler exceptions can't be rolled back**
- File: [src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs](src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs#L84)
- Standard tradeoff, but worth being deliberate. Consider inbox/outbox for at-least-once semantics.

**12. `DispatchDomainEventsAsync` is hard-wired to `AggregateRoot<Guid>`**
- File: [src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs](src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs#L138)
- A future aggregate with, e.g., `AggregateRoot<int>` would silently be skipped. Introduce a non-generic marker (`IHasDomainEvents`).

**13. `MongoResourceRepository.GetByIdAsync`/`GetAllAsync` mutate unit-of-work state on read**
- File: [src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs](src/DotNetApiPi.Infrastructure/Repositories/MongoResourceRepository.cs#L96)
- Emulating EF change tracking by pushing everything read into `_toUpdate` means a plain query with no mutation still triggers `ReplaceOneAsync` calls in `SaveChangesAsync` — write amplification.
- Fix: track "loaded but unmodified" separately, or skip replacement when the document is byte-equal.

**14. `Program.cs` uses `EnsureCreated()` instead of migrations**
- File: [src/DotNetApiPi.Api/Program.cs](src/DotNetApiPi.Api/Program.cs#L41)
- Fine for a demo; bypasses any pending migrations once schema changes.

**15. No health checks**
- Program.cs registers no `AddHealthChecks()`. Add `MapHealthChecks("/health")` and expose it in compose.

**16. `docker-compose.yml` Mongo runs with no auth**
- File: [docker-compose.yml](docker-compose.yml#L27)
- No `MONGO_INITDB_ROOT_USERNAME` / `_PASSWORD`. Acceptable for local dev only — document that.

### 🟡 Minor / polish / nits

**17. REST route is singular** — [src/DotNetApiPi.Api/Controllers/ResourceController.cs](src/DotNetApiPi.Api/Controllers/ResourceController.cs#L16): `[Route("api/[controller]")]` yields `/api/resource`. Convention is plural.

**18. DTOs use mutable `List<string>?`** — [src/DotNetApiPi.Api/Dtos/CreateResourceRequest.cs](src/DotNetApiPi.Api/Dtos/CreateResourceRequest.cs#L10), [src/DotNetApiPi.Api/Dtos/UpdateResourceRequest.cs](src/DotNetApiPi.Api/Dtos/UpdateResourceRequest.cs#L10). Use `IReadOnlyList<string>?`.

**19. `ResourceDto` exposes the Domain enum `ResourceStatus`** — [src/DotNetApiPi.Application/Dtos/ResourceDto.cs](src/DotNetApiPi.Application/Dtos/ResourceDto.cs#L2). Consider mapping to `string` at the DTO boundary for wire stability.

**20. `ResourceMapper.ToDto(Resource?)` returns nullable then callers force-unwrap with `!` and throw a spurious `ValidationException`** — [src/DotNetApiPi.Application/Commands/CreateResourceCommandHandler.cs](src/DotNetApiPi.Application/Commands/CreateResourceCommandHandler.cs#L44). Aggregate is never null there. Split into `ToDto(Resource)` and `ToDtoOrNull(Resource?)`.

**21. `Resource.NormalizeTags` does `.Distinct().ToHashSet()`** — [src/DotNetApiPi.Domain/Entities/Resource.cs](src/DotNetApiPi.Domain/Entities/Resource.cs#L205). `.ToHashSet()` already deduplicates.

**22. `ApiDbContext` allocates a fresh `JsonSerializerOptions` on every serialize/deserialize** — [src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs](src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs#L98). Hoist to `static readonly`.

**23. `AggregateRoot.DomainEvents` calls `_domainEvents.AsReadOnly()` on every access** — [src/DotNetApiPi.Domain/Common/AggregateRoot.cs](src/DotNetApiPi.Domain/Common/AggregateRoot.cs#L24). Allocates a new wrapper each time.

**24. `ApiDbContext` has a design-time constructor that silently disables event dispatch** — [src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs](src/DotNetApiPi.Infrastructure/Persistence/ApiDbContext.cs#L37). Mark it `internal` or `protected`.

**25. `ResourceName.Of(...)` and `new ResourceName(...)` are redundant** — [src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs](src/DotNetApiPi.Domain/ValueObjects/ResourceName.cs#L30). Pick one style.

**26. `AggregateRoot.ClearDomainEvents()` is public** — [src/DotNetApiPi.Domain/Common/AggregateRoot.cs](src/DotNetApiPi.Domain/Common/AggregateRoot.cs#L38). Any code can wipe pending events. Change to `internal`.

**27. `ResourceCreatedEvent` sets `OccurredOn = DateTime.UtcNow`** — [src/DotNetApiPi.Domain/Events/ResourceCreatedEvent.cs](src/DotNetApiPi.Domain/Events/ResourceCreatedEvent.cs#L15). Prefer injected `TimeProvider`.

**28. `ExceptionHandlingMiddleware.Next` is a public property** — [src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs](src/DotNetApiPi.Api/Middleware/ExceptionHandlingMiddleware.cs#L33). Convention keeps `_next` private.

**29. `Program.cs` has no `.UseHttpsRedirection()`, no `X-Correlation-Id` propagation, no request logging middleware.**

**30. `.dockerignore` doesn't ignore `Dockerfile` itself or `docker-compose.yml`** — cosmetic.

## 4. Missing pieces

- **Tests**: only Application-layer handler tests exist. Missing:
  - **Domain unit tests** for `Resource` state transitions, `ResourceName`/`ResourceTag` invariants, and `ValueObject` equality/hashing (would have caught finding #1).
  - **Infrastructure tests**: EF round-trip against SQLite in-memory, Mongo round-trip via Testcontainers.
  - **API integration tests** via `WebApplicationFactory<Program>`.
- **Health checks** (`/health/live`, `/health/ready`) plus a compose healthcheck for the `api` service.
- **Observability**: no `ILogger` calls in controllers/handlers/middleware; no OpenTelemetry / structured logging config; no request/correlation logging.
- **Validation layer**: value-object validation is the *only* input validation. No FluentValidation or data-annotations; `[ApiController]` auto-400s on model binding errors but does nothing for semantic input.
- **Migrations** instead of `EnsureCreated()`.
- **Security**: no auth, no CORS policy, no HTTPS redirection, no rate limiter.
- **Concurrency**: no ETag / optimistic concurrency token on `Resource`. `PUT` overwrites unconditionally.
- **Global error contract**: `ProblemDetails` `Type` points at `https://http.cat/{code}` — cute for a demo, wrong for a spec-compliant RFC 7807 URI.
- **`TimeProvider`** abstraction so tests don't rely on `DateTime.UtcNow`.

## 5. Prioritized recommendations

1. **Fix `ValueObject.GetHashCode`** (finding #1) — real bug, one-line fix. Add a Domain test for it.
2. **Split domain invariants into input-validation vs state-transition exceptions**, and remap: input-validation → 400, state-transition → 409 (findings #2, #4). Log every mapped exception; add a catch-all 500 branch.
3. **Enforce max lengths in the domain** (`ResourceName`, `Description`) so invariants match persistence caps (finding #3).
4. **Harden the container**: add `USER app` in the Dockerfile, guard Swagger on `IsDevelopment`, add HTTPS redirection, add a `/health` endpoint and a compose healthcheck for `api` (findings #5, #6, #15).
5. **Fix `MongoResourceRepository`**: switch `FindSync` to `Find(...).FirstOrDefaultAsync`, stop pushing read aggregates into the update change-set, and either enable a Mongo transaction or document that a `SaveChangesAsync` isn't atomic (findings #9, #10, #13).
6. **Round out the test suite**: Domain aggregate/value-object tests, Infrastructure round-trip tests (in-memory SQLite + Mongo Testcontainers), and API `WebApplicationFactory` tests.
7. **Move `EnsureCreated`/migration bootstrap out of `Program.cs`** and drop EF Core package references from the API project (findings #8, #14).
8. **Add structured logging, correlation IDs, and OpenTelemetry** — the ceiling on debuggability for anything past a demo.

## 6. Overall grade

**B / B+** — a well-thought-out, dependency-clean Clean-Architecture + DDD scaffold with one real bug (`ValueObject.GetHashCode`), one REST-semantics mistake (invariant failures → 409 instead of 400), and the usual production-readiness gaps (auth, observability, health, hardened container, broader tests). The bones are unusually good for a "scaffold"; what's needed is operational polish and one round of domain-invariant + exception-mapping cleanup.
