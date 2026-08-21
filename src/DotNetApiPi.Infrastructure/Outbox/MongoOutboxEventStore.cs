using MongoDB.Bson;
using MongoDB.Driver;

namespace DotNetApiPi.Infrastructure.Outbox;

/// <summary>
/// MongoDB implementation of <see cref="IOutboxEventStore"/> backed by the
/// <c>outbox_events</c> collection.
/// <para>
/// The claim is a single <c>findAndModify</c> (atomic on MongoDB), so
/// competing relay instances never claim the same row. The claim filter is
/// one index range — <c>status IN (Pending, Publishing) AND
/// claimableAtUtc &lt;= now</c> — and the sort is on <c>claimableAtUtc</c>
/// alone (the index's first key), so the whole claim is served by the
/// <c>status_claimableAtUtc</c> index without a blocking in-memory sort even
/// when a broker outage has built up a large backlog. (Sorting on a field
/// outside the index — e.g. an <c>_id</c> tie-break — would force MongoDB
/// to sort the whole backlog in memory before returning the first document.
///) Publishing/failure updates are
/// conditional on the row still carrying the caller's claim id, so a late
/// writer (whose lease expired in the meantime) never overwrites the row a
/// newer claimant owns — and the loss is detectable (the update matches
/// nothing) rather than inferred.
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

    private static readonly SortDefinitionBuilder<OutboxEventDocument> Sort =
        Builders<OutboxEventDocument>.Sort;

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
                ClaimableAtUtc = record.ClaimableAtUtc,
                ClaimId = record.ClaimId,
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
        // Publishable in one predicate: Pending rows whose backoff gate has
        // passed and Publishing rows whose lease expired (crash leftover).
        // Both conditions live in claimableAtUtc, so this is a single index
        // range — no $or, no blocking in-memory sort.
        var filter = Filter.And(
            Filter.In(
                d => d.Status,
                new[] { OutboxEventStatus.Pending, OutboxEventStatus.Publishing }),
            Filter.Lte(d => d.ClaimableAtUtc, now));

        // Claim = take ownership: fresh claim id and the claim gate pushed
        // to the lease expiry (the lease itself — expiry makes the row
        // claimable again — lives in the same field, so there is nothing
        // else to write).
        var leaseUntil = now.AddSeconds(leaseSeconds);
        var update = Update
            .Set(d => d.Status, OutboxEventStatus.Publishing)
            .Set(d => d.ClaimId, Guid.NewGuid())
            .Set(d => d.ClaimableAtUtc, leaseUntil)
            .Set(d => d.LastError, null);

        // A single atomic find-and-update (findAndModify): exactly one relay
        // instance can claim a given row.
        var claimed = await _collection
            .FindOneAndUpdateAsync(
                filter,
                update,
                new FindOneAndUpdateOptions<OutboxEventDocument, OutboxEventDocument>
                {
                    ReturnDocument = ReturnDocument.After,
                    // Index-only sort: claimableAtUtc is the first key of the
                    // {status, claimableAtUtc} index, so this never triggers
                    // a blocking SORT stage (a second sort key outside the
                    // index, e.g. _id, would sort the whole backlog in
                    // memory before the first document is returned).
                    // Ties on claimableAtUtc are ordered arbitrarily — any
                    // order is fair for delivery.
                    Sort = Sort.Ascending(d => d.ClaimableAtUtc)
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (claimed is null)
        {
            return null;
        }

        return ToRecord(claimed);
    }

    /// <inheritdoc />
    public async Task<bool> MarkPublishedAsync(
        Guid eventId,
        Guid claimId,
        int partition,
        long offset,
        DateTime publishedAtUtc,
        CancellationToken cancellationToken)
    {
        // Conditional on the row still being Publishing AND still owned by
        // this claim: a lease-expired takeover by another relay makes this
        // a no-op (MatchedCount 0) instead of an overwrite.
        var result = await _collection
            .UpdateOneAsync(
                Filter.And(
                    Filter.Eq(d => d.Id, eventId),
                    Filter.Eq(d => d.Status, OutboxEventStatus.Publishing),
                    Filter.Eq(d => d.ClaimId, claimId)),
                Update
                    .Set(d => d.Status, OutboxEventStatus.Published)
                    .Set(d => d.PublishedAtUtc, publishedAtUtc)
                    .Set(d => d.TopicPartition, partition)
                    .Set(d => d.Offset, offset)
                    .Set(d => d.LastError, null),
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.MatchedCount > 0;
    }

    /// <inheritdoc />
    public async Task<bool> MarkFailedAsync(
        Guid eventId,
        Guid claimId,
        int newAttempts,
        DateTime? retryAtUtc,
        string? lastError,
        CancellationToken cancellationToken)
    {
        // null gate => the retry budget is exhausted => Dead; otherwise the
        // row goes back to Pending, claimable again after the backoff.
        var status = retryAtUtc is null
            ? OutboxEventStatus.Dead
            : OutboxEventStatus.Pending;

        var update = Update
            .Set(d => d.Status, status)
            .Set(d => d.Attempts, newAttempts)
            .Set(d => d.LastError, lastError);

        // The backoff gate only exists while the row is retriable; Dead rows
        // are terminal (operators replay them on purpose — see README).
        if (retryAtUtc is not null)
        {
            update = update.Set(d => d.ClaimableAtUtc, retryAtUtc.Value);
        }

        var result = await _collection
            .UpdateOneAsync(
                Filter.And(
                    Filter.Eq(d => d.Id, eventId),
                    Filter.Eq(d => d.Status, OutboxEventStatus.Publishing),
                    Filter.Eq(d => d.ClaimId, claimId)),
                update,
                null,
                cancellationToken)
            .ConfigureAwait(false);

        return result.MatchedCount > 0;
    }

    private static OutboxEventRecord ToRecord(OutboxEventDocument document)
        => new(
            document.Id,
            document.EventType,
            document.ResourceId,
            document.OccurredOnUtc,
            document.PayloadJson,
            document.Status,
            document.Attempts,
            document.CreatedAtUtc,
            document.ClaimableAtUtc,
            document.ClaimId,
            document.PublishedAtUtc,
            document.TopicPartition,
            document.Offset,
            document.LastError);
}
