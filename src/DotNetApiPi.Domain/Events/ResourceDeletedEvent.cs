namespace DotNetApiPi.Domain.Events;

/// <summary>
/// Raised when a <see cref="Entities.Resource"/> aggregate is deleted. The
/// event is staged via <c>Resource.Delete</c> and published through the
/// outbox within the same unit of work that removes the aggregate, so the
/// published event carries the resource's final state context (identity)
/// even though the document no longer exists.
/// </summary>
public sealed class ResourceDeletedEvent : IDomainEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceDeletedEvent"/> class.
    /// </summary>
    /// <param name="resourceId">The identity of the deleted resource.</param>
    /// <param name="occurredOnUtc">
    /// The UTC timestamp at which the event occurred. Callers supply this
    /// (typically from an injected <see cref="TimeProvider"/>) so that the
    /// domain never depends on <see cref="DateTime.UtcNow"/> directly and
    /// tests can control time deterministically.
    /// </param>
    public ResourceDeletedEvent(Guid resourceId, DateTime occurredOnUtc)
    {
        ResourceId = resourceId;
        OccurredOn = occurredOnUtc;
    }

    /// <summary>
    /// Gets the identity of the deleted resource.
    /// </summary>
    public Guid ResourceId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }
}
