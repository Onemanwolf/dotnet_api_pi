using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace DotNetApiPi.Infrastructure.Tests.Integration;

/// <summary>
/// One <c>mongo:8</c> single-node replica set per test class
/// (Testcontainers). Multi-document transactions — the whole point of the
/// outbox integration tests — require a replica set, and
/// <see cref="MongoDbBuilder.WithReplicaSet(string)"/> handles the
/// initiate/join sequence and only returns the container once a writable
/// primary is available.
/// </summary>
public sealed class MongoReplicaSetFixture : IAsyncLifetime
{
    private readonly List<string> _databaseNames = [];
    private MongoDbContainer? _container;
    private IMongoClient? _client;

    /// <summary>
    /// The client connected to the replica set (direct connection to the
    /// writable primary).
    /// </summary>
    public IMongoClient Client
    {
        get
        {
            var client = _client
                ?? throw new InvalidOperationException(
                    "Fixture not initialized (use it after InitializeAsync).");

            return client;
        }
    }

    /// <summary>
    /// Creates a fresh, uniquely named database for one test and drops it
    /// when the fixture is disposed — tests never see each other's rows.
    /// </summary>
    /// <returns>The scoped database.</returns>
    public IMongoDatabase CreateDatabase()
    {
        var name = $"it_{Guid.NewGuid():N}";
        _databaseNames.Add(name);
        return Client.GetDatabase(name);
    }

    /// <inheritdoc />
    public Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:8")
            .WithReplicaSet("rs0")
            .Build();

        return StartContainerAsync();
    }

    private async Task StartContainerAsync()
    {
        if (_container is null)
        {
            throw new InvalidOperationException("InitializeAsync not called.");
        }

        await _container
            .StartAsync()
            .ConfigureAwait(false);

        _client = new MongoClient(_container.GetConnectionString());

        // Fail fast (with a clear message) when the server is not usable
        // rather than letting each test fail obscurely. (Driver 3.x: raw
        // commands go through BsonDocumentCommand + RunCommandAsync.)
        var admin = _client.GetDatabase("admin");
        var ping = await admin
            .RunCommandAsync(
                new BsonDocumentCommand<BsonDocument>(new BsonDocument("ping", 1)))
            .ConfigureAwait(false);
        _ = ping;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            foreach (var name in _databaseNames)
            {
                try
                {
                    await _client
                        .DropDatabaseAsync(name)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best effort: the container goes away anyway.
                }
            }
        }

        _client?.Dispose();

        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
