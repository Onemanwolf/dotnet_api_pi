# Kafka + Transactional Outbox — Plan

**Goal:** publish `Resource` domain events (created / activated / archived / deleted) to a Kafka
topic `resource-events` with at-least-once, ordered-per-resource delivery, using a **MongoDB
transactional outbox** as the durable hand-off, plus a containerized **consumer app** that logs
every message's content and metadata to `docker logs`.

Status: research complete — findings below are from primary sources (fetched 2026-08-20).

> **Revision 2 (2026-08-20, review round):** the implemented design evolved
> beyond v1 of this plan — see `PROGRESS.md`, "Outbox review round 2":
> MongoDB switched to an **authenticated keyFile replica set** (compose
> `mongo-keyfile-init` + authenticated `rs.initiate`); the claim gate
> collapsed to a single `claimableAtUtc` field served by the
> `status_claimableAtUtc` index (O-04/O-09); claims carry a `claimId`
> owner token so lost claims are detectable (O-05); the relay reads the
> clock per claim/mark (O-06) and publishes with bounded concurrency
> `Outbox:PublishConcurrency` (O-07); `eventType` is a stable wire name
> from `DomainEventWireTypes` and the envelope carries `schemaVersion`
> (O-08); Published rows age out via a 7-day partial TTL index (O-03); the
> README documents the dead-letter replay runbook (O-10) and that the
> outbox is Mongo-provider-only (O-11).

> **Revision 3 (2026-08-20, review round 2 — W-1…W-5):** the claim query is
> now fully index-only: the sort is on `claimableAtUtc` alone (the second key
> of `status_claimableAtUtc`), with no `_id` tie-break that forced a blocking
> in-memory SORT; MongoDB serves the claim via a `SORT_MERGE` of per-status
> index scans (verified by an integration test that asserts the explain plan
> has no blocking SORT and examines a single document over a 200-row
> backlog). Ties on `claimableAtUtc` are intentionally arbitrary — fairness,
> not ordering, is the requirement. The broker `message.timeout.ms` is now
> configurable (`Kafka:MessageTimeoutMs`, default 30000) and the relay logs
> a warning at startup when `Outbox:LeaseSeconds` cannot cover the
> worst-case batch drain (`ceil(batchSize / publishConcurrency) *
> messageTimeoutMs / 1000` ≈ 210 s at defaults); the default lease is 240 s
> accordingly. The dead `LeaseUntilUtc` field is gone — `claimableAtUtc` is
> the single claim gate. The relay emits OpenTelemetry counters on meter
> `DotNetApiPi.Outbox` (`dotnet_api_pi.outbox.published` /
> `failed_attempts` / `dead`, tagged by `event.type`), and the API wires in
> runtime/ASP.NET/HttpClient metric instrumentation with an OTLP exporter
> behind `OTEL_ENDPOINT`. A Testcontainers integration suite (real Mongo RS
> + real KRaft broker, `Category=Integration`) now proves the load-bearing
> invariants: aborted units of work leave no outbox row, a duplicate outbox
> identity inside one transaction aborts the caller's session, concurrent
> claimants never claim the same row, foreign-claim marks are no-ops,
> expired leases reclaim under a fresh `claimId`, and the full path
> publishes the stable envelope + `x-event-id` header to a real broker.

---

## 1. Research findings (deep research, 2026-08-20)

### 1.1 The outbox pattern (what it guarantees and what it does not)

Source: Chris Richardson, *Microservices Patterns* / microservices.io —
https://microservices.io/patterns/data/transactional-outbox.html (fetched).

- A command must update the aggregate **and** emit a message. Doing both "live" is not
  reliable: a message sent mid-transaction may outlive a rollback; a message sent post-commit is
  lost if the process dies in between. 2PC spanning DB + broker is out (coupling + broker
  support).
- **Solution:** store the message in the database *as part of the same transaction* that updates
  the business entity. A separate **message relay** then moves outbox rows to the broker.
- Guarantees: a message is sent **if and only if** the transaction commits; relative order is
  preserved (T1→E1 before T2→E2 for the same aggregate, given a per-aggregate partition key).
