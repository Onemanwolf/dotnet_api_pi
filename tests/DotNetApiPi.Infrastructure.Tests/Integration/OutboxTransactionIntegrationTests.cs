using System.Text.Json;
using DotNetApiPi.Application.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Infrastructure.Outbox;
using DotNetApiPi.Infrastructure.Persistence.Mongo;
using DotNetApiPi.Infrastructure.Repositories;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;
using Xunit.Abstractions;

namespace DotNetApiPi.Infrastructure.Tests.Integration;

/// <summary>
/// Integration tests for the transactional-outbox invariant against a real
/// MongoDB replica set (Testcontainers): an aborted unit of work must leave
/// no outbox row and no aggregate write; a committed one must land both
/// atomically; and the relay's claim query must be served by the
/// <c>status_claimableAtUtc</c> index (no blocking SORT) even with a large
/// backlog. Tagged <c>Category=Integration</c>: skipped with
/// <c>--filter "Category!=Integration"</c> when Docker is unavailable.
/// </summary>
public sealed class OutboxTransactionIntegrationTests : IClassFixture<MongoReplicaSetFixture>
{
    private readonly MongoReplicaSetFixture _fixture;
    private readonly ITestOutputHelper _output;

    public OutboxTransactionIntegrationTests(
        MongoReplicaSetFixture fixture,
        ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task AbortedUnitOfWork_LeavesNoOutboxRow_NorAggregateWrite()
    {
        var database = _fixture.CreateDatabase();
        var resources = database.GetCollection<ResourceDocument>("Resources");
        var store = new MongoOutboxEventStore(database);
        var client = _fixture.Client;

        // Seed a document with the identity of the aggregate we are about
        // to stage: the repository's unit-of-work transaction will fail on
        // the duplicate-key insert and abort.
        var resource = Resource.Create(new ResourceName("aborted-probe"));
        await resources
            .InsertOneAsync(ResourceDocumentMapper.ToDocument(resource));

        var repository = new MongoResourceRepository(
            client,
            resources,
            store,
            new NoOpEventDispatcher());

        await repository.AddAsync(resource);

        await Assert
            .ThrowsAsync<MongoWriteException>(() => repository.SaveChangesAsync());

        // The outbox collection exists only after an insert is attempted;
        // after an abort it must be empty...
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);
        var outboxCount = await outbox
            .CountDocumentsAsync(FilterDefinition<OutboxEventDocument>.Empty);
        Assert.Equal(0L, outboxCount);

        // ...and the aggregate write must not have happened either: the
        // collection holds exactly the pre-seeded document.
        var resourcesCount = await resources
            .CountDocumentsAsync(Builders<ResourceDocument>.Filter.Eq(d => d.Id, resource.Id));
        Assert.Equal(1L, resourcesCount);
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task OutboxAppend_ParticipatesInTheCallerTransaction()
    {
        // Load-bearing check for the store itself: two rows share one
        // identity, so the second insert inside the transaction fails with
        // a duplicate-key error. If
        // AppendWithinTransactionAsync did not join the caller's session
        // (i.e. wrote outside the transaction), the first row would
        // auto-commit and survive the abort — and this test would fail.
        var database = _fixture.CreateDatabase();
        var store = new MongoOutboxEventStore(database);

        var now = DateTime.UtcNow;
        var record = PendingRecord("resource.created.v1", now);
        var session = _fixture.Client.StartSession();

        await Assert.ThrowsAnyAsync<MongoBulkWriteException>(async () =>
        {
            using (session)
            {
                await session
                    .WithTransactionAsync(
                        async (transaction, cancellationToken) =>
                        {
                            await store
                                .AppendWithinTransactionAsync(
                                    [record, record],
                                    transaction,
                                    cancellationToken);
                            return 0;
                        },
                        new TransactionOptions(),
                        CancellationToken.None);
            }
        });

        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);
        var outboxCount = await outbox
            .CountDocumentsAsync(FilterDefinition<OutboxEventDocument>.Empty);

        Assert.Equal(0L, outboxCount);
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task AbortAfterOutboxAppend_LeavesNoOutboxRow()
    {
        // Ordering-gap guard (round 4, W-8). Two aggregates are staged:
        // A (clean) and B (a seeded duplicate key makes its insert fail).
        // Whatever the statement order inside the unit-of-work transaction
        // is, the abort must roll back everything — and in particular, if
        // the outbox append had already run (today it runs after the
        // aggregate writes; a future reordering that moves it earlier —
        // combined with a non-transactional append — would commit A's rows
        // while B's write aborts, leaving an event for a state change that
        // never committed), no outbox row may survive.
        var database = _fixture.CreateDatabase();
        var resources = database.GetCollection<ResourceDocument>("Resources");
        var store = new MongoOutboxEventStore(database);
        var client = _fixture.Client;

        var resourceA = Resource.Create(new ResourceName("abort-after-append-a"));
        var resourceB = Resource.Create(new ResourceName("abort-after-append-b"));

        // B's identity is already taken: its insert fails inside the
        // transaction, after A's writes/outbox rows are (or would be) in.
        await resources
            .InsertOneAsync(ResourceDocumentMapper.ToDocument(resourceB));

        var repository = new MongoResourceRepository(
            client,
            resources,
            store,
            new NoOpEventDispatcher());

        await repository.AddAsync(resourceA);
        await repository.AddAsync(resourceB);

        await Assert
            .ThrowsAsync<MongoWriteException>(() => repository.SaveChangesAsync());

        // A's ResourceCreated event must not have leaked... (the
        // transactional-outbox invariant: an event is handed to the
        // publisher iff the state change committed).
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);
        var outboxCount = await outbox
            .CountDocumentsAsync(FilterDefinition<OutboxEventDocument>.Empty);
        Assert.Equal(0L, outboxCount);

        // ...and neither aggregate write survived: only B's pre-seeded
        // document is in the collection.
        var countA = await resources
            .CountDocumentsAsync(Builders<ResourceDocument>.Filter.Eq(d => d.Id, resourceA.Id));
        Assert.Equal(0L, countA);
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task CommittedUnitOfWork_WritesAggregateAndOutboxAtomically()
    {
        var database = _fixture.CreateDatabase();
        var resources = database.GetCollection<ResourceDocument>("Resources");
        var store = new MongoOutboxEventStore(database);
        var client = _fixture.Client;

        var resource = Resource.Create(new ResourceName("committed-probe"));

        var repository = new MongoResourceRepository(
            client,
            resources,
            store,
            new NoOpEventDispatcher());

        await repository.AddAsync(resource);
        var affected = await repository.SaveChangesAsync();
        Assert.Equal(1, affected);

        // Exactly one aggregate document...
        var resourcesCount = await resources
            .CountDocumentsAsync(Builders<ResourceDocument>.Filter.Eq(d => d.Id, resource.Id));
        Assert.Equal(1L, resourcesCount);

        // ...and exactly one outbox row, Pending, with the stable wire
        // name and the serialized event payload.
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);
        var rows = await outbox
            .Find(FilterDefinition<OutboxEventDocument>.Empty)
            .ToListAsync();

        Assert.Single(rows);
        var row = rows[0];
        Assert.Equal(OutboxEventStatus.Pending, row.Status);
        Assert.Equal("resource.created.v1", row.EventType);
        Assert.Equal(resource.Id, row.ResourceId);
        Assert.Equal(0, row.Attempts);
        Assert.NotEqual(Guid.Empty, row.Id);

        // The payload is the concrete domain event as camelCase JSON.
        using var payload = JsonDocument.Parse(row.PayloadJson);
        var root = payload.RootElement;
        Assert.Equal(
            resource.Id.ToString("D"),
            root.GetProperty("resourceId").GetString());
        Assert.True(root.TryGetProperty("occurredOn", out _));
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task ClaimQuery_UsesIndex_WithoutBlockingSort_WithLargeBacklog()
    {
        // W-1 acceptance: with 200 backlog rows, the claim query (the exact
        // filter + sort of MongoOutboxEventStore.ClaimNextPublishableAsync)
        // must be served by the status_claimableAtUtc index — no SORT
        // stage, and a single document examined for the first match.
        var database = _fixture.CreateDatabase();
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);

        // The index the initializer creates for the claim.
        await outbox
            .Indexes
            .CreateOneAsync(
                new CreateIndexModel<OutboxEventDocument>(
                    Builders<OutboxEventDocument>.IndexKeys.Combine(
                        Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.Status),
                        Builders<OutboxEventDocument>.IndexKeys.Ascending(d => d.ClaimableAtUtc)),
                    new CreateIndexOptions { Name = "status_claimableAtUtc" }));

