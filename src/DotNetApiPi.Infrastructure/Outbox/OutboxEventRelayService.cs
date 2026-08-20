using DotNetApiPi.Infrastructure.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Background service that moves committed outbox rows to Kafka (the
/// "message relay" of the transactional-outbox pattern).
/// <para>
/// Loop: poll the outbox for publishable rows (in creation order), claim
/// each one atomically, publish to Kafka (awaiting the broker
/// acknowledgement), then mark the row <c>Published</c> with the partition
/// and offset — or record a failed attempt (backoff retry, or
/// <c>Dead</c> once the attempt budget is exhausted).
/// </para>
/// <para>
/// Delivery is at-least-once by design: a crash between the broker ack and
/// the <c>Published</c> update re-delivers the event after the lease
/// expires. Consumers de-duplicate on the stable <c>x-event-id</c> header /
/// <c>eventId</c> field (see <see cref="OutboxEventEnvelope"/>).
/// </para>
/// </summary>
public sealed class OutboxEventRelayService : IHostedService
{
    private readonly IOutboxEventStore _store;
    private readonly IKafkaEventPublisher _publisher;
    private readonly OutboxOptions _outboxOptions;
    private readonly string _topic;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxEventRelayService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _running;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="OutboxEventRelayService"/> class.
    /// </summary>
    /// <param name="store">The outbox event store.</param>
    /// <param name="publisher">The Kafka publisher.</param>
    /// <param name="outboxOptions">The relay tuning options.</param>
    /// <param name="kafkaOptions">The Kafka connection options (topic and
    /// bootstrap servers).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> (defaults to the host-
    /// registered system clock; tests may supply a fixed clock).
    /// </param>
    public OutboxEventRelayService(
        IOutboxEventStore store,
        IKafkaEventPublisher publisher,
        IOptions<OutboxOptions> outboxOptions,
        IOptions<KafkaOptions> kafkaOptions,
        ILogger<OutboxEventRelayService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        ArgumentNullException.ThrowIfNull(kafkaOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _publisher = publisher;
        _outboxOptions = outboxOptions.Value;
        _topic = kafkaOptions.Value.Topic;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_running is null)
        {
            _cts.Dispose();
            return;
        }

        // Give the loop a bounded grace period to drain the current cycle
        // (an in-flight publish finishes; the row is left Publishing and is
        // re-claimed via its lease, so nothing is lost).
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Outbox relay did not drain within the shutdown grace period; unacknowledged rows will be re-claimed after their lease expires.");
        }
        catch (OperationCanceledException)
        {
            // Host-wide cancellation: the relay is already stopping.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// The relay loop.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Outbox relay started (topic '{Topic}', poll {PollMs} ms, batch {BatchSize}, max attempts {MaxAttempts}, lease {LeaseSeconds} s).",
            _topic,
            _outboxOptions.PollIntervalMs,
            _outboxOptions.BatchSize,
            _outboxOptions.MaxAttempts,
            _outboxOptions.LeaseSeconds);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = _time.GetUtcNow().UtcDateTime;
                var processed = 0;

                for (var i = 0; i < _outboxOptions.BatchSize; i++)
                {
                    var record = await _store
                        .ClaimNextPublishableAsync(
                            now,
                            _outboxOptions.LeaseSeconds,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (record is null)
                    {
                        break;
                    }

                    processed++;

                    // Never let a single row's failure break the loop.
                    await ProcessClaimedAsync(record, cancellationToken).ConfigureAwait(false);
                }

                // Work found: go straight to the next batch. Idle: back off
                // for one poll interval.
                if (processed == 0)
                {
                    await Task
                        .Delay(TimeSpan.FromMilliseconds(_outboxOptions.PollIntervalMs), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }

        _logger.LogInformation("Outbox relay stopped.");
    }

    /// <summary>
    /// Publishes one claimed row and records the outcome; absorbs all
    /// failures (recording them on the row instead).
    /// </summary>
    private async Task ProcessClaimedAsync(
        OutboxEventRecord record,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = OutboxEventEnvelope.FromRecord(record);

            // Key = resource id => all events of one resource land in the
            // same partition (per-resource ordering on the consumer side).
            var result = await _publisher
                .PublishAsync(
                    record.ResourceId.ToString("D"),
                    envelope.Serialize(),
                    new Dictionary<string, string>
                    {
                        ["x-event-id"] = record.EventId.ToString("D")
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // Conditional on the row still being Publishing: if the lease
            // expired mid-publish and a new claimant took over, do not
            // overwrite their state.
            var recorded = await _store
                .MarkPublishedAsync(
                    record.EventId,
                    result.Partition,
                    result.Offset,
                    _time.GetUtcNow().UtcDateTime,
                    cancellationToken)
                .ConfigureAwait(false);

            if (recorded)
            {
                _logger.LogInformation(
                    "Published outbox event {EventId} ({EventType}) to {Topic}/{Partition}@{Offset}.",
                    record.EventId,
                    record.EventType,
                    result.Topic,
                    result.Partition,
                    result.Offset);
            }
            else
            {
                _logger.LogWarning(
                    "Publish record for outbox event {EventId} not applied (claim lost in the meantime).",
                    record.EventId);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown during produce: the row stays Publishing; its lease
            // expires and it is re-claimed (the record may or may not have
            // reached the broker — at-least-once, consumers de-duplicate).
            _logger.LogInformation(
                "Shutting down; outbox event {EventId} stays Publishing and will be re-claimed after its lease expires.",
                record.EventId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordFailureAsync(record, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a failed publish attempt: backoff retry, or Dead once the
    /// attempt budget is exhausted.
    /// </summary>
    private async Task RecordFailureAsync(
        OutboxEventRecord record,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var newAttempts = record.Attempts + 1;

        try
        {
            if (newAttempts >= _outboxOptions.MaxAttempts)
            {
                var dead = await _store
                    .MarkFailedAsync(
                        record.EventId,
                        newAttempts,
                        nextRetryAtUtc: null,
                        exception.Message,
                        CancellationToken.None) // shutdown must not lose the Dead state
                    .ConfigureAwait(false);

                if (dead)
                {
                    _logger.LogCritical(
                        "Outbox event {EventId} ({EventType}, resource {ResourceId}) is now DEAD after {Attempts} publish attempts: {Error}",
                        record.EventId,
                        record.EventType,
                        record.ResourceId,
                        newAttempts,
                        exception.Message);
                }
            }
            else
            {
                // Exponential backoff: 5 s, 10 s, 20 s, … (base * 2^(n-1)).
                var delayMs = _outboxOptions.BaseRetryDelayMs *
                    Math.Pow(2, newAttempts - 1);
                var nextRetryAt = _time.GetUtcNow().UtcDateTime
                    + TimeSpan.FromMilliseconds(delayMs);

                await _store
                    .MarkFailedAsync(
                        record.EventId,
                        newAttempts,
                        nextRetryAt,
                        exception.Message,
                        cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogWarning(
                    exception,
                    "Publish attempt {Attempts}/{MaxAttempts} for outbox event {EventId} ({EventType}) failed; retrying at {NextRetryAt:O}.",
                    newAttempts,
                    _outboxOptions.MaxAttempts,
                    record.EventId,
                    record.EventType,
                    nextRetryAt);
            }
        }
        catch (Exception storeException)
        {
            // The row could not be updated (e.g. the store itself is down).
            // Log loudly and keep going: the row remains Publishing and is
            // re-claimed after its lease expires, so the event is not lost.
            _logger.LogError(
                storeException,
                "Could not record the failed publish of outbox event {EventId} on the outbox row: {Error}",
                record.EventId,
                exception.Message);
        }
    }
}
