using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure.EventHandlers;

/// <summary>
/// The first real domain-event subscriber: it logs resource creations.
/// <para>
/// It exists to prove the subscriber mechanism end-to-end (registration in
/// DI → dispatch after the unit of work commits → invocation), not as the
/// final product behaviour. Replace or extend it with real side effects
/// (notifications, audit trails, projections) as the product grows.
/// </para>
/// </summary>
public sealed class ResourceCreatedEventLogSubscriber :
    IDomainEventSubscriber<ResourceCreatedEvent>
{
    private readonly ILogger<ResourceCreatedEventLogSubscriber> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ResourceCreatedEventLogSubscriber"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ResourceCreatedEventLogSubscriber(
        ILogger<ResourceCreatedEventLogSubscriber> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task HandleAsync(
        ResourceCreatedEvent @event,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resource created: {ResourceId} (event occurred on {OccurredOn:o}).",
            @event.ResourceId,
            @event.OccurredOn);

        return Task.CompletedTask;
    }
}
