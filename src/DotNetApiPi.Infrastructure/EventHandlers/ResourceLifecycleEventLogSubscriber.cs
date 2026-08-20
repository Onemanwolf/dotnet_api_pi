using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure.EventHandlers;

/// <summary>
/// Logs the resource lifecycle events (activated, archived and deleted).
/// <para>
/// Companion to <see cref="ResourceCreatedEventLogSubscriber"/>: it exists to
/// prove the subscriber mechanism end-to-end for every event type the
/// aggregate raises. These in-memory subscribers run <em>in addition to</em>
/// the outbox path (Mongo provider), so local runs without Kafka still show
/// each event in the API logs.
/// </para>
/// </summary>
public sealed class ResourceLifecycleEventLogSubscriber :
    IDomainEventSubscriber<ResourceActivatedEvent>,
    IDomainEventSubscriber<ResourceArchivedEvent>,
    IDomainEventSubscriber<ResourceDeletedEvent>
{
    private readonly ILogger<ResourceLifecycleEventLogSubscriber> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ResourceLifecycleEventLogSubscriber"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ResourceLifecycleEventLogSubscriber(
        ILogger<ResourceLifecycleEventLogSubscriber> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task HandleAsync(
        ResourceActivatedEvent @event,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resource activated: {ResourceId} (event occurred on {OccurredOn:o}).",
            @event.ResourceId,
            @event.OccurredOn);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(
        ResourceArchivedEvent @event,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resource archived: {ResourceId} (event occurred on {OccurredOn:o}).",
            @event.ResourceId,
            @event.OccurredOn);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task HandleAsync(
        ResourceDeletedEvent @event,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Resource deleted: {ResourceId} (event occurred on {OccurredOn:o}).",
            @event.ResourceId,
            @event.OccurredOn);

        return Task.CompletedTask;
    }
}
