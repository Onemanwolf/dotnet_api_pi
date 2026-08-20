using DotNetApiPi.Infrastructure.Kafka;
using DotNetApiPi.Infrastructure.Outbox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DotNetApiPi.Infrastructure.Tests.Outbox;

/// <summary>
/// Tests for the <see cref="OutboxEventRelayService"/> loop against an
/// in-memory outbox store and a fake Kafka publisher: happy-path publish
/// (Published + partition/offset), failure retry with backoff until the row
/// goes Dead, and lease-expiry re-claim of a crashed claim.
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
        store.Add(PendingEvent(eventId, "ResourceCreated", resourceId));

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

        // The wire payload is the camelCase envelope.
        using var envelope = System.Text.Json.JsonDocument.Parse(publisher.LastValue!);
        Assert.Equal(eventId.ToString("D"), envelope.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("ResourceCreated", envelope.RootElement.GetProperty("eventType").GetString());
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
        store.Add(PendingEvent(eventId, "ResourceActivated", Guid.NewGuid()));

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
        // an expired lease.
        var eventId = Guid.NewGuid();
        var stuck = PendingEvent(eventId, "ResourceArchived", Guid.NewGuid());
        store.Add(stuck with
        {
            Status = OutboxEventStatus.Publishing,
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

    private static OutboxEventRecord PendingEvent(
        Guid eventId,
        string eventType,
        Guid resourceId)
    {
        var now = DateTime.UtcNow;

        return new OutboxEventRecord(
            eventId,
            eventType,
            resourceId,
            now,
            "{\"probe\":true}",
            OutboxEventStatus.Pending,
            0,
            now,
            null,
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

            // With a fixed clock, backoff-gated rows are never publishable
            // again unless the clock moves past their nextRetryAt.
            if (advanceClock)
            {
                _time.UtcNow = _time.UtcNow.AddSeconds(30);
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Thread-safe in-memory outbox store honoring the store contract:
    /// publishable = Pending (backoff gate passed or absent) or Publishing
    /// (lease expired); claims are oldest-first by creation time.
    /// </summary>
    private sealed class InMemoryOutboxStore : IOutboxEventStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<Guid, OutboxEventRecord> _rows = new();

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
                    .Where(r =>
                        (r.Status == OutboxEventStatus.Pending
                            && (r.NextRetryAtUtc is null || r.NextRetryAtUtc <= now))
                        || (r.Status == OutboxEventStatus.Publishing
                            && r.LeaseUntilUtc is not null
                            && r.LeaseUntilUtc <= now))
                    .OrderBy(static r => r.CreatedAtUtc)
                    .ThenBy(static r => r.EventId)
                    .FirstOrDefault();

                if (claimed is not null)
                {
                    var updated = claimed with
                    {
                        Status = OutboxEventStatus.Publishing,
                        LeaseUntilUtc = now.AddSeconds(leaseSeconds),
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
            int partition,
            long offset,
            DateTime publishedAtUtc,
            CancellationToken cancellationToken)
        {
            bool applied;

            lock (_gate)
            {
                var row = _rows.GetValueOrDefault(eventId);
                applied = row is not null && row.Status == OutboxEventStatus.Publishing;

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
            int attempts,
            DateTime? nextRetryAtUtc,
            string? error,
            CancellationToken cancellationToken)
        {
            bool applied;

            lock (_gate)
            {
                var row = _rows.GetValueOrDefault(eventId);
                applied = row is not null && row.Status == OutboxEventStatus.Publishing;

                if (applied)
                {
                    // A null nextRetryAtUtc signals the relay's terminal
                    // branch: the row is Dead.
                    _rows[eventId] = row! with
                    {
                        Status = nextRetryAtUtc is null
                            ? OutboxEventStatus.Dead
                            : OutboxEventStatus.Pending,
                        Attempts = attempts,
                        NextRetryAtUtc = nextRetryAtUtc,
                        LastError = error
                    };
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
