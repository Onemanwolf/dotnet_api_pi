# Outbox / Kafka — correction spec (review round 3)

**Source:** external review of the transactional outbox + Kafka relay, re-verified
2026-08-20 19:55 EDT against commit `4e36804`.

**Scope:** six work items. Three close findings the round-2 commit left partially
done (O-02, O-04, O-09, O-10); two fix issues that round-2 introduced (N-01, N-02).
Nothing here changes the outbox pattern itself — the design is sound and verified.

**Out of scope:** any change to the transaction boundary in
`MongoResourceRepository.SaveChangesAsync`, the claim/lease state machine, the
envelope shape, or the Kafka producer configuration. Those were reviewed and are
correct.

**Definition of done for the whole spec:** `dotnet build DotNetApiPi.sln -c Release
-warnaserror` clean, `dotnet test DotNetApiPi.sln -c Release` green, commits pushed,
CI run green on GitHub.

> Note: the local SDK on the developer machine is 9.0.x while the solution targets
> `net10.0`. Build and test through the container:
> `docker run --rm -v "$PWD":/src -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test DotNetApiPi.sln -c Release`

---

## W-1 — Make the outbox claim index-only (closes O-04)

**Severity:** high · **Effort:** one line

### Problem

`ClaimNextPublishableAsync` filters on `{ status: $in [Pending, Publishing],
claimableAtUtc: { $lte: now } }`, which the `status_claimableAtUtc` index serves.
But the sort is `{ claimableAtUtc: 1, _id: 1 }`, and `_id` is not in that index, so
MongoDB adds a blocking in-memory `SORT` stage on top of the `IXSCAN`. Every claim
therefore fetches and sorts the whole claimable backlog to return one row.

Measured on the live collection with 30 claimable rows:

```
sort {claimableAtUtc, _id}   SORT=true    docsExamined=30   keysExamined=31
sort {claimableAtUtc}        SORT=false   docsExamined=1    keysExamined=1
```

At a 100k-row backlog with `BatchSize = 50`, that is ~5M document fetches per poll
cycle, and the 100 MB blocking-sort limit becomes reachable.

### Change

File: `src/DotNetApiPi.Infrastructure/Outbox/MongoOutboxEventStore.cs` (~line 138)

Current:

```csharp
Sort = Sort.Combine(
    Sort.Ascending(d => d.ClaimableAtUtc),
    Sort.Ascending(d => d.Id))
```

Required:

```csharp
// Sort on claimableAtUtc ONLY. The {status, claimableAtUtc} index can then
// provide the ordering directly (SORT_MERGE across the two status values),
// so a claim is an index seek instead of a blocking sort over the whole
// claimable backlog. Adding any key that is not in the index — _id included —
// reintroduces the in-memory SORT stage.
Sort = Sort.Ascending(d => d.ClaimableAtUtc)
```

Do **not** change the index definition, the filter, or `ReturnDocument.After`.

Ties on `claimableAtUtc` are resolved arbitrarily. That is acceptable: the claim is
already atomic per row, ordering only needs to be fair (oldest-claimable first), and
per-resource ordering is guaranteed downstream by the Kafka message key, not by this
sort.

If a deterministic tiebreak is later required, add `_id` to the **index**
(`{ status: 1, claimableAtUtc: 1, _id: 1 }`) and to the sort together — never to the
sort alone.

### Acceptance criteria

1. The claim query plan contains no `SORT` stage when rows are claimable.
2. `totalDocsExamined == 1` for a single claim regardless of backlog size.
3. Existing relay tests still pass unchanged.

### Verification

Create a backlog (stop Kafka, POST 30 resources), then:

```javascript
// mongosh -u root -p devpass123 --authenticationDatabase admin
const c = db.getSiblingDB("dotnet_api_pi").outbox_events;
const future = new Date(Date.now() + 3600*1000);
const e = c.find({status:{$in:[0,1]}, claimableAtUtc:{$lte:future}})
           .sort({claimableAtUtc:1}).limit(1).explain("executionStats");
print(JSON.stringify(e.queryPlanner.winningPlan).includes('"stage":"SORT"')); // must be false
print(e.executionStats.totalDocsExamined);                                    // must be 1
```

