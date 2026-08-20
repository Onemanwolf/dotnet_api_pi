namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// The lifecycle of an outbox event row. The state machine is:
/// <para>
/// <c>Pending</c> → (relay claim) → <c>Publishing</c> →
/// (Kafka ack) → <c>Published</c>, or (publish failure) → back to
/// <c>Pending</c> with a backoff gate, until <see cref="OutboxOptions.MaxAttempts"/>
/// is reached, after which the row becomes <c>Dead</c> (a visible, manually
/// recoverable dead-letter state).
/// </para>
/// </summary>
public enum OutboxEventStatus
{
    /// <summary>
    /// The event is committed and waiting to be published.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// A relay instance has claimed the event and is producing it (or the
    /// relay crashed mid-publish; a stale claim is re-claimable once its
    /// lease expires).
    /// </summary>
    Publishing = 1,

    /// <summary>
    /// The event was acknowledged by Kafka (partition and offset recorded).
    /// </summary>
    Published = 2,

    /// <summary>
    /// The event exhausted its retry budget. The row is retained for
    /// inspection; recovery is a manual status flip back to
    /// <see cref="Pending"/> (optionally with a reset attempt counter).
    /// </summary>
    Dead = 3
}
