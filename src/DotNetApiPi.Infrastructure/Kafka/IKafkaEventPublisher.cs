namespace DotNetApiPi.Infrastructure.Kafka;

/// <summary>
/// The result of a successful produce: where the record landed.
/// </summary>
/// <param name="Topic">The topic the record was written to.</param>
/// <param name="Partition">The partition the record landed in.</param>
/// <param name="Offset">The offset of the record within its partition.</param>
public sealed record KafkaPublishResult(string Topic, int Partition, long Offset);

/// <summary>
/// Publishes event messages to Kafka. Implemented with a single long-lived,
/// thread-safe producer (best practice: one producer per process); the
/// implementation awaits the delivery report so the caller only proceeds on
/// broker acknowledgement.
/// </summary>
public interface IKafkaEventPublisher
{
    /// <summary>
    /// Publishes a message and waits for the broker's acknowledgement.
    /// </summary>
    /// <param name="key">The message key (resource id). The key determines
    /// the partition, which preserves per-resource ordering.</param>
    /// <param name="value">The JSON envelope.</param>
    /// <param name="headers">Optional message headers (e.g.
    /// <c>x-event-id</c> for consumer-side idempotency).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The partition and offset the broker assigned.</returns>
    /// <exception cref="Confluent.Kafka.ProduceException`1">
    /// Thrown when the broker rejects the record or the delivery timeout
    /// elapses; the caller (the relay) treats that as a failed attempt.
    /// </exception>
    Task<KafkaPublishResult> PublishAsync(
        string key,
        string value,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken);
}
