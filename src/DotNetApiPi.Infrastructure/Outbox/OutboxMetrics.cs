using System.Diagnostics.Metrics;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Outbox relay metrics.
/// <para>
/// The meter must be registered explicitly in the composition root with
/// <c>AddMeter(OutboxMetrics.MeterName)</c>. OpenTelemetry does NOT
/// discover meters by itself — an unregistered <see cref="Meter"/>
/// silently drops every measurement (the counters still exist and
/// <c>Add()</c> still succeeds, which is what makes the mistake hard to
/// notice). When registered, measurements are exported only when OTLP
/// export is enabled (<c>Otel:Enabled</c> is <c>true</c>). Tags are the
/// stable <c>event.type</c> wire name only (bounded: only registered event
/// types reach this point).
/// </para>
/// </summary>
public static class OutboxMetrics
{
    /// <summary>
    /// Stable meter name. This is the identity dashboards and exporters
    /// bind to — treat it as a public contract and do not rename it
    /// casually.
    /// </summary>
    public const string MeterName = "DotNetApiPi.Outbox";

    /// <summary>The meter that owns the outbox instruments.</summary>
    public static readonly Meter Meter = new(MeterName, "1.0.0");

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
