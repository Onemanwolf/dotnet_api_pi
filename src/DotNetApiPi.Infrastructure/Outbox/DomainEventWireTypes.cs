namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// The stable <c>eventType</c> wire names for domain events published on
/// the Kafka topic. The wire contract must not be a C# class name: an IDE
/// rename of <c>ResourceArchivedEvent</c> would otherwise silently
/// republish a different contract to every consumer, with no compile error
/// anywhere. New domain events must get an explicit entry here —
/// <see cref="GetWireName"/> throws for anything unregistered, so a missing
/// mapping fails fast at the publish site instead of corrupting the
/// stream.
/// </summary>
public static class DomainEventWireTypes
{
    /// <summary>
    /// Current envelope schema version (bump when the envelope shape
    /// changes in a way consumers must handle).
    /// </summary>
    public const int EnvelopeSchemaVersion = 1;

    private static readonly Dictionary<Type, string> WireNames = new()
    {
        [typeof(DotNetApiPi.Domain.Events.ResourceCreatedEvent)] = "resource.created.v1",
        [typeof(DotNetApiPi.Domain.Events.ResourceActivatedEvent)] = "resource.activated.v1",
        [typeof(DotNetApiPi.Domain.Events.ResourceArchivedEvent)] = "resource.archived.v1",
        [typeof(DotNetApiPi.Domain.Events.ResourceDeletedEvent)] = "resource.deleted.v1"
    };

    /// <summary>
    /// Maps a domain event type to its stable wire name.
    /// </summary>
    /// <param name="eventType">The domain event's CLR type.</param>
    /// <returns>The stable wire name (e.g. <c>resource.created.v1</c>).</returns>
    /// <exception cref="InvalidOperationException">
    /// The type has no registered wire name — register it before publishing.
    /// </exception>
    public static string GetWireName(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        if (WireNames.TryGetValue(eventType, out var wireName))
        {
            return wireName;
        }

        throw new InvalidOperationException(
            $"No stable wire name is registered for domain event type '{eventType.FullName}'. " +
            $"Add it to {nameof(DomainEventWireTypes)} before publishing new events.");
    }
}
