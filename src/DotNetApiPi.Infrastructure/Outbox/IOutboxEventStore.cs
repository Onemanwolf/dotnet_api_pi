using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// Storage for outbox event rows. The write path
/// (<see cref="AppendWithinTransactionAsync"/>) is invoked by the repository
/// inside the unit-of-work transaction — the outbox row commits or aborts
/// with the aggregate write, which is the core invariant of the
/// transactional-outbox pattern ("the event is published if and only if the
/// transaction committed"). The read/claim/mark methods are used by the
/// relay outside any transaction.
/// </summary>
public interface IOutboxEventStore
{
    /// <summary>
    /// Inserts outbox rows using the supplied client session, so the insert
    /// participates in the unit-of-work transaction.
    /// </summary>
    /// <param name="records">The rows to insert (typically one per raised
    /// domain event).</param>
    /// <param name="session">The active client session whose transaction the
    /// insert must join.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task AppendWithinTransactionAsync(
        IReadOnlyList<OutboxEventRecord> records,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the next publishable row (in creation order) and
    /// returns it in its claimed state. Publishable means: <c>Pending</c>
    /// rows whose backoff gate has passed, or <c>Publishing</c> rows whose
    /// claim lease has expired (a relay that claimed and then crashed). The
    /// claim is a single find-and-update, so it is race-free even with
    /// multiple relay instances.
    /// </summary>
    /// <param name="now">The current UTC time (injected so tests can drive
    /// time deterministically).</param>
    /// <param name="leaseSeconds">Claim lease length.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The claimed row, or <c>null</c> when nothing is publishable.</returns>
    Task<OutboxEventRecord?> ClaimNextPublishableAsync(
        DateTime now,
        int leaseSeconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks a claimed row as published (Kafka ack received). The update is
    /// conditional on the row still being <c>Publishing</c>: if the claim
    /// lease expired and another instance took over, this is a no-op
    /// instead of an overwrite.
    /// </summary>
    /// <param name="eventId">The event identity.</param>
    /// <param name="partition">The Kafka partition the record landed in.</param>
    /// <param name="offset">The Kafka offset of the record.</param>
    /// <param name="publishedAtUtc">When the ack arrived (UTC).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when this call recorded the publish.</returns>
    Task<bool> MarkPublishedAsync(
        Guid eventId,
        int partition,
        long offset,
        DateTime publishedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed publish attempt on a claimed row. The update is
    /// conditional on the row still being <c>Publishing</c>. The relay
    /// decides the outcome: <paramref name="nextRetryAtUtc"/> set → back to
    /// <c>Pending</c> (retry after backoff); <c>null</c> → <c>Dead</c>
    /// (retry budget exhausted).
    /// </summary>
    /// <param name="eventId">The event identity.</param>
    /// <param name="newAttempts">The attempt counter after this failure.</param>
    /// <param name="nextRetryAtUtc">Backoff gate, or <c>null</c> for
    /// <c>Dead</c>.</param>
    /// <param name="lastError">Failure detail for operators.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> when this call recorded the failure.</returns>
    Task<bool> MarkFailedAsync(
        Guid eventId,
        int newAttempts,
        DateTime? nextRetryAtUtc,
        string? lastError,
        CancellationToken cancellationToken);
}
