using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.EventHandlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Infrastructure.Tests;

/// <summary>
/// Verifies the domain-event subscriber mechanism (audit finding F-21):
/// subscribers are resolved from DI per event type, an unhandled event type
/// logs (debug) instead of warning/throwing, and a throwing subscriber is
/// contained (it must not fail the dispatch or stop other subscribers).
/// </summary>
public sealed class DomainEventSubscriberTests
{
    [Fact]
    public async Task RegisteredSubscriber_IsInvokedExactlyOncePerEvent()
    {
        var subscriber = new CountingResourceCreatedSubscriber();
        using var provider = BuildServiceProvider(subscriber);
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider
            .GetRequiredService<IDomainEventDispatcher>();

        var resource = Resource.Create(new ResourceName("Subscriber test"));
        Assert.Single(resource.DomainEvents);

        await dispatcher.DispatchAsync(resource.DomainEvents);

        Assert.Equal(1, subscriber.InvocationCount);
    }

    [Fact]
    public async Task UnhandledEventType_LogsDebug_AndDoesNotThrow()
    {
        var logger = new ListLogger();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<DomainEventDispatcher>>(logger);
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider
            .GetRequiredService<IDomainEventDispatcher>();

        var act = async () =>
            await dispatcher
                .DispatchAsync([new UnhandledTestEvent()]);

        await act();

        Assert.Contains(
            logger.Entries,
            entry =>
                entry.StartsWith("[Debug]") &&
                entry.Contains("UnhandledTestEvent"));
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotStopSubsequentSubscribers()
    {
        var logger = new ListLogger();
        var survivingSubscriber = new CountingResourceCreatedSubscriber();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<DomainEventDispatcher>>(logger);
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Registration order defines resolution order: the throwing
        // subscriber runs first.
        services.AddSingleton<
            IDomainEventSubscriber<ResourceCreatedEvent>,
            ThrowingResourceCreatedSubscriber>();
        services.AddSingleton<IDomainEventSubscriber<ResourceCreatedEvent>>(
            survivingSubscriber);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider
            .GetRequiredService<IDomainEventDispatcher>();

        var resource = Resource.Create(new ResourceName("Throwing subscriber test"));

        await dispatcher.DispatchAsync(resource.DomainEvents);

        // The throwing subscriber was contained…
        Assert.Contains(
            logger.Entries,
            entry => entry.StartsWith("[Error]") &&
                entry.Contains(nameof(ThrowingResourceCreatedSubscriber)));

        // …and the later subscriber still ran.
        Assert.Equal(1, survivingSubscriber.InvocationCount);
    }

    /// <summary>
    /// Builds a service provider hosting the real dispatcher and the given
    /// subscriber registration.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        IDomainEventSubscriber<ResourceCreatedEvent> subscriber)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<DomainEventDispatcher>>(new ListLogger());
        services.AddSingleton<IDomainEventSubscriber<ResourceCreatedEvent>>(
            subscriber);
        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A domain event type with no registered subscriber.
    /// </summary>
    private sealed class UnhandledTestEvent : IDomainEvent
    {
        /// <inheritdoc />
        public DateTime OccurredOn { get; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Counts how often it is invoked.
    /// </summary>
    private sealed class CountingResourceCreatedSubscriber :
        IDomainEventSubscriber<ResourceCreatedEvent>
    {
        private int _invocationCount;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        /// <inheritdoc />
        public Task HandleAsync(
            ResourceCreatedEvent @event,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocationCount);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Always throws, to verify that one failing subscriber is contained.
    /// </summary>
    private sealed class ThrowingResourceCreatedSubscriber :
        IDomainEventSubscriber<ResourceCreatedEvent>
    {
        /// <inheritdoc />
        public Task HandleAsync(
            ResourceCreatedEvent @event,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated subscriber failure");
    }

    /// <summary>
    /// A minimal <see cref="ILogger"/> that captures formatted entries for
    /// assertion.
    /// </summary>
    private sealed class ListLogger : ILogger, ILogger<DomainEventDispatcher>
    {
        private readonly List<string> _entries = [];

        public List<string> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add($"[{logLevel}] {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
