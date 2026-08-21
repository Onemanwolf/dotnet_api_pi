namespace DotNetApiPi.Infrastructure.Kafka;

/// <summary>
/// Kafka connection options. Bound from the <c>Kafka</c> configuration
/// section. An empty <see cref="BootstrapServers"/> disables the outbox
/// relay at startup (events keep accumulating in the outbox collection) so
/// the API can run against MongoDB without a broker.
/// </summary>
public sealed class KafkaOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "Kafka";

    /// <summary>
    /// Comma-separated broker addresses, e.g. <c>localhost:29092</c> (the
    /// host-facing listener of the compose stack) or <c>kafka:19092</c>
    /// (from inside the compose network).
    /// </summary>
    public string BootstrapServers { get; init; } = string.Empty;

    /// <summary>
    /// The topic that domain events are published to.
    /// </summary>
    public string Topic { get; init; } = "resource-events";

    /// <summary>
    /// Maximum time (milliseconds) a message may take before the producer
    /// gives up on it (delivery deadline, including retries). This is the
    /// per-message worst-case delay that bounds how long one claimed outbox
    /// row can be held mid-publish — the relay's claim lease must outlive
    /// the worst-case batch drain (see <see cref="DotNetApiPi.Infrastructure.Outbox.OutboxOptions.LeaseSeconds"/>
    /// and the relay's startup guard).
    /// </summary>
    public int MessageTimeoutMs { get; init; } = 30_000;
}
