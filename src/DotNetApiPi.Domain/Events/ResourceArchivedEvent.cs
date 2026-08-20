namespace DotNetApiPi.Domain.Events;

/// <summary>
/// Raised when a <see cref="Entities.Resource"/> aggregate transitions from
/// <c>Active</c> to <c>Archived</c> (terminal state).
/// </summary>
public sealed class ResourceArchivedEvent : IDomainEvent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceArchivedEvent"/> class.
    /// </summary>
    /// <param name="resourceId">The identity of the archived resource.</param>
    /// <param name="occurredOnUtc">
    /// The UTC timestamp at which the event occurred. Callers supply this
    /// (typically from an injected <see cref="TimeProvider"/>) so that the
    /// domain never depends on <see cref="DateTime.UtcNow"/> directly and
    /// tests can control time deterministically.
    /// </param>
    public ResourceArchivedEvent(Guid resourceId, DateTime occurredOnUtc)
    {
        ResourceId = resourceId;
        OccurredOn = occurredOnUtc;
    }

    /// <summary>
    /// Gets the identity of the archived resource.
    /// </summary>
    public Guid ResourceId { get; }

    /// <inheritdoc />
    public DateTime OccurredOn { get; }
}