- **It is at-least-once, never exactly-once:** the relay can crash after producing but before
  recording the publish → duplicates on restart. **Consumers must be idempotent** (track event
  ids). The pattern docs call this out explicitly and note it is usually acceptable because
  consumers of message brokers must tolerate duplicates anyway.
- Two relay styles: **transaction-log tailing** (Debezium-style, CDC on the outbox) and the
  **polling publisher** (periodically query the outbox). Polling is the pragmatic choice for a
  single-service scaffold: no CDC dependency, no resume-token management, and the outbox is a
  queue by nature — seconds of latency are irrelevant.

### 1.2 MongoDB specifics

Source: MongoDB Database Manual — *Transactions*
(https://www.mongodb.com/docs/manual/core/transactions/, fetched) and *Change Streams*
(https://www.mongodb.com/docs/manual/changeStreams.md, fetched).

- Multi-document transactions are supported on **replica sets and sharded clusters only** — a
  standalone `mongod` cannot run them. ⇒ the compose MongoDB must be a **single-node replica
  set** (`mongod --replSet rs0` + one-time `rs.initiate`). This is the standard local-dev
  recipe and costs ~nothing.
- A transaction is atomic: all changes apply or all roll back; uncommitted changes are invisible
  outside the session. Writing the aggregate doc + outbox docs in one session/transaction is the
  required atomicity. Collections may even span databases; ours are in the same DB.
- Caveats worth knowing: transactions have higher cost than single-document writes (irrelevant
  at this scale); write concern for the commit is majority (trivially satisfied on a 1-node RS).
- Change streams (the "tailing" relay option) also require a replica set — available to us, but
  a change-stream relay would need resume-token persistence and cursor-reopen logic for marginal
  benefit over a 1-second poll at this throughput. **Decision: polling relay.**

### 1.3 Kafka in Docker (KRaft, official image)

Sources: Apache Kafka 4.x docs + KIP-593/848/1112 (KRaft GA → ZK deprecation → ZK **removal**
in 4.0.0), official `apache/kafka` Docker image README + `docker/examples`
(raw.githubusercontent.com/apache/kafka/trunk/docker/…, fetched), Docker Hub API (tag
verification, fetched).

- **Kafka 4.x is KRaft-only** — ZooKeeper is gone, not deprecated (KIP-1112, 4.0.0, Mar 2025).
  3.9 was the last ZK-capable line. No client-side impact (wire protocol unchanged).
- **Image: official `apache/kafka`** (maintained by the Apache project; KIP-975). Bitnami was
  considered and rejected: its open images moved under the paid *Bitnami Secure Images* program
  with reduced free update cadence. **Pinned tag: `apache/kafka:4.3.1`** — latest active 4.x
  tag on Docker Hub as of 2026-08-20 (verified via the Docker Hub tags API).
- Single-node = one combined `broker,controller` node. The image's own
  `docker-compose-files/single-node/plaintext` example (fetched) is the canonical env-var set;
  key facts from it:
  - `KAFKA_NODE_ID=1`, `KAFKA_PROCESS_ROLES=broker,controller`,
    `KAFKA_CONTROLLER_QUORUM_VOTERS=1@<host>:<controller-port>`.
  - **`KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1` must be set explicitly** — the broker default
    is 3 and a 1-node cluster cannot satisfy it.
  - Same for `transaction.state.log.*` and (new in 4.x) the
    `share.coordinator.state.topic.*` replication factors.
  - Env-var naming: `.`→`_`, `_`→`__`, `-`→`___`, prefix `KAFKA_`.
  - `CLUSTER_ID` is optional (the image ships a default); pinning it is the documented example's
    practice — we pin it so re-creating the volume has a deterministic story.
- **Two/three-listener pattern** for "containers + host access": internal listener advertised
  with the compose hostname (`kafka:19092`) for the API + consumer; a host-facing listener
  advertised as `localhost:29092` for manual tooling (kcat, IDEs); a CONTROLLER listener that is
  **never advertised**. This avoids the classic "advertise localhost → containers break /
  advertise hostname → host breaks" trap. (Matches the multi-node example in the official
  README: `PLAINTEXT` for brokers, `PLAINTEXT_HOST` for clients.)
- **Topic lifecycle: `auto.create.topics.enable=false`** (the community-consensus best practice,
  and increasingly recommended even for dev so dev exercises the production lifecycle), with the
  topic **pre-created by a one-shot init container**:
  `kafka-topics --create --if-not-exists --topic resource-events --partitions 3 --replication-factor 1`.
  (Note: 4.x CLI scripts dropped the `.sh` suffix: `kafka-topics`, not `kafka-topics.sh`.)
- **Healthcheck:** `kafka-topics --bootstrap-server localhost:<internal-port> --list` — forces a
  full metadata round-trip, proving the broker *serves clients*, not just that the JVM is up.
  Wired via `depends_on: condition: service_healthy` (this repo's existing convention).
- Sizing: 1 GiB heap (`-Xms1g -Xmx1g`), 2 GiB container limit is comfortable next to MongoDB on a
  laptop. Short retention (`retention.ms=1h` on the topic) keeps the Docker VM disk small.

### 1.4 .NET client (Confluent.Kafka)

Source: confluentinc/confluent-kafka-dotnet README (fetched) + librdkafka/Kafka producer
documentation.

- **Package: `Confluent.Kafka` 2.15.0** — latest per the README; it ships a **net10.0** target,
  so it drops straight into this project (librdkafka.redist is pulled transitively; macOS x64/
  arm64 + linux musl/glibc all covered — the consumer container gets the linux binary).
- Producer best practice:
  - **`EnableIdempotence = true`** — the broker deduplicates per-producer; it implicitly forces
    `acks=all` and the recommended in-flight request window. This is the standard "no lost or
    duplicate messages from this producer" setting (the outbox still adds the at-least-once
    guarantee across crashes).
  - `ProduceAsync` (await the delivery report) in the relay — the relay must *know* success
    before marking the outbox row published; fire-and-forget is wrong here by construction.
  - Small `LingerMs` (10) + snappy compression: negligible volume, but free correctness/efficiency.
  - **Message key = resource id** → all events for one resource land in the same partition →
    per-resource ordering without consumer-side reordering.
  - One long-lived `IProducer` per process (singleton, thread-safe), flushed/disposed on shutdown.
- Consumer best practice:
  - A **consumer group** (`dotnet-api-pi-logger`) so restarts resume from committed offsets;
    `EnableAutoCommit = false` + **manual commit after processing** (at-least-once: a crash
    between consume and commit re-delivers — the logger is idempotent, so that's fine).
  - `AutoOffsetReset = Earliest` so a fresh group replays history (the logger wants everything).
  - Single-line structured JSON to stdout → `docker logs` stays greppable.
- **Schema strategy:** plain JSON envelope, no Schema Registry. For a dev scaffold this is the
  accepted pragmatic choice: the envelope is versioned by construction (additive fields only,
  `eventType` discriminator, unknown `eventType` values are logged and skipped by consumers).

### 1.5 Delivery semantics summary (what we ship)

| Guarantee | How |
|---|---|
| Event persisted iff aggregate write committed | outbox row written **in the same MongoDB transaction** |
| Event published ≥1 time if the relay runs | polling relay + atomic claim; retries with backoff |
| No duplicates *from the producer* | `EnableIdempotence` (acks=all) |
| Order per resource | partition key = resourceId |
| Duplicates across relay crashes possible | by design — consumers must be idempotent (eventId) |
| Failed publishes never block writes | relay is a background service; failures stay in the outbox (retry → Dead) |

---

## 2. Key decisions (with rationale)

| # | Decision | Rationale |
|---|---|---|
| D1 | `apache/kafka:4.3.1`, single-node KRaft, combined controller+broker | §1.3 — current GA, ZK-free, official image |
| D2 | Listeners: `CONTROLLER://:29093` (internal), `BROKER://:19092` (compose network, advertised `kafka:19092`), `EXTERNAL://:29092` (advertised `localhost:29092`, host-mapped) | §1.3 two-listener pattern; repo convention of offset host ports (mongo already on 27018) |
| D3 | `auto.create.topics.enable=false`; `resource-events` (3 partitions, RF 1, retention 1 h) pre-created by one-shot `kafka-init` container | §1.3 — explicit topic lifecycle; 3 partitions = demonstrable parallelism with RF 1 |
| D4 | MongoDB converted to **single-node replica set** (`rs0`) in compose; one-shot `mongo-init` service runs `rs.initiate` idempotently and waits for PRIMARY | §1.2 — transactions (and change streams) require a replica set; writes need a primary |
| D5 | Outbox = `outbox_events` collection in the same Mongo DB; aggregate write + outbox insert in **one client-session transaction per `SaveChangesAsync`** | §1.1/1.2 — the only design that meets "sent iff committed" on MongoDB; also upgrades the whole unit of work to all-or-nothing (the Mongo repo's docs literally prescribe this once a replica set exists) |
| D6 | Relay = `BackgroundService` polling publisher: 1 s poll, batch 50, atomic claim (`Pending`→`Publishing` + 30 s lease), exponential backoff (5 s · 2ⁿ, max 5 attempts), then `Dead` with `LastError` | §1.1 polling-publisher pattern; claim/lease survives relay crashes (expired leases are re-claimed); Dead = visible, recoverable dead-letter state (manual re-queue = flip status back) |
| D7 | Producer: Confluent.Kafka 2.15.0, singleton `IProducer<string,string>`, `EnableIdempotence=true`, `LingerMs=10`, snappy, key = resourceId ("D" format) | §1.4 |
| D8 | Envelope: camelCase JSON `{eventId, eventType, resourceId, occurredOnUtc, payload}`; `eventId` = outbox `_id` (stable, unique) | §1.4/1.5 — stable idempotency key for consumers; `eventType` = CLR type name (stable, additive evolution) |
| D9 | New consumer app `DotNetApiPi.Consumer` (console, net10.0): group `dotnet-api-pi-logger`, `AutoOffsetReset=Earliest`, manual commit, single-line JSON logs; own Dockerfile (multi-stage, non-root `app` user like the API) | user requirement; §1.4 consumer best practices |
| D10 | New domain events: `ResourceActivatedEvent`, `ResourceArchivedEvent`, `ResourceDeletedEvent` (alongside existing `ResourceCreatedEvent`); `Activate/Archive/Delete` gain an optional `TimeProvider` param (source-compatible) | user requirement ("created and other relevant domain events"); mirrors the existing `Create`/`ResourceCreatedEvent` pattern |
| D11 | Outbox + relay are active **only when `Storage__Provider=mongo`**; the relay additionally no-ops (with a clear log) when `Kafka:BootstrapServers` is empty | keeps the zero-dependency SQLite dev path intact; outbox semantics only exist where the transactional store exists (user: "use Mongo for the outbox table") |
| D12 | No W3C trace headers in v1 (noted as follow-up) | the relay runs outside the request scope; wiring correlation/trace context through the outbox row is a clean extension but out of scope now |

## 3. Architecture

```
                       ┌──────────────────────────────────────────────────┐
                       │  API (Storage__Provider=mongo)                   │
 POST /api/resources   │                                                  │
 ─────────────────────▶│  CommandHandler → MongoResourceRepository        │
                       │    ┌─ MongoDB client session / TRANSACTION ──┐   │
                       │    │  1. insert/replace/delete aggregate doc │   │
                       │    │  2. insert outbox_events rows (events)  │   │
                       │    └────────────── commit / abort ────────────┘   │
                       │    3. after commit: in-memory subscribers        │
                       │       (existing log subscriber — unchanged)      │
                       │                                                  │
                       │  OutboxEventRelayService (BackgroundService)     │
                       │    poll outbox_events (Pending / stale leases)   │
                       │    claim → produce → mark Published/Retry/Dead   │
                       └───────────────────────────┬──────────────────────┘
                                                   │ ProduceAsync (key = resourceId)
                                                   ▼
                    Kafka 4.3.1 (KRaft single node, apache/kafka:4.3.1)
                    topic: resource-events (3 partitions, RF 1, 1h retention)
                                                   │
                                                   ▼  consumer group "dotnet-api-pi-logger"
                       DotNetApiPi.Consumer (container)
                         single-line JSON to stdout → docker logs:
                         topic, partition, offset, key, headers, timestamp, body
```

### Outbox document shape (`outbox_events`)

```jsonc
{
  "_id": "0e5c9e5f-... (Guid, = eventId, unique)",
  "eventType": "ResourceCreated",       // CLR type name — stable discriminator
  "resourceId": "8f14e45f-...",         // partition key + consumer filter
  "occurredOnUtc": "2026-08-20T15:00:00Z",
  "payload": "{ \"resourceId\": \"...\", \"occurredOn\": \"...\" }",  // camelCase JSON of the domain event
  "status": 0,                          // 0=Pending 1=Publishing 2=Published 3=Dead
  "attempts": 0,
  "createdAtUtc": "...",
  "nextRetryAtUtc": null,               // backoff gate on failures
  "leaseUntilUtc": null,                // claim lease (Publishing rows)
  "publishedAtUtc": null,
  "topicPartition": null,
  "offset": null,
  "lastError": null
}
```

Index: `{ status: 1, createdAtUtc: 1 }` — the relay's working set; created at startup
idempotently by the initializer.

### Kafka message

- **key:** resourceId, lowercase `"D"` format (per-resource ordering)
- **value:** the envelope above (minus outbox-internal fields — partition/offset stay in the DB)
- **headers:** `x-event-id` (= eventId) for cheap consumer-side idempotency without parsing the body

## 4. File changes

**Domain** — `src/DotNetApiPi.Domain/`
- `Events/ResourceActivatedEvent.cs`, `Events/ResourceArchivedEvent.cs`, `Events/ResourceDeletedEvent.cs` (new)
- `Entities/Resource.cs` — `Activate/Archive` raise events; new `Delete(TimeProvider?)` raises `ResourceDeletedEvent` (event-only, no version bump — the delete path is version-guarded via the loaded version, like today)

**Application** — `src/DotNetApiPi.Application/`
- `Commands/DeleteResourceCommandHandler.cs` — call `resource.Delete()` before `RemoveAsync` (the event must be staged before `SaveChangesAsync`)

**Infrastructure** — `src/DotNetApiPi.Infrastructure/`
- `Outbox/OutboxEventStatus.cs`, `Outbox/OutboxEventRecord.cs`, `Outbox/OutboxEventDocument.cs`, `Outbox/OutboxEventEnvelope.cs`, `Outbox/IOutboxEventStore.cs`, `Outbox/MongoOutboxEventStore.cs`, `Outbox/OutboxEventRelayService.cs` (new)
- `Kafka/KafkaOptions.cs`, `Kafka/IKafkaEventPublisher.cs`, `Kafka/ConfluentKafkaEventPublisher.cs` (new)
- `Repositories/MongoResourceRepository.cs` — one client-session transaction per UoW: aggregate write + outbox insert; post-commit in-memory dispatch unchanged
- `MongoInfrastructureInitializer.cs`- `DependencyInjection.cs` — wire `KafkaOptions`, outbox store/collection, publisher (singleton `IAsyncDisposable`), relay (`IHostedService`, mongo-only + requires bootstrap servers)
- `appsettings.json` (Api) — add `"Kafka": { "BootstrapServers": "localhost:29092", "Topic": "resource-events" }`

**New project** — `src/DotNetApiPi.Consumer/`
- `DotNetApiPi.Consumer.csproj` (net10.0, `Confluent.Kafka`, `Microsoft.Extensions.Hosting`)
- `Program.cs` — generic host, console logger, IConsumer loop with manual commit + graceful shutdown
- `Dockerfile` — multi-stage `sdk:10.0` → `runtime:10.0`, non-root `app` user (mirrors the API Dockerfile)

**Build/compose**
- `Directory.Packages.props` — `Confluent.Kafka 2.15.0`, `Microsoft.Extensions.Hosting 10.0.11`
- `DotNetApiPi.sln` — add `DotNetApiPi.Consumer`
- `docker-compose.yml`:
  - `mongo` — `command: mongod --replSet rs0 --bind_ip_all` (rest unchanged, auth kept)
  - `mongo-init` — new one-shot: `rs.initiate` idempotently, wait for PRIMARY, exit 0
  - `kafka` — new: `apache/kafka:4.3.1`, KRaft single node, 3 listeners, RF=1 settings, volume, healthcheck
  - `kafka-init` — new one-shot: create `resource-events` (if-not-exists)
  - `api` — env: `Storage__MongoConnectionString` gains `?replicaSet=rs0&authSource=admin`, add `Kafka__BootstrapServers=kafka:19092` + `Kafka__Topic=resource-events`; `depends_on` += `kafka: service_healthy`, `kafka-init: service_completed_successfully`, `mongo-init: service_completed_successfully`
  - `consumer` — new: built from `src/DotNetApiPi.Consumer`, env `Kafka__BootstrapServers=kafka:19092`, group id; depends on `kafka: service_healthy`

**Tests**
- `tests/DotNetApiPi.Domain.Tests/Entities/ResourceDomainEventTests.cs` (new) — each lifecycle transition raises exactly one correctly-typed event with the right id/timestamp; reconstitution raises nothing; `Delete` event semantics
- `tests/DotNetApiPi.Infrastructure.Tests/Outbox/OutboxEventRelayTests.cs` (new) — relay with in-memory store + fake publisher: pending→published (with partition/offset), failure→retry backoff, max attempts→Dead, lease expiry reclaim, batch ordering
- existing suites untouched and green (CI: `dotnet build --warnaserror` + `dotnet test`)

**Docs**
- `README.md`, `PROGRESS.md` — short addenda (feature + how to run)

## 5. Verification plan

1. `dotnet build -c Release -warnaserror` + `dotnet test` — local, must be green (CI parity).
2. `docker compose up --build -d` — full stack (mongo RS + kafka + api + consumer).
3. `curl -sX POST localhost:8090/api/resources -H 'content-type: application/json' -d '{"name":"Kafka probe"}'` → 201.
4. `docker compose logs consumer` → JSON line with topic `resource-events`, key = resource id, body `ResourceCreated`.
5. `docker compose logs api` → outbox relay publish line; `mongosh` check: `outbox_events` row `status=2 (Published)`, `offset` set.
6. Activate + archive the resource → two more events appear in the consumer logs, in order.
7. `kcat -t resource-events` (host, port 29092) as a manual sanity check that the external listener works.

## 6. Risks & known trade-offs

- **At-least-once is a feature, not a bug:** relay crash between produce and mark-published ⇒
  duplicate on restart (mitigated: narrow window, idempotent consumers via `x-event-id`).
- **Single-node RS** gives transaction durability, not fault tolerance — acceptable for local dev
  (a 3-node RS in compose is the documented upgrade path; the code doesn't care).
- **Existing volume data:** `mongo-data` created under standalone mode is compatible with
  single-node RS conversion (same storage format; `rs.initiate` on first boot of the new mode).
  A full `docker compose down -v` is always the clean slate.
- **Mongo UoW atomicity upgrade:** with D5, a concurrency conflict on the *last* aggregate now
  rolls back earlier aggregates in the same `SaveChangesAsync` (previously: partial apply).
  This is the standard UoW contract (and what the EF provider already does) — noted because the
  Mongo repo's XML docs describe the old behavior; the docs get updated.
- **Kafka heap/IO:** 1 GiB heap is a minimum; the dev box runs this next to MongoDB — keep
  retention at 1 h so the Docker VM disk doesn't grow.
- **No trace context in headers yet** (D12): follow-up = store correlation id on the outbox row
  at write time, emit `traceparent`/`x-correlation-id` headers in the relay.
