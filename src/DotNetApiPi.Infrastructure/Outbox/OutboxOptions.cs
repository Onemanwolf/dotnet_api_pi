namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Tuning options for the outbox relay (the background service that moves
/// committed outbox rows to Kafka). Bound from the <c>Outbox</c>
/// configuration section.
/// </summary>
public sealed class OutboxOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "Outbox";

    /// <summary>
    /// How long the relay sleeps between publishable-batch polls when no
    /// events are pending (or after processing a batch).
    /// </summary>
    public int PollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// The maximum number of events claimed per poll cycle.
    /// </summary>
    public int BatchSize { get; init; } = 50;

    /// <summary>
    /// Maximum number of publishes in flight at once. A claimed batch is
    /// grouped by resource id and the groups run concurrently up to this
    /// limit — ordering inside a resource is preserved (its events stay
    /// sequential), while a slow broker round-trip no longer blocks every
    /// other resource behind it.
    /// </summary>
    public int PublishConcurrency { get; init; } = 8;

    /// <summary>
    /// The maximum number of publish attempts before a row is marked
    /// <see cref="OutboxEventStatus.Dead"/>.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Claim lease length: a <c>Publishing</c> row whose lease expired is
    /// treated as a relay crash leftover and re-claimed.
    /// </summary>
    public int LeaseSeconds { get; init; } = 30;

    /// <summary>
    /// Base retry delay; the actual backoff after attempt <c>n</c> is
    /// <c>BaseRetryDelayMs * 2^(n-1)</c> (5 s, 10 s, 20 s, …).
    /// </summary>
    public int BaseRetryDelayMs { get; init; } = 5000;
}
