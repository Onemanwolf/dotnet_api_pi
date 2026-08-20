using DotNetApiPi.Domain.Events;

namespace DotNetApiPi.Application.Common;

/// <summary>
/// A subscriber for a specific domain event type. Subscribers are registered
/// in the DI container (e.g. by the infrastructure registration) and are
/// resolved and invoked by the domain event dispatcher after the unit of
/// work that raised the event has committed.
/// <para>
/// Failure contract: a throwing subscriber is logged (error) and must not
/// fail the request or stop other subscribers from running. Subscriber
/// failures also cannot roll back the write that raised the event — there is
/// no outbox yet; subscribe only to handlers that are safe to run after a
/// commit and tolerant of duplicate delivery. Introducing an outbox is the
/// correct next step for at-least-once delivery.
/// </para>
/// </summary>
/// <typeparam name="TEvent">
/// The domain event type this subscriber handles.
/// </typeparam>
public interface IDomainEventSubscriber<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>
    /// Handles the domain event.
    /// </summary>
    /// <param name="event">The domain event to handle.</param>
    /// <param name="cancellationToken">
    /// The token used to cancel the operation.
    /// </param>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
