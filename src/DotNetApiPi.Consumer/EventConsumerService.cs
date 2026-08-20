using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Consumer;

/// <summary>
/// The consumer loop: reads from the <c>resource-events</c> topic in a
/// consumer group and logs every message's content and metadata (topic,
/// partition, offset, key, headers, timestamp, body) to stdout as a single
/// JSON line, so <c>docker logs</c> stays greppable.
/// <para>
/// The Confluent.Kafka consumer API is synchronous (each <c>Consume</c> call
/// blocks on librdkafka's internal thread), so the loop runs on a dedicated
/// thread-pool thread.
/// </para>
/// <para>
/// Offsets are committed manually, only after a message has been fully
/// processed (logged): at-least-once delivery. A crash between consume and
/// commit re-delivers the message on restart — harmless for a logger, and
/// the <c>x-event-id</c> header is the de-duplication key for consumers that
/// need it.
/// </para>
/// </summary>
public sealed class EventConsumerService : IHostedService
{
    private readonly ConsumerConfig _config;
    private readonly string _topic;
    private readonly ILogger<EventConsumerService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private Task? _running;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventConsumerService"/>
    /// class.
    /// </summary>
    /// <param name="config">The consumer configuration.</param>
    /// <param name="topic">The topic to consume from.</param>
    /// <param name="logger">The logger.</param>
    public EventConsumerService(
        ConsumerConfig config,
        string topic,
        ILogger<EventConsumerService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _topic = topic ?? throw new ArgumentNullException(nameof(topic));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Consumer did not drain within the shutdown grace period; uncommitted messages will be re-delivered.");
        }
        catch (OperationCanceledException)
        {
            // Host-wide cancellation.
        }

        _cts.Dispose();
    }

    /// <summary>
    /// The consume loop (runs on a dedicated thread).
    /// </summary>
    private Task RunAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            using var consumer = new ConsumerBuilder<string, string>(_config)
                .SetLogHandler((_, message) =>
                {
                    var level = message.Level switch
                    {
                        SyslogLevel.Error => LogLevel.Error,
                        SyslogLevel.Warning => LogLevel.Warning,
                        SyslogLevel.Info => LogLevel.Information,
                        _ => LogLevel.Debug
                    };

                    _logger.Log(
                        level,
                        new EventId(0),
                        "librdkafka: {Message}",
                        message.Message);
                })
                .Build();

            consumer.Subscribe(_topic);
            _logger.LogInformation(
                "Consumer started (group '{Group}', topic '{Topic}', auto.offset.reset=earliest).",
                _config.GroupId,
                _topic);

            try
            {
                while (true)
                {
                    ConsumeResult<string, string> result;

                    try
                    {
                        // Blocks until a message arrives or the token is
                        // cancelled (null on cancellation).
                        var consumed = consumer.Consume(cancellationToken);
                        if (consumed is null)
                        {
                            break;
                        }

                        result = consumed;
                    }
                    catch (Confluent.Kafka.KafkaException exception)
                    {
                        // Transient broker-side errors; librdkafka reconnects
                        // internally where it can. Log and keep polling.
                        _logger.LogWarning(
                            "Consumer poll error (continuing): {Message}",
                            exception.Message);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        Thread.Sleep(TimeSpan.FromSeconds(1));
                        continue;
                    }

                    Handle(consumer, result);
                }
            }
            finally
            {
                // Close commits the last processed offsets and releases the
                // consumer; a failure here only means a few messages are
                // re-delivered on the next start (at-least-once).
                try
                {
                    consumer.Close();
                }
                catch (Confluent.Kafka.KafkaException exception)
                {
                    _logger.LogWarning(
                        "Consumer close reported: {Message}",
                        exception.Message);
                }
            }

            _logger.LogInformation("Consumer stopped.");
        }, cancellationToken);
    }

    /// <summary>
    /// Logs one message (content + metadata) as a single JSON line and
    /// commits its offset. Key/headers/value/timestamp are read from the
    /// <c>Message</c> payload (the <c>ConsumeResult</c> pass-through
    /// properties are obsolete in Confluent.Kafka 2.15).
    /// </summary>
    private void Handle(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result)
    {
        var message = result.Message;

        string? key = message.Key;

        IReadOnlyDictionary<string, string>? headers = null;
        if (message.Headers is { Count: > 0 })
        {
            var headerMap = new Dictionary<string, string>();

            foreach (var header in message.Headers)
            {
                // IHeader exposes the key; the value is resolved through the
                // collection (newer Confluent.Kafka header model).
                if (message.Headers.TryGetLastBytes(header.Key, out var value))
                {
                    headerMap[header.Key] = Encoding.UTF8.GetString(value);
                }
            }

            headers = headerMap;
        }

        JsonElement body;
        try
        {
            body = JsonSerializer.Deserialize<JsonElement>(message.Value);
        }
        catch (JsonException)
        {
            // Non-JSON payload: log it as an opaque string instead of
            // crashing the consumer.
            body = JsonSerializer.SerializeToElement(message.Value);
        }

        var line = new
        {
            receivedAtUtc = DateTime.UtcNow,
            topic = result.Topic,
            partition = (int)result.Partition,
            offset = result.Offset.Value,
            key,
            headers,
            messageTimestampUtc = message.Timestamp.UtcDateTime,
            body
        };

        // One log line per message: content and metadata in one greppable
        // unit (the console formatter emits it as a single structured line).
        _logger.LogInformation(
            "Kafka message: {Line}",
            JsonSerializer.Serialize(line, _jsonOptions));

        // Manual commit after processing: at-least-once (a crash before the
        // commit re-delivers the message on the next start; a logger
        // tolerates that).
        try
        {
            consumer.Commit(result);
        }
        catch (Confluent.Kafka.KafkaException exception)
        {
            // The message is already logged; a commit failure only means it
            // will be re-delivered later.
            _logger.LogWarning(
                "Offset commit failed for {Topic}/{Partition}@{Offset} (will be re-delivered): {Message}",
                result.Topic,
                result.Partition,
                result.Offset.Value,
                exception.Message);
        }
    }
}
