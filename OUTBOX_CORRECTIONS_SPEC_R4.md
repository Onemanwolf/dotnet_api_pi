# Outbox / Kafka — correction spec (review round 4)

**Source:** verification of round 3 (`8f6ad2f`) against a running stack and a real
OTLP collector, 2026-08-21.

**Result of round 3:** W-1, W-2, W-3, W-5 and W-6 are implemented correctly and
verified. This spec covers the one item that is incomplete (W-4) plus one optional
test-hardening item.

**Do not re-do anything else.** The claim sort, the lease guard, the removal of
`LeaseUntilUtc`, the Testcontainers suite and the CI push were all checked and are
correct. In particular the sort in `ClaimNextPublishableAsync` must stay
`Sort.Ascending(d => d.ClaimableAtUtc)` with no second key.

---

## W-7 — Register the outbox meter so its metrics are actually exported

**Closes:** the residual half of W-4 · **Severity:** high (the feature is currently
inert) · **Effort:** ~5 lines

### Problem

The instruments in `src/DotNetApiPi.Infrastructure/Outbox/OutboxMetrics.cs` are
correct: three counters, tagged with `event.type` only, incremented at all three call
sites in `OutboxEventRelayService` (published / failed attempt / dead). The
`LogError` → `LogCritical` change was made.

But nothing subscribes the meter, so the MeterProvider discards every measurement.
`grep -rn "AddMeter" src` returns nothing.

Proven against a real OTLP collector, using an image rebuilt from `8f6ad2f`, with
`Otel__Enabled=true`:

```
3 resources created → relay logged 3 × "Published outbox event"
   (so OutboxMetrics.PublishedEvents.Add(...) ran three times)

instrumentation scopes received by the collector:
   Microsoft.AspNetCore, Microsoft.AspNetCore.Hosting,
   Microsoft.AspNetCore.MemoryPool, Microsoft.AspNetCore.Routing,
   Microsoft.AspNetCore.Server.Kestrel, System.Net.Http,
   System.Net.NameResolution, System.Runtime

occurrences of "DotNetApiPi.Outbox" in the collector output: 0
```

The metrics pipeline itself works — the built-in instrumentations export fine. Only
the outbox meter is missing, because only the built-ins register themselves.

### Root cause

`OutboxMetrics.cs` carries this comment:

> *"Counters are created on first touch (OTel picks up every `Meter` that is created
> in the process; the API's …"*

That is not how OpenTelemetry .NET works. A `MeterProvider` collects **only** meters
whose names were registered with `AddMeter(...)` — exactly like `AddSource(...)` for
tracing. An unregistered `Meter` is a no-op sink: the `Counter<T>` objects exist,
`Add()` succeeds, and the measurements are dropped.

Fix the comment as well as the code, or the registration will be removed again by
whoever reads it next.

### Change

**(a)** `src/DotNetApiPi.Infrastructure/Outbox/OutboxMetrics.cs` — introduce the name
as a constant and use it, and correct the comment:

```csharp
/// <summary>
/// Outbox relay metrics.
/// <para>
/// The meter must be registered explicitly in the composition root with
/// <c>AddMeter(OutboxMetrics.MeterName)</c>. OpenTelemetry does NOT discover
/// meters by itself — an unregistered Meter silently drops every
/// measurement (the counters still exist and Add() still succeeds, which is
/// what makes the mistake hard to notice).
/// </para>
/// </summary>
public static class OutboxMetrics
{
    /// <summary>
    /// Stable meter name. This is the identity dashboards and exporters bind
    /// to — treat it as a public contract and do not rename it casually.
    /// </summary>
    public const string MeterName = "DotNetApiPi.Outbox";

    /// <summary>The meter that owns the outbox instruments.</summary>
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // ... counters unchanged ...
}
```

**(b)** `src/DotNetApiPi.Api/Program.cs` — in the existing `.WithMetrics(...)` block
(currently around line 241), add the meter as the first registration:

```csharp
    .WithMetrics(metrics =>
    {
        var pipeline = metrics
            // Without this the outbox counters are collected by nothing.
            .AddMeter(OutboxMetrics.MeterName)
            .AddRuntimeInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (otelEnabled)
        {
            pipeline.AddOtlpExporter(options => options.Endpoint = otelEndpoint);
        }
    });
```

Add `using DotNetApiPi.Infrastructure.Outbox;` if it is not already present, or
qualify the constant.

Do not change anything else in the OTel block — tracing is verified working.

**(c)** README: in the observability section, list the three metric names
(`outbox.events.published`, `outbox.publish.attempts.failed`, `outbox.events.dead`),
state the meter name `DotNetApiPi.Outbox`, and note they are exported only when
`Otel:Enabled=true`. Tie `outbox.events.dead` to the existing dead-row replay runbook
as its alerting trigger.

### Acceptance criteria

