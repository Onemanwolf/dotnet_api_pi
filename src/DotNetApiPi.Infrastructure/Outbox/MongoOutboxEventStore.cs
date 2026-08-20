using MongoDB.Bson;
using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// MongoDB implementation of <see cref="IOutboxEventStore"/> backed by the
/// <c>outbox_events</c> collection.
/// <para>
/// The claim is a single <c>findAndModify</c> (atomic on MongoDB), so
/// competing relay instances never claim the same row; the publish/failure
/// updates are conditional on the row still being <c>Publishing</c>, so a
/// late writer (whose lease expired in the meantime) never overwrites the
/// row a newer claimant owns.
/// </para>
/// </summary>
public sealed class MongoOutboxEventStore : IOutboxEventStore
{
    /// <summary>
    /// The outbox collection name.
    /// </summary>
    public const string CollectionName = "outbox_events";

    private static readonly FilterDefinitionBuilder<OutboxEventDocument> Filter =
        Builders<OutboxEventDocument>.Filter;

    private static readonly UpdateDefinitionBuilder<OutboxEventDocument> Update =
        Builders<OutboxEventDocument>.Update;

    private readonly IMongoCollection<OutboxEventDocument> _collection;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoOutboxEventStore"/>
    /// class.
    /// </summary>
    /// <param name="database">The database that hosts the outbox collection
    /// (the same database as the aggregate documents).</param>
    public MongoOutboxEventStore(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _collection = database.GetCollection<OutboxEventDocument>(CollectionName);
    }

    /// <summary>
    /// Gets the collection handle (used by the infrastructure initializer to
    /// create indexes).
    /// </summary>
    public IMongoCollection<OutboxEventDocument> Collection => _collection;

    /// <inheritdoc />
    public async Task AppendWithinTransactionAsync(
        IReadOnlyList<OutboxEventRecord> records,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(session);

        if (records.Count == 0)
        {
            return;
        }

        var documents = records
            .Select(static record => new OutboxEventDocument
            {
                Id = record.EventId,
                EventType = record.EventType,
                ResourceId = record.ResourceId,
                OccurredOnUtc = record.OccurredOnUtc,
                PayloadJson = record.PayloadJson,
                Status = record.Status,
                Attempts = record.Attempts,
                CreatedAtUtc = record.CreatedAtUtc,
                NextRetryAtUtc = record.NextRetryAtUtc,
                LeaseUntilUtc = record.LeaseUntilUtc,
                PublishedAtUtc = record.PublishedAtUtc,
                TopicPartition = record.TopicPartition,
                Offset = record.Offset,
                LastError = record.LastError
            })
            .ToArray();

        // All rows or none: the whole append runs inside the caller's
        // transaction, so it commits with the aggregate write or is aborted
        // with it. (Session travels as the first argument in driver 3.x.)
        await _collection
            .InsertManyAsync(session, documents, options: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OutboxEventRecord?> ClaimNextPublishableAsync(
        DateTime now,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        // Publishable: Pending rows whose backoff gate has passed (or has no
        // gate), or Publishing rows whose lease expired (crash leftover).
        var filter = Filter.Or(
            Filter.And(
                Filter.Eq(d => d.Status, OutboxEventStatus.Pending),
                Filter.Or(
                    Filter.Eq(d => d.NextRetryAtUtc, (DateTime?)null),
                    Filter.Lte(d => d.NextRetryAtUtc, now))),
            Filter.And(
                Filter.Eq(d => d.Status, OutboxEventStatus.Publishing),
                Filter.Lte(d => d.LeaseUntilUtc, now)));

        var update = Update
            .Set(d => d.Status, OutboxEventStatus.Publishing)
            .Set(d => d.LeaseUntilUtc, now.AddSeconds(leaseSeconds))
            .Set(d => d.LastError, null);

        // A single atomic find-and-update (findAndModify) ordered by
        // creation time (id as tie-break): exactly one relay instance can
        // claim a given row, and the oldest event is always claimed first.
        var claimed = await _collection
            .FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<OutboxEventDocument, OutboxEventDocument>
                {
                    ReturnDocument = ReturnDocument.After,
                    Sort = Builders<OutboxEventDocument>.Sort.Combine(
                        Builders<OutboxEventDocument>.Sort.Ascending(d => d.CreatedAtUtc),
                        Builders<OutboxEventDocument>.Sort.Ascending(d => d.Id))
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return null;
        }

        return new OutboxEventRecord(
            claimed.Id,
            claimed.EventType,
            claimed.ResourceId,
            claimed.OccurredOnUtc,
            claimed.PayloadJson,
            claimed.Status,
            claimed.Attempts,
            claimed.CreatedAtUtc,
            claimed.NextRetryAtUtc,
            claimed.LeaseUntilUtc,
            claimed.PublishedAtUtc,
            claimed.TopicPartition,
            claimed.Offset,
            claimed.LastError);
    }

    /// <inheritdoc />
    public async Task<bool> MarkPublishedAsync(
        Guid eventId,
        int partition,
        long offset,
        DateTime publishedAtUtc,
        CancellationToken cancellationToken)
    {
        var result = await _collection
            .UpdateOneAsync(
                Filter.And(
                    Filter.Eq(d => d.Id, eventId),
                    Filter.Eq(d => d.Status, OutboxEventStatus.Publishing)),
                Update
                    .Set(d => d.Status, OutboxEventStatus.Published)
                    .Set(d => d.PublishedAtUtc, publishedAtUtc)
                    .Set(d => d.TopicPartition, partition)
                    .Set(d => d.Offset, offset)
                    .Set(d => d.LeaseUntilUtc, null)
                    .Set(d => d.LastError, null),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.MatchedCount > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        Guid eventId,
        int newAttempts,
        DateTime? nextRetryAtUtc,
        string? lastError,
        CancellationToken cancellationToken)
    {
        // null gate => the retry budget is exhausted => Dead; otherwise the
        // row goes back to Pending and is claimable after the backoff.
        var status = nextRetryAtUtc is null
            ? OutboxEventStatus.Dead
            : OutboxEventStatus.Pending;

        var result = await _collection
            .UpdateOneAsync(
                Filter.And(
                    Filter.Eq(d => d.Id, eventId),
                    Filter.Eq(d => d.Status, OutboxEventStatus.Publishing)),
                Update
                    .Set(d => d.Status, status)
                    .Set(d => d.Attempts, newAttempts)
                    .Set(d => d.NextRetryAtUtc, nextRetryAtUtc)
                    .Set(d => d.LeaseUntilUtc, null)
                    .Set(d => d.LastError, lastError),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.MatchedCount > 0;
    }
}
