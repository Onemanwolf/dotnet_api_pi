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

        // Indexes are the one thing MongoDB does not create lazily. All are
        // idempotent (createIndex is a no-op when the index already exists
        // with the same specification).
        //
        // status + claimableAtUtc: the relay's working set. The claim query
        // is "status IN (Pending, Publishing) AND claimableAtUtc <= now" in
        // claimableAtUtc order — this compound index serves both the filter
        // and the sort, so a broker-outage backlog never forces a blocking
        // in-memory sort (which has a 100 MB ceiling).
        var claimIndexKeys = Builders<OutboxEventDocument>.IndexKeys
            .Combine(
                Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.Status),
                Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.ClaimableAtUtc));

        await _outboxStore.Collection
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<OutboxEventDocument>(
                    claimIndexKeys,
                    new CreateIndexOptions { Name = "status_claimableAtUtc" }),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        // TTL: published rows are terminal and only matter for replay/audit
        // — let them age out (7 days) so the collection cannot outgrow the
        // working set. The partial filter keeps Pending, Publishing and
        // Dead rows indefinitely. (Created when the first Published row
        // exists; the TTL monitor deletes only documents in the partial
        // index.)
        await _outboxStore.Collection
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<OutboxEventDocument>(
                    Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.PublishedAtUtc),
                    new CreateIndexOptions<OutboxEventDocument>
                    {
                        Name = "publishedAtUtc_ttl",
                        ExpireAfter = TimeSpan.FromDays(7),
                        // Strongly-typed partial filter (driver 3.x): only
                        // terminal Published rows enter the TTL index.
                        PartialFilterExpression = Builders<OutboxEventDocument>.Filter.Eq(
                            d => d.Status,
                            OutboxEventStatus.Published)
                    }),
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
