using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure.EventHandlers;

/// <summary>
/// Publishes domain events. The generic scaffold ships without concrete event
/// handlers, but the dispatcher is wired through dependency injection so that
/// handlers can be added later (e.g. by registering additional subscribers and
/// resolving them here).
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventDispatcher"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public DomainEventDispatcher(ILogger<DomainEventDispatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private readonly ILogger<DomainEventDispatcher> _logger;

    /// <inheritdoc />
    public Task DispatchAsync(
        IEnumerable<IDomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        foreach (var @event in events)
        {
            _logger.LogInformation(
                "Domain event '{EventType}' (occurred on {OccurredOn:o}).",
                @event.GetType().Name,
                @event.OccurredOn);
        }

        return Task.CompletedTask;
    }
}