        // A large backlog: all claimable now, spread over distinct gates.
        var now = DateTime.UtcNow;
        var documents = Enumerable.Range(0, 200)
            .Select(i => new OutboxEventDocument
            {
                Id = Guid.NewGuid(),
                EventType = "resource.created.v1",
                ResourceId = Guid.NewGuid(),
                OccurredOnUtc = now,
                PayloadJson = "{}",
                Status = OutboxEventStatus.Pending,
                Attempts = 0,
                CreatedAtUtc = now,
                ClaimableAtUtc = now.AddSeconds(-i),
                ClaimId = Guid.Empty
            })
            .ToList();
        await outbox.InsertManyAsync(documents);

        // The exact claim query (driver 3.x dropped the typed Explain API,
        // so the raw explain command is run directly; find with the same
        // filter/sort/limit is index-plan-equivalent to the
        // findAndModify the store issues).
        var explain = await database
            .RunCommandAsync(
                new BsonDocumentCommand<BsonDocument>(
                    new BsonDocument
                    {
                        ["explain"] = new BsonDocument
                        {
                            ["find"] = "outbox_events",
                            ["filter"] = new BsonDocument
                            {
                                ["status"] = new BsonDocument
                                {
                                    ["$in"] = new BsonArray
                                    {
                                        OutboxEventStatus.Pending,
                                        OutboxEventStatus.Publishing
                                    }
                                },
                                ["claimableAtUtc"] = new BsonDocument
                                {
                                    ["$lte"] = now
                                }
                            },
                            ["sort"] = new BsonDocument { ["claimableAtUtc"] = 1 },
                            ["limit"] = 1
                        },
                        ["verbosity"] = "executionStats"
                    }));

