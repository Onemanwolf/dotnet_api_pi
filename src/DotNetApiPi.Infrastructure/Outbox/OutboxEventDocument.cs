using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// The <c>outbox_events</c> MongoDB document: one row per domain event,
/// written in the same client-session transaction as the aggregate write
/// that raised it. Field names are stored camelCase to match the JSON
/// conventions used elsewhere in this codebase.
/// </summary>
public sealed class OutboxEventDocument
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutboxEventDocument"/>
    /// class.
    /// </summary>
    public OutboxEventDocument()
    {
    }

    /// <summary>
    /// Gets or sets the stable, unique identity of the event. It doubles as
    /// the <c>x-event-id</c> Kafka header (consumer idempotency key).
    /// </summary>
    [BsonId]
    [BsonElement("_id")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the stable discriminator for the payload (the domain
    /// event's CLR type name, e.g. <c>ResourceCreated</c>).
    /// </summary>
    [BsonElement("eventType")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the identity of the aggregate the event belongs to; also
    /// the Kafka message key.
    /// </summary>
    [BsonElement("resourceId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ResourceId { get; set; }

    /// <summary>
    /// Gets or sets when the domain event occurred (UTC, stamped by the
    /// aggregate).
    /// </summary>
    [BsonElement("occurredOnUtc")]
    public DateTime OccurredOnUtc { get; set; }

    /// <summary>
    /// Gets or sets the domain event serialized as camelCase JSON.
    /// </summary>
    [BsonElement("payload")]
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the lifecycle state. Stored as an integer for compact,
    /// index-friendly queries (see <see cref="OutboxEventStatus"/>).
    /// </summary>
    [BsonElement("status")]
    [BsonRepresentation(BsonType.Int32)]
    public OutboxEventStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the number of publish attempts made so far.
    /// </summary>
    [BsonElement("attempts")]
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets when the row was written (UTC).
    /// </summary>
    [BsonElement("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the single claim gate (UTC): the row is claimable on
    /// after this instant. Creation time on insert, lease expiry while
    /// <c>Publishing</c>, backoff time after a failed attempt. One field
    /// (instead of a nullable backoff gate plus a lease) keeps the claim
    /// query a single index range.
    /// </summary>
    [BsonElement("claimableAtUtc")]
    public DateTime ClaimableAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the identity of the current claim (a fresh GUID set by
    /// the claiming relay). Publish/failure updates only apply while this
    /// value still matches, which is what makes the lease a real ownership
    /// guarantee instead of a convention.
    /// </summary>
    [BsonElement("claimId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid ClaimId { get; set; }

    /// <summary>
    /// Gets or sets the claim lease (UTC); a <c>Publishing</c> row whose
    /// lease expired is re-claimable as a crash leftover.
    /// </summary>
    [BsonElement("leaseUntilUtc")]
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>
    /// Gets or sets when the Kafka ack arrived (UTC).
    /// </summary>
    [BsonElement("publishedAtUtc")]
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the Kafka partition the event landed in.
    /// </summary>
    [BsonElement("topicPartition")]
    public int? TopicPartition { get; set; }

    /// <summary>
    /// Gets or sets the Kafka offset of the published record.
    /// </summary>
    [BsonElement("offset")]
    public long? Offset { get; set; }

    /// <summary>
    /// Gets or sets the failure detail from the last attempt.
    /// </summary>
    [BsonElement("lastError")]
    public string? LastError { get; set; }
}
