using DotNetApiPi.Infrastructure.Outbox;
using MongoDB.Driver;
using Xunit;

namespace DotNetApiPi.Infrastructure.Tests.Integration;

/// <summary>
/// Integration tests for the outbox claim contract against a real
/// MongoDB replica set (Testcontainers): concurrent claimants never claim
/// the same row, a mark with a foreign claim id is a no-op that leaves the
/// row untouched, and an expired lease makes the row reclaimable under a
/// fresh claim id. Tagged <c>Category=Integration</c>.
/// </summary>
public sealed class OutboxClaimIntegrationTests : IClassFixture<MongoReplicaSetFixture>
{
    private readonly MongoReplicaSetFixture _fixture;

    public OutboxClaimIntegrationTests(MongoReplicaSetFixture fixture)
    {
        _fixture = fixture;
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task ConcurrentClaims_NeverReturnTheSameRow()
    {
        var database = _fixture.CreateDatabase();
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);

        // 32 backlog rows, all claimable immediately, distinct claim gates
        // so no two claims race on the same instant.
        var now = DateTime.UtcNow;
        var documents = Enumerable.Range(0, 32)
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
                ClaimableAtUtc = now.AddMilliseconds(-i),
                ClaimId = Guid.Empty
            })
            .ToList();
        await outbox.InsertManyAsync(documents);

        // Two store instances racing: each claims until nothing publishable
        // remains (claimed rows go Publishing with a future lease, so they
        // drop out of the claimable set).
        var storeA = new MongoOutboxEventStore(database);
        var storeB = new MongoOutboxEventStore(database);
        var claimsA = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var claimsB = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        await Task.WhenAll(
            DrainAsync(storeA, claimsA),
            DrainAsync(storeB, claimsB));

        var allClaims = claimsA.Concat(claimsB).ToList();

        // Every row was claimed exactly once across both claimants.
        Assert.Equal(32, allClaims.Count);
        Assert.Equal(32, allClaims.Distinct().Count());
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task MarkPublished_WithForeignClaimId_IsNoOp_AndLeavesRowUntouched()
    {
        var database = _fixture.CreateDatabase();
        var store = new MongoOutboxEventStore(database);
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);

        var now = DateTime.UtcNow;
        await SeedPendingRowAsync(outbox, now);

        var claimed = await store
            .ClaimNextPublishableAsync(now, leaseSeconds: 300, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.NotEqual(Guid.Empty, claimed!.ClaimId);

        // A late writer (e.g. this relay's lease expired and a newer
        // claimant owns the row now) marks with a foreign claim id...
        var applied = await store
            .MarkPublishedAsync(
                claimed.EventId,
                Guid.NewGuid(), // foreign claim id
                0,
                1,
                DateTime.UtcNow,
                CancellationToken.None);

        // ...the mark is a detectable no-op, and the row is exactly as the
        // original claim left it.
        Assert.False(applied);

        var after = await outbox
            .Find(Builders<OutboxEventDocument>.Filter.Eq(d => d.Id, claimed.EventId))
            .FirstOrDefaultAsync();

        Assert.NotNull(after);
        Assert.Equal(OutboxEventStatus.Publishing, after!.Status);
        Assert.Equal(claimed.ClaimId, after.ClaimId);
        Assert.Equal(claimed.ClaimableAtUtc, after.ClaimableAtUtc);
        Assert.Null(after.PublishedAtUtc);
        Assert.Null(after.TopicPartition);
        Assert.Null(after.Offset);
    }

    [Trait("Category", "Integration")]
    [Fact(Timeout = 120_000)]
    public async Task ExpiredClaim_IsReclaimable_UnderFreshClaimId()
    {
        var database = _fixture.CreateDatabase();
        var store = new MongoOutboxEventStore(database);
        var outbox = database.GetCollection<OutboxEventDocument>(MongoOutboxEventStore.CollectionName);

        var now = DateTime.UtcNow;
        await SeedPendingRowAsync(outbox, now);

        // First claim with a 1 s lease: the row is claimable again one
        // second after the claim instant (the lease lives in
        // claimableAtUtc).
        var first = await store
            .ClaimNextPublishableAsync(now, leaseSeconds: 1, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(OutboxEventStatus.Publishing, first!.Status);

        await Task.Delay(TimeSpan.FromMilliseconds(1_500));

        // The lease has expired: the row is a crash leftover and the
        // claim picks it up again, with a fresh claim id.
        var second = await store
            .ClaimNextPublishableAsync(DateTime.UtcNow, leaseSeconds: 300, CancellationToken.None);

        Assert.NotNull(second);
        Assert.Equal(first.EventId, second!.EventId);
        Assert.NotEqual(first.ClaimId, second.ClaimId);
        Assert.Equal(OutboxEventStatus.Publishing, second.Status);
    }

    /// <summary>
    /// Claims rows until the publishable set is empty (a claimed row goes
    /// <c>Publishing</c> with a future lease and drops out of the set).
    /// </summary>
    private static async Task DrainAsync(
        MongoOutboxEventStore store,
        System.Collections.Concurrent.ConcurrentBag<Guid> claims)
    {
        while (true)
        {
            var record = await store
                .ClaimNextPublishableAsync(DateTime.UtcNow, leaseSeconds: 300, CancellationToken.None);

            if (record is null)
            {
                return;
            }

            claims.Add(record.EventId);
        }
    }

    private static Task SeedPendingRowAsync(
        IMongoCollection<OutboxEventDocument> outbox,
        DateTime now)
    {
        return outbox.InsertOneAsync(new OutboxEventDocument
        {
            Id = Guid.NewGuid(),
            EventType = "resource.activated.v1",
            ResourceId = Guid.NewGuid(),
            OccurredOnUtc = now,
            PayloadJson = "{}",
            Status = OutboxEventStatus.Pending,
            Attempts = 0,
            CreatedAtUtc = now,
            ClaimableAtUtc = now,
            ClaimId = Guid.Empty
        });
    }
}
