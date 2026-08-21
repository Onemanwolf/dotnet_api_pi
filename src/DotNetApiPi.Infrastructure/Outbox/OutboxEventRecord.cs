namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// A provider-agnostic view of an outbox event row, used by the relay and
/// its tests. The MongoDB implementation maps to/from
/// <see cref="OutboxEventDocument"/>; other stores (e.g. an in-memory fake
/// for tests) can implement <see cref="IOutboxEventStore"/> directly.
/// </summary>
/// <param name="EventId">Stable, unique identity of the event (= the outbox
/// row id and the <c>x-event-id</c> Kafka header). Consumers can use it for
/// idempotency.</param>
/// <param name="EventType">Stable discriminator for the payload (the wire
/// name, e.g. <c>resource.created.v1</c> — see
/// <see cref="DomainEventWireTypes"/>; never a CLR type name).</param>
/// <param name="ResourceId">The identity of the aggregate the event belongs
/// to; also the Kafka message key (per-resource ordering).</param>
/// <param name="OccurredOnUtc">When the domain event occurred (stamped by
/// the aggregate, not by the relay).</param>
/// <param name="PayloadJson">The domain event serialized as camelCase JSON.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Attempts">Number of publish attempts made so far.</param>
/// <param name="CreatedAtUtc">When the row was written (inside the
/// aggregate's transaction).</param>
/// <param name="ClaimableAtUtc">The single claim gate: when the row becomes
/// claimable again — the creation time for a fresh row, the backoff deadline
/// after a failed attempt, and the lease expiry while <c>Publishing</c>.
/// Collapsing backoff gate and lease into one field lets the claim query be
/// a single index range (no <c>$or</c>, no in-memory sort).</param>
/// <param name="ClaimId">Identity of the current claim: a fresh GUID set by
/// the claiming relay. The publish/failure updates only apply while this
/// value still matches, so a late writer whose lease expired can never
/// overwrite the row a newer claimant owns.</param>
/// <param name="PublishedAtUtc">Set when the Kafka ack arrived.</param>
/// <param name="TopicPartition">Kafka partition the event landed in (set on
/// publish).</param>
/// <param name="Offset">Kafka offset of the published record (set on
/// publish).</param>
/// <param name="LastError">Failure detail from the last attempt (set on
/// failure; <c>null</c> for healthy rows).</param>
public sealed record OutboxEventRecord(
    Guid EventId,
    string EventType,
    Guid ResourceId,
    DateTime OccurredOnUtc,
    string PayloadJson,
    OutboxEventStatus Status,
    int Attempts,
    DateTime CreatedAtUtc,
    DateTime ClaimableAtUtc,
    Guid ClaimId,
    DateTime? PublishedAtUtc,
    int? TopicPartition,
    long? Offset,
    string? LastError);
