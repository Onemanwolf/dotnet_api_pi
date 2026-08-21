using System.Diagnostics.Metrics;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Outbox relay metrics. Counters are created on first touch (OTel picks
/// up every <see cref="Meter"/> that is created in the process; the API's
/// <c>WithMetrics</c> block exports them — to OTLP when <c>Otel:Enabled</c>
/// is set, to the <c>/metrics</c> Prometheus endpoint otherwise). Tagged by
/// the stable <c>event.type</c> wire name (bounded: only registered event
/// types reach this point).
/// </summary>
public static class OutboxMetrics
{
    /// <summary>The meter that owns the outbox instruments.</summary>
    public static readonly Meter Meter = new("DotNetApiPi.Outbox");

    /// <summary>
    /// Events published to Kafka and recorded as <c>Published</c> on the
    /// outbox row (per <c>event.type</c>).
    /// </summary>
    public static readonly Counter<long> PublishedEvents =
        Meter.CreateCounter<long>(
            "dotnet_api_pi.outbox.published",
            description: "Outbox events published to Kafka and recorded as Published.");

    /// <summary>
    /// Publish attempts that failed and went back to <c>Pending</c>
    /// (per <c>event.type</c>).
    /// </summary>
    public static readonly Counter<long> FailedAttempts =
        Meter.CreateCounter<long>(
            "dotnet_api_pi.outbox.failed_attempts",
            description: "Failed publish attempts that went back to Pending with backoff.");

    /// <summary>
    /// Rows that exhausted their retry budget and are now <c>Dead</c>
    /// (operator action required — replay runbook in the README; per
    /// <c>event.type</c>).
    /// </summary>
    public static readonly Counter<long> DeadEvents =
        Meter.CreateCounter<long>(
            "dotnet_api_pi.outbox.dead",
            description: "Outbox rows that exhausted their retry budget and are Dead.");

    /// <summary>
    /// Builds the standard <c>event.type</c> tag set for the given wire
    /// name.
    /// </summary>
    /// <param name="eventType">The stable wire event type name
    /// (see <see cref="DomainEventWireTypes"/>).</param>
    /// <returns>A single <c>event.type</c> tag.</returns>
    public static IEnumerable<KeyValuePair<string, object?>> EventTags(string eventType)
        => [new KeyValuePair<string, object?>("event.type", eventType)];
}
