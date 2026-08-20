namespace DotNetApiPi.Domain.Events;

/// <summary>
/// Raised when a <see cref="Entities.Resource"/> aggregate transitions from
/// <c>Draft</c> to <c>Active</c>.
/// </summary>
public sealed class ResourceActivatedEvent : IDomainEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceActivatedEvent"/> class.
    /// </summary>
    /// <param name="resourceId">The identity of the activated resource.</param>
    /// <param name="occurredOnUtc">
    /// The UTC timestamp at which the event occurred. Callers supply this
    /// (typically from an injected <see cref="TimeProvider"/>) so that the
    /// domain never depends on <see cref="DateTime.UtcNow"/> directly and
    /// tests can control time deterministically.
    /// </param>
    public ResourceActivatedEvent(Guid resourceId, DateTime occurredOnUtc)
    {
        ResourceId = resourceId;
        OccurredOn = occurredOnUtc;
    }

    /// <summary>
    /// Gets the identity of the activated resource.
    /// </summary>
    public Guid ResourceId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }
}
