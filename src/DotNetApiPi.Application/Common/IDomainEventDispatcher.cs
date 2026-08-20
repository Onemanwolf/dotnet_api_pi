using DotNetApiPi.Domain.Events;

namespace DotNetApiPi.Application.Common;

/// <summary>
/// Dispatches domain events raised by aggregates. The concrete implementation
/// lives in the infrastructure layer; the application layer only depends on
/// this abstraction (dependency inversion).
/// </summary>
public interface IDomainEventDispatcher
{
    /// <summary>
    /// Asynchronously dispatches the supplied domain events.
    /// </summary>
    /// <param name="events">The domain events to dispatch.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    Task DispatchAsync(
        IEnumerable<IDomainEvent> events,
        CancellationToken cancellationToken = default);
}
