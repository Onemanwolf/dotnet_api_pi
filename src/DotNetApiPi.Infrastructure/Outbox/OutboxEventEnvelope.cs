using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// The Kafka message envelope for a published outbox event. Outbox-internal
/// bookkeeping (status, attempts, lease, last error) never leaves the
/// database; only the event itself and its stable identities cross the wire.
/// </summary>
/// <param name="EventId">Stable event identity (= outbox row id, = the
/// <c>x-event-id</c> header). Consumers use it for idempotency.</param>
/// <param name="EventType">Stable discriminator for the payload.</param>
/// <param name="ResourceId">The aggregate identity (also the message key).</param>
/// <param name="OccurredOnUtc">When the domain event occurred.</param>
/// <param name="Payload">The domain event's payload, preserved verbatim from
/// the stored row (camelCase JSON of the domain event).</param>
public sealed record OutboxEventEnvelope(
    Guid EventId,
    string EventType,
    Guid ResourceId,
    DateTime OccurredOnUtc,
    JsonElement Payload)
{
    /// <summary>
    /// Shared, immutable serialization options: camelCase, and Guids as
    /// lowercase "D" strings (the .NET 9+ default).
    /// </summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes a domain event into the camelCase JSON payload stored on
    /// the outbox row (and embedded in the envelope).
    /// </summary>
    /// <param name="domainEvent">The domain event.</param>
    /// <returns>The camelCase JSON payload.</returns>
    public static string SerializeEvent(DotNetApiPi.Domain.Events.IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Serialize with the event's concrete type so its properties are
        // round-tripped exactly as defined.
        return JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), SerializerOptions);
    }

    /// <summary>
    /// Builds an envelope from an outbox row.
    /// </summary>
    /// <param name="record">The outbox row.</param>
    /// <returns>The envelope.</returns>
    public static OutboxEventEnvelope FromRecord(OutboxEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var payload = JsonSerializer.Deserialize<JsonElement>(record.PayloadJson);

        if (payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Outbox event '{record.EventId}' has no payload.");
        }

        return new OutboxEventEnvelope(
            record.EventId,
            record.EventType,
            record.ResourceId,
            record.OccurredOnUtc,
            payload);
    }

    /// <summary>
    /// Serializes the envelope to the JSON value that will be sent as the
    /// Kafka message value.
    /// </summary>
    /// <returns>The JSON envelope.</returns>
    public string Serialize()
    {
        return JsonSerializer.Serialize(this, SerializerOptions);
    }
}
