using DotNetApiPi.Domain.Events;

namespace DotNetApiPi.Domain.Common;

/// <summary>
/// Non-generic marker for aggregates that raise <see cref="IDomainEvent"/>s.
/// <para>
/// Infrastructure code (unit-of-work implementations) uses this interface to
/// discover pending domain events without being hard-wired to a specific
/// <see cref="AggregateRoot{TId}"/> key type. Previously the EF Core path
/// only matched <c>AggregateRoot{Guid}</c>, which would silently skip a
/// future aggregate with a different identity type.
/// </para>
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>
    /// Gets the domain events raised by this aggregate since the last
    /// dispatch.
    /// </summary>
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
}
