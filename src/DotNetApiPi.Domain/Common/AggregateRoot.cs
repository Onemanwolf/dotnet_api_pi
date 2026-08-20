using DotNetApiPi.Domain.Events;

namespace DotNetApiPi.Domain.Common;

/// <summary>
/// Base class for aggregate roots.
/// An aggregate root is the entry point to an aggregate and the only object that
/// can be directly referenced and persisted. It is responsible for enforcing
/// invariants across its child entities.
/// </summary>
/// <typeparam name="TId">The type of the aggregate root's identity.</typeparam>
public abstract class AggregateRoot<TId>
    : BaseEntity<TId>,
        IHasDomainEvents,
        IClearableDomainEvents
    where TId : notnull
{
    /// <summary>
    /// Pending domain events raised by this aggregate, to be dispatched after
    /// the unit of work completes. Events are cleared once they have been dispatched.
    /// </summary>
    private readonly List<IDomainEvent> _domainEvents = [];

    /// <summary>
    /// Cached read-only view over <see cref="_domainEvents"/>. The wrapper is
    /// allocated once so that repeated access to
    /// <see cref="IHasDomainEvents.DomainEvents"/> does not allocate a new
    /// <see cref="System.Collections.ObjectModel.ReadOnlyCollection{T}"/>.
    /// </summary>
    private readonly IReadOnlyCollection<IDomainEvent> _domainEventsView;

    /// <summary>
    /// Initializes a new instance of the <see cref="AggregateRoot{TId}"/> class.
    /// </summary>
    protected AggregateRoot()
    {
        _domainEventsView = _domainEvents.AsReadOnly();
    }

    /// <summary>
    /// Gets the pending domain events for this aggregate.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEventsView;

    /// <summary>
    /// Records a domain event to be raised at the end of the unit of work.
    /// </summary>
    protected void AddDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Removes all pending domain events. Invoked by the event dispatcher
    /// after the events have been handled.
    /// <para>
    /// Implemented explicitly against the <em>internal</em>
    /// <see cref="IClearableDomainEvents"/> marker, so it is only reachable
    /// through that interface: application and API code may observe pending
    /// events (<see cref="IHasDomainEvents.DomainEvents"/>) but cannot wipe
    /// them, while the trusted infrastructure assembly (granted
    /// <c>InternalsVisibleTo</c>) can clear them after dispatch.
    /// </para>
    /// </summary>
    void IClearableDomainEvents.ClearDomainEvents() => _domainEvents.Clear();
}