---

## W-2 — Make the claim lease outlast the batch it protects (closes N-01)

**Severity:** medium · **Effort:** small

### Problem

The O-07 batching fix changed the timing arithmetic. Defaults today:

| Setting | Value | Where |
|---|---|---|
| `Outbox:BatchSize` | 50 | `OutboxOptions.cs` |
| `Outbox:PublishConcurrency` | 8 | `OutboxOptions.cs` |
| `Outbox:LeaseSeconds` | 30 | `OutboxOptions.cs` |
| `MessageTimeoutMs` | 30000 | hard-coded in `ConfluentKafkaEventPublisher.cs` |

A full batch is `ceil(50 / 8) = 7` waves. Against a degraded broker each wave can burn
the full 30 s produce timeout, so the last rows of a batch may be published ~210 s
after they were claimed — under a 30 s lease. From ~30 s in, every still-unpublished
row in the batch looks abandoned to any other relay instance.

With a single relay this is invisible (the loop awaits the whole batch before
claiming again). With two or more API replicas it produces duplicate publishes during
a broker slowdown. Delivery is at-least-once and the `claimId` makes the loser's
write a clean no-op, so this is not data loss — but it is avoidable noise at exactly
the wrong moment.

### Change

**(a)** Make the produce timeout configurable so the relation is expressible.

File: `src/DotNetApiPi.Infrastructure/Kafka/KafkaOptions.cs`

```csharp
/// <summary>
/// Hard deadline for a single produce, including librdkafka's internal
/// retries (maps to message.timeout.ms). The outbox lease must outlast a
/// whole batch of these — see OutboxOptions.LeaseSeconds.
/// </summary>
public int MessageTimeoutMs { get; init; } = 30_000;
```

File: `src/DotNetApiPi.Infrastructure/Kafka/ConfluentKafkaEventPublisher.cs` — replace
the hard-coded `MessageTimeoutMs = 30_000` in the `ProducerConfig` initializer with
`MessageTimeoutMs = kafka.MessageTimeoutMs`.

**(b)** Raise the default lease past the worst-case batch drain.

File: `src/DotNetApiPi.Infrastructure/Outbox/OutboxOptions.cs`

```csharp
/// <summary>
/// Claim lease in seconds. MUST outlast the worst case time to publish a
/// whole claimed batch, otherwise rows still in flight become claimable by
/// another relay instance and are published twice:
///
///     LeaseSeconds >= (BatchSize / PublishConcurrency) * (MessageTimeoutMs / 1000)
///
/// With the defaults (50 / 8 = 7 waves x 30 s) that lower bound is 210 s;
/// 240 s leaves headroom. Raising BatchSize or lowering PublishConcurrency
/// without raising this value reintroduces duplicate publishes.
/// </summary>
public int LeaseSeconds { get; init; } = 240;
```

**(c)** Fail loudly on a misconfiguration rather than silently duplicating.

File: `src/DotNetApiPi.Infrastructure/Outbox/OutboxEventRelayService.cs`, in
`StartAsync` before the loop starts:

```csharp
var worstCaseDrainSeconds =
    Math.Ceiling((double)_outboxOptions.BatchSize / _outboxOptions.PublishConcurrency)
    * (_messageTimeoutMs / 1000.0);

if (_outboxOptions.LeaseSeconds < worstCaseDrainSeconds)
{
    _logger.LogWarning(
        "Outbox lease ({LeaseSeconds}s) is shorter than the worst-case batch drain ({DrainSeconds}s: {BatchSize} rows / {Concurrency} concurrent x {TimeoutMs}ms). Rows still in flight can be re-claimed by another relay and published twice. Raise Outbox:LeaseSeconds, lower Outbox:BatchSize, or raise Outbox:PublishConcurrency.",
        _outboxOptions.LeaseSeconds,
        worstCaseDrainSeconds,
        _outboxOptions.BatchSize,
        _outboxOptions.PublishConcurrency,
        _messageTimeoutMs);
}
```