1. `grep -rn "AddMeter" src` shows the registration.
2. `OutboxMetrics.MeterName` is a `const string` and is used in the `Meter`
   constructor.
3. The misleading "OTel picks up every Meter" comment is gone.
4. With `Otel__Enabled=true` and a collector attached, creating a resource results in
   `outbox.events.published` arriving under scope `DotNetApiPi.Outbox`.
5. Nothing else in the metrics pipeline regresses — the eight built-in scopes above
   still export.

### Verification

```bash
# 1. collector
cat > /tmp/otel.yaml <<'EOF'
receivers: { otlp: { protocols: { grpc: { endpoint: 0.0.0.0:4317 } } } }
exporters: { debug: { verbosity: detailed } }
service:
  pipelines:
    metrics: { receivers: [otlp], exporters: [debug] }
EOF
docker run -d --name otelcol --network dotnet_api_pi_default \
  -v /tmp/otel.yaml:/etc/otelcol/config.yaml \
  otel/opentelemetry-collector:latest --config /etc/otelcol/config.yaml

# 2. rebuild the API image FIRST (a stale image is the classic false negative here)
docker compose build api

# 3. run the API against the collector, create an event, wait one 60 s export window
docker run -d --name api-metrics-check --network dotnet_api_pi_default -p 8093:8080 \
  -e ASPNETCORE_ENVIRONMENT=Development -e Storage__Provider=mongo \
  -e "Storage__MongoConnectionString=mongodb://root:devpass123@mongo:27017/?replicaSet=rs0&authSource=admin" \
  -e Kafka__BootstrapServers=kafka:19092 \
  -e Otel__Enabled=true -e Otel__Exporter__Otlp__Endpoint=http://otelcol:4317 \
  dotnet_api_pi-api
sleep 12
curl -s -X POST http://localhost:8093/api/resources \
  -H 'Content-Type: application/json' -d '{"name":"metric probe"}'
sleep 70

# 4. must print a non-zero count
docker logs otelcol 2>&1 | grep -c "DotNetApiPi.Outbox"
docker logs otelcol 2>&1 | grep -c "outbox.events.published"

# 5. cleanup
docker rm -f otelcol api-metrics-check
```

---

## W-8 — Close the ordering gap in the transaction tests (optional hardening)

**Severity:** low · **Effort:** one test

### Problem

The two transaction tests currently divide the work like this:

- `AbortedUnitOfWork_LeavesNoOutboxRow_NorAggregateWrite` — aborts on a duplicate-key
  **aggregate** insert. Because `SaveChangesAsync` writes aggregates first and appends
  outbox rows last, the abort happens *before* the append is ever reached, so this
  test does not exercise the append at all.
- `OutboxAppend_ParticipatesInTheCallerTransaction` — proves the append joins the
  caller's session, by inserting two rows with one identity so the bulk write fails
  mid-way.

I verified this split empirically: with `AppendWithinTransactionAsync` sabotaged to
ignore the caller's session, the second test fails (correctly) and the first still
passes. The coupling is therefore covered — but by exactly one test, and only for the
current statement order. If someone reorders `SaveChangesAsync` so the outbox append
runs before the aggregate writes, no test would notice that an aborted aggregate write
can now leave a committed outbox row.

### Change

Add one integration test to `OutboxTransactionIntegrationTests`:

```
AbortAfterOutboxAppend_LeavesNoOutboxRow
```

It must abort the unit of work at a point where outbox rows have already been appended
inside the transaction, then assert `outbox_events` is empty. Construct it through the
repository so it exercises real ordering — for example, stage two aggregates where the
**second** aggregate's write fails on a seeded duplicate key, so the first aggregate's
outbox rows are already staged when the abort happens.

Keep `[Trait("Category", "Integration")]` and `[Fact(Timeout = 120_000)]`.

### Acceptance criteria

1. The test passes against current `main`.
2. The test fails if the outbox append is moved before the aggregate writes in
   `SaveChangesAsync` **and** the append is made non-transactional — i.e. it detects
   the reordering hazard the existing pair misses.

---

## Not defects — do not "fix" these

- **The Kafka integration test failing locally with `Local: Message timed out`.** That
  reproduces only under docker-in-docker, where Testcontainers' broker advertises a
  listener the test container cannot route to. The same test passes in GitHub Actions
  on a native daemon (run `32438866327`, Infrastructure 41/41 in 56 s). Do not add
  retries or relax the assertion to chase it.
- **`docsExamined: 0` when explaining the claim query on an idle collection.** All
  rows are `Published`, so the status filter matches nothing. The populated case is
  covered by `ClaimQuery_UsesIndex_WithoutBlockingSort_WithLargeBacklog`.

## Suggested order

1. **W-7** — the metrics feature is inert until this lands.
2. **W-8** — optional; do it if the transaction ordering is likely to be touched again.
3. Push, and confirm the CI run is green on the resulting commit.
