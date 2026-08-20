using DotNetApiPi.Infrastructure.Kafka;
using DotNetApiPi.Infrastructure.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetApiPi.Infrastructure.Tests.Outbox;

/// <summary>
/// Tests for the <see cref="OutboxEventRelayService"/> loop against an
/// in-memory outbox store and a fake Kafka publisher: happy-path publish
/// (Published + partition/offset + envelope schema), failure retry with
/// backoff until the row goes Dead, lease-expiry re-claim of a crashed
/// claim, and graceful handling of a lost claim (lease expired, another
/// relay owns the row — the mark is a no-op and the event is re-delivered
/// under the new claim).
/// </summary>
public sealed class OutboxEventRelayServiceTests : IDisposable
{
    private readonly List<OutboxEventRelayService> _relays = [];
    private readonly MutableTimeProvider _time = new();

    public void Dispose()
    {
        foreach (var relay in _relays)
        {
            relay.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    [Fact(Timeout = 20_000)]
    public async Task Relay_PublishesPendingRow_AndMarksPublishedWithPartitionAndOffset()
    {
        var store = new InMemoryOutboxStore();
        var publisher = new FakePublisher(
            static (_, _, _, _) => new KafkaPublishResult("resource-events", 2, 41));
        var relay = StartRelay(store, publisher, new OutboxOptions
        {
            PollIntervalMs = 10,
            BatchSize = 1
        });

        var resourceId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        store.Add(PendingEvent(eventId, "resource.created.v1", resourceId));

        await UntilAsync(
            async () =>
            {
                var row = store.Find(eventId);
                return row is not null && row.Status == OutboxEventStatus.Published;
            },
            "row to be Published");

        var row = store.Find(eventId)!;
        Assert.Equal(2, row.TopicPartition);
        Assert.Equal(41L, row.Offset);
        Assert.Null(row.LastError);

        // Key = resource id (stable "D" format) and the x-event-id header
        // carry the identities the consumer de-duplicates on.
        Assert.Equal(resourceId.ToString("D"), publisher.LastKey);
        Assert.Equal(eventId.ToString("D"), publisher.LastHeaders?["x-event-id"]);

        // The wire payload is the camelCase envelope with its schema
        // version and stable event-type name.
        using var envelope = System.Text.Json.JsonDocument.Parse(publisher.LastValue!);
        Assert.Equal(1, envelope.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(eventId.ToString("D"), envelope.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("resource.created.v1", envelope.RootElement.GetProperty("eventType").GetString());
    }

    [Fact(Timeout = 20_000)]
    public async Task Relay_RetriesWithBackoff_UntilRowIsDead()
    {
        var store = new InMemoryOutboxStore();
        var publisher = new FakePublisher(
            static (_, _, _, _) => throw new InvalidOperationException("broker down"));
        var relay = StartRelay(store, publisher, new OutboxOptions
        {
            PollIntervalMs = 10,
            BatchSize = 1,
            MaxAttempts = 3,
            BaseRetryDelayMs = 1_000
        });

        var eventId = Guid.NewGuid();
        store.Add(PendingEvent(eventId, "resource.activated.v1", Guid.NewGuid()));

        await UntilAsync(
            async () =>
            {
                var row = store.Find(eventId);
                return row is not null && row.Status == OutboxEventStatus.Dead;
            },
            "row to be Dead",
            advanceClock: true);

        var row = store.Find(eventId)!;
        Assert.Equal(3, row.Attempts);
        Assert.Equal("broker down", row.LastError);
        Assert.Equal(3, publisher.CallCount);
    }

    [Fact(Timeout = 20_000)]
    public async Task Relay_ReclaimsPublishingRow_WhenLeaseExpires()
    {
        var store = new InMemoryOutboxStore();
        var publisher = new FakePublisher(
            static (_, _, _, _) => new KafkaPublishResult("resource-events", 0, 7));
        var relay = StartRelay(store, publisher, new OutboxOptions
        {
            PollIntervalMs = 10,
            BatchSize = 1,
            LeaseSeconds = 30
        });

        // A claim left behind by a crashed relay instance: Publishing with
        // an expired lease (the claim gate has passed, so the row is
        // publishable again).
        var eventId = Guid.NewGuid();
        var stuck = PendingEvent(eventId, "resource.archived.v1", Guid.NewGuid());
        store.Add(stuck with
        {
            Status = OutboxEventStatus.Publishing,
            ClaimableAtUtc = _time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(60),
            LeaseUntilUtc = _time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(60)
        });

        await UntilAsync(
            async () =>
            {
                var row = store.Find(eventId);
                return row is not null && row.Status == OutboxEventStatus.Published;
            },
            "row to be re-claimed and Published");

        Assert.Equal(0, store.Find(eventId)!.TopicPartition);
    }

    [Fact(Timeout = 20_000)]
    public async Task Relay_SurvivesLostClaim_AndRedeliversUnderNewClaim()
    {
        // O-05: the lease must be a real ownership guarantee. If the row is
        // taken over while this relay is mid-publish (the mark finds the row
        // no longer owned by its claim id), the relay must NOT treat the
        // failure as fatal and NOT corrupt the row — and the event must
        // still be delivered, under the new claim.
        var store = new InMemoryOutboxStore
        {
            TakeOverBeforeFirstMark = true
        };
        var publisher = new FakePublisher(
            static (_, _, _, _) => new KafkaPublishResult("resource-events", 1, 3));
        var relay = StartRelay(store, publisher, new OutboxOptions
        {
            PollIntervalMs = 10,
            BatchSize = 1,
            LeaseSeconds = 30
        });

        var eventId = Guid.NewGuid();
        store.Add(PendingEvent(eventId, "resource.deleted.v1", Guid.NewGuid()));

        await UntilAsync(
            async () =>
            {
                var row = store.Find(eventId);
                return row is not null && row.Status == OutboxEventStatus.Published;
            },
            "row to be Published after the lost claim",
            advanceClock: true);

        // Delivered twice (the publish before the takeover, plus the
        // re-delivery under the new claim): at-least-once, de-duplicated by
        // consumers on x-event-id.
        Assert.Equal(2, publisher.CallCount);

        // The row was not corrupted by the failed mark: it carries the new
        // claim's outcome.
        var row = store.Find(eventId)!;
        Assert.NotEqual(Guid.Empty, row.ClaimId);
        Assert.Equal(1, row.TopicPartition);
        Assert.Equal(3L, row.Offset);
    }

    private OutboxEventRelayService StartRelay(
        InMemoryOutboxStore store,
        FakePublisher publisher,
        OutboxOptions outboxOptions)
    {
        var relay = new OutboxEventRelayService(
            store,
            publisher,
            Options.Create(outboxOptions),
            Options.Create(new KafkaOptions { BootstrapServers = "test:19092" }),
            NullLogger<OutboxEventRelayService>.Instance,
            _time);

        _relays.Add(relay);
        relay.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return relay;
    }

    private OutboxEventRecord PendingEvent(
        Guid eventId,
        string eventType,
        Guid resourceId)
    {
        // Use the test clock (not the wall clock): the relay and the store
        // both evaluate claim gates against _time.
        var now = _time.GetUtcNow().UtcDateTime;

        return new OutboxEventRecord(
            eventId,
            eventType,
            resourceId,
            now,
            "{\"probe\":true}",
            OutboxEventStatus.Pending,
            0,
            now,
            now, // claimable immediately
            Guid.Empty, // assigned on first claim
            null,
            null,
            null,
            null,
            null);
    }

    private async Task UntilAsync(
        Func<Task<bool>> condition,
        string description,
        bool advanceClock = false,
        int timeoutMs = 15_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (true)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            // With a fixed clock, backoff-gated rows (and expired leases)
            // are never publishable again unless the clock moves past their
            // claimableAtUtc.
            if (advanceClock)
            {
                _time.UtcNow = _time.UtcNow.AddSeconds(30);
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Thread-safe in-memory outbox store honoring the store contract:
    /// publishable = Pending or Publishing with the claim gate
    /// (<c>claimableAtUtc</c>) passed; claims are oldest-first by claim
    /// gate; marks apply only while the caller's claim id still matches
    /// (a lost claim is a no-op that returns <c>false</c>).
    /// </summary>
    private sealed class InMemoryOutboxStore : IOutboxEventStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, OutboxEventRecord> _rows = new();

        /// <summary>
        /// When set, the next <see cref="MarkPublishedAsync"/> simulates a
        /// takeover by a new claimant first (the mark then finds the row no
        /// longer owned by the caller's claim id and fails), clearing the
        /// flag after use.
        /// </summary>
        public bool TakeOverBeforeFirstMark { get; set; }

        public void Add(OutboxEventRecord record)
        {
            lock (_gate)
            {
                _rows[record.EventId] = record;
            }
        }

        public OutboxEventRecord? Find(Guid eventId)
        {
            lock (_gate)
            {
                return _rows.GetValueOrDefault(eventId);
            }
        }

        public Task AppendWithinTransactionAsync(
            IReadOnlyList<OutboxEventRecord> records,
            MongoDB.Driver.IClientSessionHandle session,
            CancellationToken cancellationToken)
        {
            // The in-memory fake has no real transactions; the session is
            // accepted for contract conformance and ignored.
            _ = session;

            lock (_gate)
            {
                foreach (var record in records)
                {
                    _rows[record.EventId] = record;
                }
            }

            return Task.CompletedTask;
        }

        public Task<OutboxEventRecord?> ClaimNextPublishableAsync(
            DateTime now,
            int leaseSeconds,
            CancellationToken cancellationToken)
        {
            OutboxEventRecord? claimed = null;

            lock (_gate)
            {
                claimed = _rows.Values
                    .Where(static r => r.Status is OutboxEventStatus.Pending or OutboxEventStatus.Publishing)
                    .Where(r => r.ClaimableAtUtc <= now)
                    .OrderBy(static r => r.ClaimableAtUtc)
                    .ThenBy(static r => r.EventId)
                    .FirstOrDefault();

                if (claimed is not null)
                {
                    var leaseUntil = now.AddSeconds(leaseSeconds);
                    var updated = claimed with
                    {
                        Status = OutboxEventStatus.Publishing,
                        ClaimId = Guid.NewGuid(),
                        ClaimableAtUtc = leaseUntil,
                        LeaseUntilUtc = leaseUntil,
                        LastError = null
                    };
                    _rows[claimed.EventId] = updated;
                    claimed = updated;
                }
            }

            return Task.FromResult(claimed);
        }

        public Task<bool> MarkPublishedAsync(
            Guid eventId,
            Guid claimId,
            int partition,
            long offset,
            DateTime publishedAtUtc,
            CancellationToken cancellationToken)
        {
            bool applied;

            lock (_gate)
            {
                var row = _rows.GetValueOrDefault(eventId);

                // Simulated takeover: the row is no longer owned by the
                // caller's claim id when the mark arrives.
                if (row is not null && TakeOverBeforeFirstMark)
                {
                    TakeOverBeforeFirstMark = false;
                    var takenOver = row with
                    {
                        ClaimId = Guid.NewGuid(),
                        ClaimableAtUtc = row.ClaimableAtUtc,
                        LeaseUntilUtc = row.LeaseUntilUtc
                    };
                    _rows[eventId] = takenOver;
                    row = takenOver;
                }

                applied = row is not null
                    && row.Status == OutboxEventStatus.Publishing
                    && row.ClaimId == claimId;

                if (applied)
                {
                    _rows[eventId] = row! with
                    {
                        Status = OutboxEventStatus.Published,
                        PublishedAtUtc = publishedAtUtc,
                        TopicPartition = partition,
                        Offset = offset,
                        LeaseUntilUtc = null,
                        LastError = null
                    };
                }
            }

            return Task.FromResult(applied);
        }

        public Task<bool> MarkFailedAsync(
            Guid eventId,
            Guid claimId,
            int attempts,
            DateTime? retryAtUtc,
            string? error,
            CancellationToken cancellationToken)
        {
            bool applied;

            lock (_gate)
            {
                var row = _rows.GetValueOrDefault(eventId);

                applied = row is not null
                    && row.Status == OutboxEventStatus.Publishing
                    && row.ClaimId == claimId;

                if (applied)
                {
                    // A null retryAtUtc signals the relay's terminal branch:
                    // the row is Dead. Otherwise it goes back to Pending,
                    // claimable again after the backoff.
                    var updated = row! with
                    {
                        Status = retryAtUtc is null
                            ? OutboxEventStatus.Dead
                            : OutboxEventStatus.Pending,
                        Attempts = attempts,
                        LeaseUntilUtc = null,
                        LastError = error
                    };

                    if (retryAtUtc is not null)
                    {
                        updated = updated with { ClaimableAtUtc = retryAtUtc.Value };
                    }

                    _rows[eventId] = updated;
                }
            }

            return Task.FromResult(applied);
        }
    }

    /// <summary>
    /// Fake publisher: records the last call and returns (or throws) from a
    /// pluggable behavior.
    /// </summary>
    private sealed class FakePublisher : IKafkaEventPublisher
    {
        private readonly Func<
            string,
            string,
            IReadOnlyDictionary<string, string>?,
            CancellationToken,
            KafkaPublishResult> _behavior;
        private int _callCount;

        public FakePublisher(
            Func<
                string,
                string,
                IReadOnlyDictionary<string, string>?,
                CancellationToken,
                KafkaPublishResult> behavior)
        {
            _behavior = behavior;
        }

        public int CallCount
        {
            get => Volatile.Read(ref _callCount);
        }

        public string? LastKey { get; private set; }

        public string? LastValue { get; private set; }

        public IReadOnlyDictionary<string, string>? LastHeaders { get; private set; }

        public Task<KafkaPublishResult> PublishAsync(
            string key,
            string value,
            IReadOnlyDictionary<string, string>? headers,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            LastKey = key;
            LastValue = value;
            LastHeaders = headers;

            return Task.FromResult(_behavior(key, value, headers, cancellationToken));
        }
    }

    /// <summary>
    /// A TimeProvider whose clock the test can advance, so backoff-gated
    /// rows become publishable on demand.
    /// </summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