Inject `IOptions<KafkaOptions>` (already injected — reuse it) to read
`MessageTimeoutMs` into a `_messageTimeoutMs` field.

**(d)** Update the `Outbox:LeaseSeconds` row in the README configuration table to the
new default and state the constraint.

### Acceptance criteria

1. `KafkaOptions.MessageTimeoutMs` is configurable and used by the producer.
2. Default `LeaseSeconds` is 240.
3. Starting the relay with `Outbox:LeaseSeconds=30` logs the warning once, naming all
   four numbers.
4. Starting with defaults logs nothing extra.
5. A unit test asserts the warning fires for an under-sized lease and does not fire
   for the defaults.

---

## W-3 — Remove the vestigial `leaseUntilUtc` field (closes N-02)

**Severity:** low · **Effort:** small, mechanical

### Problem

The round-2 collapse of `nextRetryAtUtc` + lease into `claimableAtUtc` left
`LeaseUntilUtc` behind. It is still written on claim and nulled on publish/fail, but
no code reads it for any decision. A stored row therefore carries two fields that
look like the lease, only one of which is real — on an inspected row `leaseUntilUtc`
was `null` while `claimableAtUtc` held the actual expiry.

### Change

Delete the property and every write to it:

| File | What to remove |
|---|---|
| `Outbox/OutboxEventDocument.cs` | the `LeaseUntilUtc` property |
| `Outbox/OutboxEventRecord.cs` | the `LeaseUntilUtc` record parameter + its doc comment |
| `Outbox/MongoOutboxEventStore.cs` | the mapping in `AppendWithinTransactionAsync`, the `.Set(d => d.LeaseUntilUtc, …)` in the claim update and in both mark methods, and the constructor argument in the record projection |
| `Outbox/MongoOutboxEventRelayService.cs` / tests | any construction site that passes it |

Update the `OutboxEventRecord` XML docs so `ClaimableAtUtc` is described as the single
gate: "when this row next becomes claimable — creation time for a new row, the
backoff deadline after a failure, the lease expiry while `Publishing`."