        var plan = explain["queryPlanner"]["winningPlan"];
        var planJson = plan.ToJson();
        _output.WriteLine(planJson);

        // No blocking in-memory SORT over the backlog (MongoDB 8 serves
        // the $in over the leading key via a SORT_MERGE of per-status
        // index scans whose sort pattern matches the second index key).
        Assert.DoesNotContain("\"stage\":\"SORT\"", planJson);
        // ...and no collection scan either: the claim index serves it.
        Assert.Contains("status_claimableAtUtc", planJson);
        Assert.DoesNotContain("COLLSCAN", planJson);
        // ...and the first match is a single documented fetch.
        var stats = explain["executionStats"];
        Assert.Equal(1, stats["totalDocsExamined"].AsInt32);
    }

    /// <summary>
    /// A fresh Pending outbox record (the same shape the repository writes
    /// inside a unit of work).
    /// </summary>
    private static OutboxEventRecord PendingRecord(string eventType, DateTime now)
        => new(
            Guid.NewGuid(),
            eventType,
            Guid.NewGuid(),
            now,
            "{\"probe\":true}",
            OutboxEventStatus.Pending,
            0,
            now,
            now, // claimable immediately
            Guid.Empty, // assigned on first claim
            null,
            null,
            null,
            null);

    /// <summary>
    /// A dispatcher that drops every event (the tests assert on the
    /// persistence, not on in-process dispatch).
    /// </summary>
    private sealed class NoOpEventDispatcher : IDomainEventDispatcher
    {
        public Task DispatchAsync(
            IEnumerable<IDomainEvent> events,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
