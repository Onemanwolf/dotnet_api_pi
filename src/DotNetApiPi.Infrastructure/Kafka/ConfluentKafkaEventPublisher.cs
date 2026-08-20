using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetApiPi.Infrastructure.Kafka;

/// <summary>
/// Confluent.Kafka implementation of <see cref="IKafkaEventPublisher"/>.
/// Owns one long-lived <see cref="IProducer{TKey,TValue}"/> (thread-safe;
/// the recommended pattern is a single producer per process) and awaits the
/// delivery report on every publish.
/// <para>
/// Producer settings follow Kafka best practice for reliable delivery:
/// <see langword="true"/> idempotence (which implicitly forces
/// <c>acks=all</c>) so the broker deduplicates this producer's records even
/// under retries, a short linger window for modest batching, and snappy
/// compression. Ordering is preserved by partitioning on the message key
/// (resource id).
/// </para>
/// </summary>
public sealed class ConfluentKafkaEventPublisher : IKafkaEventPublisher, IAsyncDisposable
{
    private readonly IProducer<string, string> _producer;
    private readonly string _topic;
    private readonly ILogger<ConfluentKafkaEventPublisher> _logger;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ConfluentKafkaEventPublisher"/> class and starts the
    /// producer.
    /// </summary>
    /// <param name="options">The Kafka options.</param>
    /// <param name="logger">The logger.</param>
    public ConfluentKafkaEventPublisher(
        IOptions<KafkaOptions> options,
        ILogger<ConfluentKafkaEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var kafka = options.Value;
        ArgumentException.ThrowIfNullOrWhiteSpace(kafka.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(kafka.Topic);

        _topic = kafka.Topic;
        _logger = logger;

        var config = new ProducerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            // Idempotent producer: retries never duplicate a record on the
            // broker (per-producer deduplication). Setting this also
            // requires acks=all, which is applied explicitly for clarity.
            EnableIdempotence = true,
            Acks = Acks.All,
            // Deliver the record on the next batch window; 10 ms keeps the
            // relay's round-trip latency low while still allowing small
            // batches.
            LingerMs = 10,
            CompressionType = CompressionType.Snappy,
            // A single event envelope is small, but the broker's default
            // max request size is much larger than we need; cap it so a
            // pathological payload fails fast on the client instead of
            // being silently split/rejected late.
            MessageMaxBytes = 1_000_000,
            // Hard deadline for a produce (including internal retries): on
            // expiry, ProduceAsync faults and the relay records a failed
            // attempt. (librdkafka's message.timeout.ms — Confluent.Kafka
            // 2.15 no longer exposes the old delivery-timeout setting.)
            MessageTimeoutMs = 30_000
        };

        _producer = new ProducerBuilder<string, string>(config)
            .SetLogHandler((_, message) =>
            {
                var level = message.Level switch
                {
                    SyslogLevel.Error => LogLevel.Error,
                    SyslogLevel.Warning => LogLevel.Warning,
                    SyslogLevel.Info => LogLevel.Information,
                    _ => LogLevel.Debug
                };

                logger.Log(
                    level,
                    new EventId(0),
                    "librdkafka: {Message}",
                    message.Message);
            })
            .Build();

        _logger.LogInformation(
            "Kafka producer started (bootstrap '{Bootstrap}', topic '{Topic}').",
            kafka.BootstrapServers,
            _topic);
    }

    /// <inheritdoc />
    public async Task<KafkaPublishResult> PublishAsync(
        string key,
        string value,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(value);

        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(ConfluentKafkaEventPublisher));
        }

        var message = new Message<string, string>
        {
            Key = key,
            Value = value
        };

        if (headers is { Count: > 0 })
        {
            // Confluent.Kafka 2.15 header model: a Headers collection of
            // (string key, byte[] value) pairs.
            var messageHeaders = new Headers();

            foreach (var (name, headerValue) in headers)
            {
                messageHeaders.Add(name, System.Text.Encoding.UTF8.GetBytes(headerValue));
            }

            message.Headers = messageHeaders;
        }

        // ProduceAsync completes when the broker acknowledges the record
        // (acks=all) — or faults (ProduceException) when the delivery
        // timeout elapses or the broker rejects the record. (In
        // Confluent.Kafka 2.15 the report's Topic is a plain string.)
        var report = await _producer
            .ProduceAsync(_topic, message, cancellationToken)
            .ConfigureAwait(false);

        return new KafkaPublishResult(
            report.Topic,
            report.Partition.Value,
            report.Offset.Value);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Dispose flushes pending batches; ignore the KafkaException that
        // surfaces when flushing fails during shutdown (the outbox is the
        // source of truth — unacknowledged publishes stay Pending/Publishing
        // and are re-delivered after restart).
        try
        {
            await Task.Run(_producer.Dispose).ConfigureAwait(false);
        }
        catch (Confluent.Kafka.KafkaException exception)
        {
            _logger.LogWarning(
                "Kafka producer dispose reported: {Message}",
                exception.Message);
        }

        _logger.LogInformation("Kafka producer disposed.");
    }
}