**Migration note:** existing documents keep a stale `leaseUntilUtc` field. That is
harmless (the C# model simply ignores unmapped fields) — do **not** write a migration.
Optionally mention in the README that the field is a leftover on rows created before
this change.

### Acceptance criteria

1. `grep -rn "LeaseUntilUtc" src tests` returns nothing.
2. Build clean with `-warnaserror`; all tests pass.
3. A newly created outbox row has no `leaseUntilUtc` field.

---

## W-4 — Emit a metric when an event goes Dead (closes O-10)

**Severity:** low · **Effort:** small

### Problem

Exhausting the attempt budget logs (`OutboxEventRelayService.cs:336`) and nothing
else. The README replay runbook added in round 2 is good, but only helps someone who
already suspects a problem. There is no signal to alert on.

Note the current call is `LogError`; a permanently undeliverable domain event is a
`LogCritical`-grade condition.

### Change

**(a)** New file `src/DotNetApiPi.Infrastructure/Outbox/OutboxMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// OpenTelemetry instruments for the outbox relay. The meter name is the
/// stable identity that exporters and dashboards bind to.
/// </summary>
public static class OutboxMetrics
{
    /// <summary>The meter name (register with AddMeter in the composition root).</summary>
    public const string MeterName = "DotNetApiPi.Outbox";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Events that exhausted the retry budget and were marked Dead.</summary>
    public static readonly Counter<long> DeadEvents =
        Meter.CreateCounter<long>(
            "outbox.events.dead",
            unit: "{event}",
            description: "Outbox events that exhausted MaxAttempts and were marked Dead.");

    /// <summary>Events successfully published to Kafka.</summary>
    public static readonly Counter<long> PublishedEvents =
        Meter.CreateCounter<long>(
            "outbox.events.published",
            unit: "{event}",
            description: "Outbox events successfully published to Kafka.");

    /// <summary>Failed publish attempts that will be retried.</summary>
    public static readonly Counter<long> FailedAttempts =
        Meter.CreateCounter<long>(
            "outbox.publish.attempts.failed",
            unit: "{attempt}",
            description: "Failed publish attempts (excluding the final attempt that marks an event Dead).");
}
```

**(b)** In `OutboxEventRelayService`:

- where a row is marked Dead: `OutboxMetrics.DeadEvents.Add(1, new KeyValuePair<string, object?>("event.type", record.EventType));` and change `LogError` → `LogCritical`.
- where a publish succeeds: `OutboxMetrics.PublishedEvents.Add(1, new KeyValuePair<string, object?>("event.type", record.EventType));`
- on a retryable failure: `OutboxMetrics.FailedAttempts.Add(1, new KeyValuePair<string, object?>("event.type", record.EventType));`

Tag with `event.type` only — never with `resourceId` or `eventId` (unbounded
cardinality).

**(c)** In `src/DotNetApiPi.Api/Program.cs`, the OpenTelemetry builder currently has
only `.WithTracing(...)`. Add metrics:

```csharp
    .WithMetrics(metrics =>
    {
        var pipeline = metrics
            .AddMeter(DotNetApiPi.Infrastructure.Outbox.OutboxMetrics.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation();

        if (otelEnabled)
        {
            pipeline.AddOtlpExporter(options => options.Endpoint = otelEndpoint);
        }
    })
```

`AddRuntimeInstrumentation` needs `OpenTelemetry.Instrumentation.Runtime` — add it to
`Directory.Packages.props` and the Infrastructure/Api csproj, or drop that line if you
prefer not to add a package.

**(d)** README: add the three metric names next to the dead-row runbook, and state
that they are only exported when `Otel:Enabled=true`.

### Acceptance criteria

1. Meter `DotNetApiPi.Outbox` is registered and exports when OTel is enabled.
2. Forcing a Dead row increments `outbox.events.dead` and logs at Critical.
3. Metric tags carry `event.type` and nothing higher-cardinality.

---

## W-5 — Integration tests against real MongoDB and Kafka (closes O-09)

**Severity:** high · **Effort:** the largest item here

### Problem

All 31 Infrastructure tests run against an in-memory fake store. Nothing exercises
`MongoOutboxEventStore` against a real server, and nothing at all covers the guarantee
the pattern exists to provide: **an aborted transaction must leave no outbox row.**
Kafka is untested. This finding was missed in round 2 because the commit message
reused the number `O-09` for an unrelated change.

### Change

Add package references (versions in `Directory.Packages.props`):

```xml
<PackageVersion Include="Testcontainers.MongoDb" Version="4.*" />
<PackageVersion Include="Testcontainers.Kafka" Version="4.*" />
```

Create `tests/DotNetApiPi.Infrastructure.Tests/Integration/` with a shared fixture
that starts a **single-node replica set** (transactions require one — a plain
standalone container will make every test fail at `StartTransaction`):

```csharp
new MongoDbBuilder()
    .WithImage("mongo:8")
    .WithReplicaSet()          // required for transactions
    .Build();
```

> The replica set is a hard requirement, not a preference: without it every test
> that opens a transaction fails at `StartTransaction`. Confirm the builder method
> name against the `Testcontainers.MongoDb` version you resolve — if that release
> does not expose `WithReplicaSet`, fall back to a `ContainerBuilder` with
> `--replSet rs0 --bind_ip_all` plus a one-shot `rs.initiate({_id:"rs0",members:
> [{_id:0,host:"localhost:27017"}]})` and a wait-for-primary loop, mirroring what
> `docker-compose.yml` already does for the `mongo-init` service.

Mark every test in this folder `[Trait("Category", "Integration")]` so they can be
filtered: `dotnet test --filter "Category!=Integration"` must still work on a machine
without Docker.

**Required tests:**

| # | Test | Asserts |
|---|---|---|
| 1 | `AbortedTransaction_LeavesNoOutboxRow` | Start a UoW, stage an aggregate, force the transaction to abort (throw inside the callback), then assert `outbox_events` is empty **and** the aggregate was not written. *This is the priority test — it is the pattern's core guarantee.* |
| 2 | `CommittedTransaction_WritesAggregateAndOutboxRowTogether` | The happy path of #1: exactly one aggregate document and exactly one outbox row, both present. |
| 3 | `ConcurrentClaims_NeverReturnTheSameRow` | Two `MongoOutboxEventStore` instances claiming in parallel over N rows return N distinct ids, no duplicates. |
| 4 | `MarkPublished_WithForeignClaimId_IsNoOp` | Claim a row, then call `MarkPublishedAsync` with a different `claimId`; assert it returns false and the row is untouched. |
| 5 | `ExpiredClaim_IsReclaimable` | Claim with a 1-second lease, wait past it, claim again; assert the second claim succeeds and issues a **new** `claimId`. |
| 6 | `ClaimPlan_UsesIndexWithoutBlockingSort` | Seed ~200 claimable rows, run the claim query with `explain("executionStats")`, assert no `SORT` stage and `totalDocsExamined == 1`. *Guards W-1 against regression.* |
| 7 | `RelayPublishesToRealBroker` (Kafka container) | Run the real `ConfluentKafkaEventPublisher` against a Testcontainers broker; consume the topic and assert the envelope's `eventType`, `schemaVersion` and the `x-event-id` header match the outbox row. |

CI already runs on `ubuntu-latest`, which has Docker, so no workflow change is needed.
Add a timeout to each test (`[Fact(Timeout = 120_000)]`) — container startup is slow
on a cold image cache.

### Acceptance criteria

1. All seven tests exist, pass, and are traited `Integration`.
2. `dotnet test --filter "Category!=Integration"` still passes with Docker unavailable.
3. Test #1 fails if the outbox append is moved outside the transaction (verify by
   temporarily doing so — this proves the test is load-bearing rather than vacuous).
