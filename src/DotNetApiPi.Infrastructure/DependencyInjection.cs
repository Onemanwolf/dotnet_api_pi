using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Infrastructure.EventHandlers;
using DotNetApiPi.Infrastructure.Kafka;
using DotNetApiPi.Infrastructure.Outbox;
using DotNetApiPi.Infrastructure.Persistence;
using DotNetApiPi.Infrastructure.Persistence.Mongo;
using DotNetApiPi.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure;

/// <summary>
/// Registers the infrastructure layer's services (persistence, repositories and
/// the domain event dispatcher) into the dependency injection container.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the infrastructure layer's services to the service collection,
    /// using the persistence provider selected by
    /// <paramref name="options"/>.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="options">The persistence options (provider and connection details).</param>
    /// <param name="configuration">
    /// An optional configuration source for the <c>Kafka</c> and
    /// <c>Outbox</c> sections (used by the MongoDB provider). <c>null</c>
    /// disables the outbox relay, which is the right default for tests and
    /// callers that do not run a Kafka broker.
    /// </param>
    /// <returns>The same service collection, to enable method chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        PersistenceOptions options,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();

        // Domain-event subscribers (audit finding F-21): the dispatcher
        // resolves IDomainEventSubscriber{TEvent> registrations from the
        // container when an event is dispatched. Singletons are appropriate
        // for stateless subscribers (like the logging one below); scoped
        // subscribers can be registered here too, since the dispatcher is
        // scoped and resolves them from the scoped provider.
        services.AddSingleton<
            IDomainEventSubscriber<ResourceCreatedEvent>,
            ResourceCreatedEventLogSubscriber>();
        services.AddSingleton<
            IDomainEventSubscriber<ResourceActivatedEvent>,
            ResourceLifecycleEventLogSubscriber>();
        services.AddSingleton<
            IDomainEventSubscriber<ResourceArchivedEvent>,
            ResourceLifecycleEventLogSubscriber>();
        services.AddSingleton<
            IDomainEventSubscriber<ResourceDeletedEvent>,
            ResourceLifecycleEventLogSubscriber>();

        switch (options.Provider)
        {
            case StorageProvider.Sqlite:
                AddSqlite(services, options.SqliteConnectionString);
                break;

            case StorageProvider.Mongo:
                AddMongo(services, options, configuration);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.Provider,
                    $"Unsupported storage provider '{options.Provider}'.");
        }

        return services;
    }

    /// <summary>
    /// Adds the infrastructure layer's services to the service collection,
    /// using SQLite with the supplied connection string (convenience overload
    /// for callers that only want the embedded database).
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <returns>The same service collection, to enable method chaining.</returns>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
        => AddInfrastructure(
            services,
            new PersistenceOptions
            {
                Provider = StorageProvider.Sqlite,
                SqliteConnectionString = connectionString
            });

    /// <summary>
    /// Registers the EF Core + SQLite persistence stack.
    /// </summary>
    private static void AddSqlite(
        IServiceCollection services,
        string? connectionString)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);

        // The context is registered through a factory so that the domain event
        // dispatcher can be injected (the options-only overload cannot inject it).
        services.AddScoped<ApiDbContext>(provider =>
            new ApiDbContext(
                new DbContextOptionsBuilder<ApiDbContext>()
                    .UseSqlite(connectionString)
                    .Options,
                provider.GetRequiredService<IDomainEventDispatcher>()));

        services.AddScoped<DbContext>(provider =>
            provider.GetRequiredService<ApiDbContext>());

        services.AddScoped<IResourceRepository, ResourceRepository>();
        services.AddScoped<IInfrastructureInitializer, SqliteInfrastructureInitializer>();
    }

    /// <summary>
    /// Registers the MongoDB persistence stack. The client, database and
    /// collection handles are thread-safe and registered as singletons; the
    /// repository (the unit of work) is scoped, like its EF Core counterpart.
    /// <para>
    /// Also registers the transactional-outbox stack: the outbox store (the
    /// <c>outbox_events</c> collection), the Confluent.Kafka publisher (one
    /// long-lived producer per process), and the relay (a hosted background
    /// service that moves committed outbox rows to Kafka). The relay is only
    /// registered when <c>Kafka:BootstrapServers</c> is configured — without
    /// a broker the outbox rows simply accumulate, and the API (including
    /// the in-memory subscribers) keeps working.
    /// </para>
    /// </summary>
    private static void AddMongo(
        IServiceCollection services,
        PersistenceOptions options,
        IConfiguration? configuration)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.MongoConnectionString);

        services.AddSingleton<IMongoClient>(_ => new MongoClient(options.MongoConnectionString));

        services.AddSingleton(provider =>
            provider.GetRequiredService<IMongoClient>()
                .GetDatabase(options.MongoDatabaseName));

        services.AddSingleton(provider =>
            provider.GetRequiredService<IMongoDatabase>()
                .GetCollection<ResourceDocument>("Resources"));

        services.AddScoped<IResourceRepository, MongoResourceRepository>();
        services.AddScoped<IInfrastructureInitializer, MongoInfrastructureInitializer>();

        // Transactional outbox: domain events are written to the
        // outbox_events collection inside the unit-of-work transaction and
        // relayed to Kafka in the background.
        services.Configure<KafkaOptions>(section =>
        {
            if (configuration is null)
            {
                return;
            }

            configuration.GetSection(KafkaOptions.SectionName).Bind(section);
        });

        services.Configure<OutboxOptions>(section =>
            configuration?.GetSection(OutboxOptions.SectionName)?.Bind(section));

        services.AddSingleton<MongoOutboxEventStore>(provider =>
            new MongoOutboxEventStore(provider.GetRequiredService<IMongoDatabase>()));
        services.AddSingleton<IOutboxEventStore>(provider =>
            provider.GetRequiredService<MongoOutboxEventStore>());

        services.AddSingleton<IKafkaEventPublisher, ConfluentKafkaEventPublisher>();

        var bootstrapServers = configuration?[KafkaOptions.SectionName + ":BootstrapServers"];

        if (!string.IsNullOrWhiteSpace(bootstrapServers))
        {
            services.AddHostedService<OutboxEventRelayService>();
        }
    }
}
