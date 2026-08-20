using System.Reflection;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure.EventHandlers;

/// <summary>
/// Publishes domain events to <see cref="IDomainEventSubscriber{TEvent}"/>
/// subscribers resolved from the dependency-injection container.
/// <para>
/// Subscribers are resolved per event type (the closed
/// <c>IDomainEventSubscriber{TEvent}</c> interface for each concrete event
/// type), so the dispatcher is extensible without modification: registering
/// a new subscriber in DI is enough to receive that event type.
/// </para>
/// <para>
/// Failure contract: an event type with no registered subscriber gets a
/// debug log (normal — most events have no consumers yet). A subscriber that
/// throws is logged (error) and the dispatch continues: subscriber failures
/// must not fail the request that committed the write, and they cannot roll
/// that write back (there is no outbox yet — introducing one is the correct
/// next step for at-least-once delivery).
/// </para>
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly ILogger<DomainEventDispatcher> _logger;

    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventDispatcher"/>
    /// class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="serviceProvider">
    /// The service provider used to resolve event subscribers.
    /// </param>
    public DomainEventDispatcher(
        ILogger<DomainEventDispatcher> logger,
        IServiceProvider serviceProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serviceProvider =
            serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        IEnumerable<IDomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            _logger.LogInformation(
                "Domain event '{EventType}' (occurred on {OccurredOn:o}).",
                @event.GetType().Name,
                @event.OccurredOn);

            await DispatchToSubscribersAsync(@event, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the subscribers for one event type and invokes each of them,
    /// containing any exception a subscriber throws.
    /// </summary>
    private async Task DispatchToSubscribersAsync(
        IDomainEvent @event,
        CancellationToken cancellationToken)
    {
        var subscriberType = typeof(IDomainEventSubscriber<>)
            .MakeGenericType(@event.GetType());

        // The subscriber contract has exactly one method; the generic
        // interface for this event type is a closed interface, so
        // GetServices(Type) can be used directly.
        var handleAsync = subscriberType
            .GetMethod(nameof(IDomainEventSubscriber<IDomainEvent>.HandleAsync))!;

        List<object> subscribers;

        try
        {
            subscribers = _serviceProvider
                .GetServices(subscriberType)
                .Cast<object>()
                .ToList();
        }
        catch (Exception exception)
        {
            // Resolution failures (e.g. a subscriber whose constructor
            // requires a scoped service — subscribers must be resolvable from
            // the root provider) must not abort the dispatch of the remaining
            // events or fail the save that already committed.
            _logger.LogError(
                exception,
                "Could not resolve subscribers for domain event '{EventType}'; none were invoked.",
                @event.GetType().Name);

            return;
        }

        if (subscribers.Count == 0)
        {
            _logger.LogDebug(
                "No subscriber registered for domain event '{EventType}'; skipping.",
                @event.GetType().Name);

            return;
        }

        foreach (var subscriber in subscribers)
        {
            try
            {
                await (Task)handleAsync
                    .Invoke(subscriber, [@event, cancellationToken])!;
            }
            catch (Exception exception)
            {
                // MethodBase.Invoke wraps the target exception in a
                // TargetInvocationException; log the original one.
                var originalException = exception is
                        TargetInvocationException { InnerException: { } inner }
                    ? inner
                    : exception;

                _logger.LogError(
                    originalException,
                    "Subscriber {SubscriberType} threw while handling domain event '{EventType}'; continuing with the remaining subscribers.",
                    subscriber.GetType().FullName,
                    @event.GetType().Name);
            }
        }
    }
}