4. Test #6 fails if `_id` is re-added to the claim sort.

---

## W-6 — Push, and let CI verify (closes O-02)

**Severity:** high · **Effort:** one command

### Problem

`main` is 2 commits ahead of `origin/main`. The most recent CI run is for `11f2dde`
(the Swagger change). Neither the outbox commit nor the round-2 commit has been
compiled or tested anywhere but the developer machine — and the first round of this
work shipped a consumer that did not compile, which is precisely what the gate exists
to catch.

### Change

Push the branch. Then confirm the run:

```bash
git push origin main
gh run watch "$(gh run list --limit 1 --json databaseId -q '.[0].databaseId')" --exit-status
```

Do this **after** W-1 through W-5 so CI validates the finished state, but do not defer
it further than that.

### Acceptance criteria

1. `git status -sb` shows no `ahead` marker.
2. The newest GitHub Actions run is green and its commit is `HEAD`.

---

## Suggested order

1. **W-1** (one line, biggest scalability win)
2. **W-3** (mechanical, do it while the field layout is fresh)
3. **W-2** (small, prevents duplicate publishes before a second replica exists)
4. **W-4** (small, gives the runbook a trigger)
5. **W-5** (largest; W-5 test #6 also locks in W-1)
6. **W-6** (last, so CI validates everything together)

## What not to change

- The transaction boundary in `MongoResourceRepository.SaveChangesAsync` — verified correct.
- `{ status: 1, claimableAtUtc: 1 }` index definition, the claim filter, `ReturnDocument.After`.
- The `claimId` ownership model — verified correct.
- Producer settings `EnableIdempotence` / `Acks.All` — verified correct.
- The envelope shape and the `DomainEventWireTypes` mapping — verified correct.
- The partial TTL index — verified correct.
