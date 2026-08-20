namespace DotNetApiPi.Domain.Events;

/// <summary>
/// Raised when a <see cref="Entities.Resource"/> aggregate is created.
/// </summary>
public sealed class ResourceCreatedEvent : IDomainEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceCreatedEvent"/> class.
    /// </summary>
    /// <param name="resourceId">The identity of the created resource.</param>
    /// <param name="occurredOnUtc">
    /// The UTC timestamp at which the event occurred. Callers supply this
    /// (typically from an injected <see cref="TimeProvider"/>) so that the
    /// domain never depends on <see cref="DateTime.UtcNow"/> directly and
    /// tests can control time deterministically.
    /// </param>
    public ResourceCreatedEvent(Guid resourceId, DateTime occurredOnUtc)
    {
        ResourceId = resourceId;
        OccurredOn = occurredOnUtc;
    }

    /// <summary>
    /// Gets the identity of the created resource.
    /// </summary>
    public Guid ResourceId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }
}
