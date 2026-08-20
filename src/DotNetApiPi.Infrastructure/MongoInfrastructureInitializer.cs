using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure;

/// <summary>
/// MongoDB implementation of <see cref="IInfrastructureInitializer"/>.
/// MongoDB creates its database and collection lazily on first write, so this
/// initializer is a documented no-op apart from a startup log entry — the
/// collection handle is already resolved by the repository.
/// </summary>
public sealed class MongoInfrastructureInitializer : IInfrastructureInitializer
{
    private readonly IMongoCollection<Infrastructure.Persistence.Mongo.ResourceDocument> _collection;
    private readonly ILogger<MongoInfrastructureInitializer> _logger;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="MongoInfrastructureInitializer"/> class.
    /// </summary>
    /// <param name="collection">The resource document collection.</param>
    /// <param name="logger">The logger.</param>
    public MongoInfrastructureInitializer(
        IMongoCollection<Infrastructure.Persistence.Mongo.ResourceDocument> collection,
        ILogger<MongoInfrastructureInitializer> logger)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "MongoDB storage selected (database '{Database}', collection '{Collection}'); " +
            "the collection is created lazily on first write.",
            _collection.Database.DatabaseNamespace.DatabaseName,
            _collection.CollectionNamespace.CollectionName);

        return Task.CompletedTask;
    }
}
