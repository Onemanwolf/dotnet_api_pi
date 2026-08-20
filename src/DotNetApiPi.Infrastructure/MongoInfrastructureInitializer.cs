using DotNetApiPi.Infrastructure.Outbox;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure;

/// <summary>
/// MongoDB implementation of <see cref="IInfrastructureInitializer"/>.
/// MongoDB creates its database and collections lazily on first write, so
/// this initializer creates the outbox collection's indexes (which cannot
/// be created lazily) and logs a startup entry.
/// </summary>
public sealed class MongoInfrastructureInitializer : IInfrastructureInitializer
{
    private readonly IMongoCollection<Infrastructure.Persistence.Mongo.ResourceDocument> _collection;
    private readonly MongoOutboxEventStore _outboxStore;
    private readonly ILogger<MongoInfrastructureInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="MongoInfrastructureInitializer"/> class.
    /// </summary>
    /// <param name="collection">The resource document collection.</param>
    /// <param name="outboxStore">The outbox event store (its collection is
    /// indexed at startup via the store's concrete collection handle).</param>
    /// <param name="logger">The logger.</param>
    public MongoInfrastructureInitializer(
        IMongoCollection<Infrastructure.Persistence.Mongo.ResourceDocument> collection,
        MongoOutboxEventStore outboxStore,
        ILogger<MongoInfrastructureInitializer> logger)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "MongoDB storage selected (database '{Database}', collection '{Collection}'); " +
            "the collection is created lazily on first write.",
            _collection.Database.DatabaseNamespace.DatabaseName,
            _collection.CollectionNamespace.CollectionName);

        // Indexes are the one thing MongoDB does not create lazily. Both are
        // idempotent (createIndex is a no-op when the index already exists
        // with the same specification).
        //
        // status + createdAtUtc: the relay's working set — it scans Pending
        // (and lease-expired Publishing) rows in creation order.
        var statusIndexKeys = Builders<OutboxEventDocument>.IndexKeys
            .Combine(
                Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.Status),
                Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.CreatedAtUtc));

        await _outboxStore.Collection
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<OutboxEventDocument>(
                    statusIndexKeys,
                    new CreateIndexOptions { Name = "status_createdAtUtc" }),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        // resourceId: debugging/queries ("which events belong to this
        // resource?") and future consumer-side lookups.
        await _outboxStore.Collection
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<OutboxEventDocument>(
                    Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.ResourceId),
                    new CreateIndexOptions { Name = "resourceId" }),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Outbox collection '{OutboxCollection}' is ready (indexes ensured).",
            _outboxStore.Collection.CollectionNamespace.CollectionName);
    }
}
