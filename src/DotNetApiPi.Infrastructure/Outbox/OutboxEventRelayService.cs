using DotNetApiPi.Infrastructure.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Background service that moves committed outbox rows to Kafka (the
/// "message relay" of the transactional-outbox pattern).
/// <para>
/// Loop: claim up to <c>BatchSize</c> publishable rows (each claim uses a
/// fresh clock reading, so leases are measured from the moment of
/// claiming), then publish the batch with bounded concurrency — grouped by
/// resource id so a resource's events stay sequential while other
/// resources drain in parallel. Each publish marks its row
/// <c>Published</c> with the partition and offset (or records a failed
/// attempt: backoff retry, or <c>Dead</c> once the attempt budget is
/// exhausted). Mark operations carry the row's claim id, so a claim lost to
/// a lease-expired takeover is a detectable no-op, never an overwrite.
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
    private readonly int _messageTimeoutMs;
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
        _messageTimeoutMs = kafkaOptions.Value.MessageTimeoutMs;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Configuration guard: a claim lease must outlive the worst-case
        // drain of one full batch (batch / concurrency sequential rounds,
        // each bounded by the broker message timeout). Below that, a slow
        // broker can expire a claim mid-batch and a concurrent relay
        // re-claims and double-publishes — at-least-once tolerates the
        // duplicate, but the setting is misconfigured, not a behavior.
        var worstCaseDrainSeconds = Math.Ceiling(
                (double)_outboxOptions.BatchSize / _outboxOptions.PublishConcurrency)
            * (_messageTimeoutMs / 1000.0);

        if (_outboxOptions.LeaseSeconds < worstCaseDrainSeconds)
        {
            _logger.LogWarning(
                "Outbox lease ({LeaseSeconds} s) is below the worst-case drain time for one batch: {BatchSize} events / {Concurrency} concurrent / {TimeoutMs} ms broker message timeout ≈ {DrainSeconds} s. A slow broker can expire claims mid-batch and a concurrent relay will double-publish. Raise Outbox:LeaseSeconds (or lower Kafka:MessageTimeoutMs).",
                _outboxOptions.LeaseSeconds,
                _outboxOptions.BatchSize,
                _outboxOptions.PublishConcurrency,
                _messageTimeoutMs,
                worstCaseDrainSeconds);
        }

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
            "Outbox relay started (topic '{Topic}', poll {PollMs} ms, batch {BatchSize}, concurrency {Concurrency}, max attempts {MaxAttempts}, lease {LeaseSeconds} s, message timeout {TimeoutMs} ms).",
            _topic,
            _outboxOptions.PollIntervalMs,
            _outboxOptions.BatchSize,
            _outboxOptions.PublishConcurrency,
            _outboxOptions.MaxAttempts,
            _outboxOptions.LeaseSeconds,
            _messageTimeoutMs);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var batch = await ClaimBatchAsync(cancellationToken).ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    // Idle: back off for one poll interval.
                    await Task
                        .Delay(TimeSpan.FromMilliseconds(_outboxOptions.PollIntervalMs), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // Publish the batch with bounded concurrency, grouped by
                // resource: one resource's events stay sequential (same
                // Kafka key => same partition), while a slow round-trip on
                // one resource no longer blocks every other resource.
                var groups = batch.GroupBy(static record => record.ResourceId).ToList();

                using var throttle = new SemaphoreSlim(
                    _outboxOptions.PublishConcurrency,
                    _outboxOptions.PublishConcurrency);

                var groupTasks = groups
                    .Select(group => RunResourceGroupAsync(group, throttle, cancellationToken))
                    .ToArray();

                await Task.WhenAll(groupTasks).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }

        _logger.LogInformation("Outbox relay stopped.");
    }

    /// <summary>
    /// Claims up to <c>BatchSize</c> publishable rows, reading the clock
    /// fresh for every claim so each lease is measured from its own claim
    /// instant (a stale cycle-wide timestamp would make later rows' leases
    /// already expired by the time the broker is degraded).
    /// </summary>
    private async Task<List<OutboxEventRecord>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<OutboxEventRecord>();

        for (var i = 0; i < _outboxOptions.BatchSize; i++)
        {
            var record = await _store
                .ClaimNextPublishableAsync(
                    _time.GetUtcNow().UtcDateTime,
                    _outboxOptions.LeaseSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            if (record is null)
            {
                break;
            }

            batch.Add(record);
        }

        return batch;
    }

    /// <summary>
    /// Runs one resource's claimed rows (in claim order) under the
    /// concurrency throttle.
    /// </summary>
    private async Task RunResourceGroupAsync(
        IEnumerable<OutboxEventRecord> group,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var record in group)
            {
                // Never let a single row's failure break the group (or the
                // loop): failures are recorded on the row instead.
                await ProcessClaimedAsync(record, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            throttle.Release();
        }
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

            // Conditional on the row still being Publishing AND still
            // carrying this claim's id: if the lease expired mid-publish
            // and a new claimant took over, do not overwrite their state.
            var recorded = await _store
                .MarkPublishedAsync(
                    record.EventId,
                    record.ClaimId,
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

                OutboxMetrics.PublishedEvents.Add(1, OutboxMetrics.EventTags(record.EventType).ToArray());
            }
            else
            {
                // Lost the claim (lease expired, another relay owns the row
                // now). The other claimant will publish the event; at-least-
                // once delivery plus x-event-id de-duplication absorbs the
                // possible duplicate.
                _logger.LogWarning(
                    "Publish record for outbox event {EventId} not applied — claim lost to another relay; the event will be (re-)delivered by the new claimant.",
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
    /// Records a failed publish attempt with exponential backoff; sends the
    /// row to <c>Dead</c> once the attempt budget is exhausted.
    /// </summary>
    private async Task RecordFailureAsync(
        OutboxEventRecord record,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var attempts = record.Attempts + 1;

        var retryAtUtc = attempts < _outboxOptions.MaxAttempts
            ? _time.GetUtcNow().UtcDateTime.AddMilliseconds(
                (long)_outboxOptions.BaseRetryDelayMs * Math.Pow(2, attempts - 1))
            : (DateTime?)null;

        var lastError = exception.Message.Length > 500
            ? exception.Message[..500]
            : exception.Message;

        var recorded = await _store
            .MarkFailedAsync(
                record.EventId,
                record.ClaimId,
                attempts,
                retryAtUtc,
                lastError,
                cancellationToken)
            .ConfigureAwait(false);

        var tags = OutboxMetrics.EventTags(record.EventType);

        if (retryAtUtc is null)
        {
            if (recorded)
            {
                // Terminal: the row is Dead. This is an operator action
                // item (replay runbook in the README), so it gets the
                // highest log level plus its own metric.
                _logger.LogCritical(
                    exception,
                    "Outbox event {EventId} ({EventType}) exhausted {MaxAttempts} publish attempts and is now Dead. Inspect the row (lastError) and replay it on purpose (see README).",
                    record.EventId,
                    record.EventType,
                    _outboxOptions.MaxAttempts);

                OutboxMetrics.DeadEvents.Add(1, tags.ToArray());
            }
            else
            {
                // The claim was lost before the terminal mark was applied;
                // the new claimant owns the retry budget now and will log
                // the Dead transition if their attempts run out.
                _logger.LogError(
                    exception,
                    "Outbox event {EventId} exhausted its publish budget, but the claim was lost before the row could be marked Dead; the new claimant owns the retry budget now.",
                    record.EventId);
            }
        }
        else
        {
            if (recorded)
            {
                OutboxMetrics.FailedAttempts.Add(1, tags.ToArray());
            }

            _logger.LogWarning(
                exception,
                "Publish attempt {Attempts}/{MaxAttempts} for outbox event {EventId} ({EventType}) failed; retrying at {RetryAtUtc}.",
                attempts,
                _outboxOptions.MaxAttempts,
                record.EventId,
                record.EventType,
                retryAtUtc.Value);
        }

        if (!recorded)
        {
            _logger.LogWarning(
                "Failure record for outbox event {EventId} not applied — claim lost to another relay; the new claimant owns the retry budget now.",
                record.EventId);
        }
    }
}
