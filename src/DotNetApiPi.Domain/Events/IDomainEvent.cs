namespace DotNetApiPi.Domain.Events;

/// <summary>
/// Marker interface for a domain event.
/// Domain events describe something that has happened within the domain
/// and are raised by aggregate roots. They are dispatched after a unit of
/// work has been committed, ensuring eventual consistency.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// Gets the timestamp at which the event occurred.
    /// </summary>
    DateTime OccurredOn { get; }
}
