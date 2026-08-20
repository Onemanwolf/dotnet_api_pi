using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Events;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Infrastructure.Outbox;
using DotNetApiPi.Infrastructure.Persistence.Mongo;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace DotNetApiPi.Infrastructure.Repositories;

/// <summary>
/// MongoDB implementation of the <see cref="IResourceRepository"/> interface.
/// Persists <see cref="Resource"/> aggregates as documents through the MongoDB
/// driver.
/// <para>
/// It mirrors the unit-of-work semantics of the EF Core implementation:
/// aggregates staged through <see cref="AddAsync"/> or
/// <see cref="RemoveAsync"/> — or loaded through the read methods and then
/// mutated — are written when <see cref="SaveChangesAsync"/> completes.
/// Loaded-but-unmodified aggregates are skipped (the persisted document is
/// compared against the aggregate's current state), so a plain read followed
/// by a save performs no writes.
/// </para>
/// <para>
/// <b>Unit-of-work atomicity (transactional outbox).</b>
/// <see cref="SaveChangesAsync"/> plans the writes in a stable
/// (identity-sorted) order and applies them — plus the outbox rows for every
/// raised domain event — inside a <b>single client-session transaction</b>
/// (one <c>IClientSessionHandle</c>, whose <c>WithTransactionAsync</c> wraps
/// the first write through the last) — the outbox
/// rows commit or abort with the aggregate writes, which is the core
/// invariant of the transactional-outbox pattern: an event is handed to the
/// publisher <i>if and only if</i> the state change committed. In-memory
/// subscribers are then dispatched after the commit (the existing contract),
/// and the rows are picked up by the outbox relay for Kafka delivery.
/// </para>
/// <para>
/// <b>Replica set requirement.</b> Multi-document transactions are only
/// available on a replica set (standalone <c>mongod</c> cannot run them).
/// The compose stack runs a single-node replica set for this reason; against
/// a standalone server the transaction start fails with a
/// <c>TransactionalException</c> and the unit of work is aborted (nothing is
/// partially applied).
/// </para>
/// <para>
/// <b>Optimistic concurrency.</b> Replacements and removals are filtered on
/// both the identity and the version the aggregate was loaded with
/// (<c>Id == … AND Version == …</c>). If another writer committed a newer
/// version in the meantime, the filter matches nothing, the affected count is
/// zero, and <see cref="ResourceConcurrencyException"/> is thrown (HTTP 412)
/// instead of silently overwriting or deleting the concurrent change — and
/// the whole unit of work (all aggregates plus their outbox rows) is rolled
/// back with the failed write.
/// </para>
/// </summary>
public sealed class MongoResourceRepository : IResourceRepository
{
    /// <summary>
    /// A <see cref="Resource"/> that participates in the current unit of work,
    /// together with the document it was loaded from (<c>null</c> when the
    /// aggregate was staged for insertion and has no persisted state yet).
    /// </summary>
    private sealed record TrackedAggregate(Resource Aggregate, ResourceDocument? Original);

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoResourceRepository"/>
    /// class.
    /// </summary>
    /// <param name="client">The MongoDB client (used to start the
    /// unit-of-work client session/transaction).</param>
    /// <param name="collection">The collection that stores the resource documents.</param>
    /// <param name="outboxStore">The outbox event store (rows are written
    /// inside the unit-of-work transaction).</param>
    /// <param name="dispatcher">The domain event dispatcher.</param>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> used to stamp outbox rows
    /// (defaults to the system clock; tests may supply a fixed clock).
    /// </param>
    public MongoResourceRepository(
        IMongoClient client,
        IMongoCollection<ResourceDocument> collection,
        IOutboxEventStore outboxStore,
        IDomainEventDispatcher dispatcher,
        TimeProvider? timeProvider = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _outboxStore = outboxStore ?? throw new ArgumentNullException(nameof(outboxStore));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _time = timeProvider ?? TimeProvider.System;
    }

    private readonly IMongoClient _client;
    private readonly IMongoCollection<ResourceDocument> _collection;
    private readonly IOutboxEventStore _outboxStore;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly TimeProvider _time;

    // Unit of work: every aggregate staged (added, loaded and/or mutated) since
    // the last SaveChangesAsync call. Aggregates that were loaded from the
    // database carry the original document so unchanged reads can be skipped
    // at save time.
    private readonly Dictionary<Guid, TrackedAggregate> _staged = [];

    /// <summary>
    /// Aggregates staged for removal. <c>LoadedVersion</c> is the version the
    /// document was loaded with (when the aggregate went through the unit of
    /// work); it is <c>null</c> when a never-persisted aggregate is removed
    /// before its insert is applied, in which case the delete is plain
    /// (nothing can exist in the collection for that identity yet).
    /// </summary>
    private readonly Dictionary<Guid, (Resource Aggregate, int? LoadedVersion)> _toRemove = [];

    /// <inheritdoc />
    public Task<Resource> AddAsync(
        Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Staging an insert supersedes any previous staging of the same
        // identity (mirrors EF Core's state transitions for new entities).
        // A null "Original" marks the aggregate as a new insert.
        _staged[resource.Id] = new TrackedAggregate(resource, null);
        _toRemove.Remove(resource.Id);

        return Task.FromResult(resource);
    }

    /// <inheritdoc />
    public Task RemoveAsync(
        Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // Removing a never-persisted aggregate simply unstages it; otherwise
        // the delete is applied to the database on SaveChangesAsync. The
        // loaded version (if any) is kept so the delete can be filtered on
        // it, like the replace path.
        var loadedVersion = _staged.TryGetValue(resource.Id, out var tracked)
            ? tracked.Original?.Version
            : null;

        _staged.Remove(resource.Id);
        _toRemove[resource.Id] = (resource, loadedVersion);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Resource?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var filter = Builders<ResourceDocument>.Filter.Eq(document => document.Id, id);

        // Find (async cursor) rather than FindSync: FindSync would perform the
        // I/O synchronously on a thread-pool thread and defeat the async path.
        var document = await _collection
            .Find(filter)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        var resource = ResourceDocumentMapper.ToAggregate(document);

        // Load the aggregate into the unit of work so that mutations are
        // persisted when SaveChangesAsync is called (EF Core parity). The
        // original document is kept so an unchanged read produces no write.
        _staged[id] = new TrackedAggregate(resource, document);

        return resource;
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Resource> Items, int TotalCount)> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1)
        {
            throw new ArgumentException(
                $"Both page and pageSize must be positive (page: {page}, pageSize: {pageSize}).",
                nameof(page));
        }

        // Ordering by identity alone is a deterministic total order (mirrors
        // the EF Core implementation), so pages do not shift under
        // concurrent inserts.
        var totalCount = await _collection
            .CountDocumentsAsync(
                Builders<ResourceDocument>.Filter.Empty,
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        var documents = await _collection
            .AsQueryable()
            .OrderBy(static document => document.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var resources = new List<Resource>(documents.Count);

        foreach (var document in documents)
        {
            var resource = ResourceDocumentMapper.ToAggregate(document);
            resources.Add(resource);

            // Same unit-of-work tracking as GetByIdAsync.
            _staged[resource.Id] = new TrackedAggregate(resource, document);
        }

        return (resources, (int)totalCount);
    }

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var affected = 0;

        // Plan the writes: inserts for new aggregates, version-guarded
        // replacements only for aggregates whose state actually diverged from
        // the persisted document (a plain read with no mutation therefore
        // triggers no writes), and deletes for removed aggregates.
        //
        // The plan is processed in identity-sorted (stable) order so the
        // transaction touches the documents in a deterministic order (no
        // write-order surprises under concurrent units of work).
        var plan = new List<(Guid Id, TrackedAggregate? Tracked, (Resource, int?)? Removed)>();

        foreach (var (id, tracked) in _staged)
        {
            if (_toRemove.ContainsKey(id))
            {
                continue;
            }

            plan.Add((id, tracked, null));
        }

        foreach (var (id, removed) in _toRemove)
        {
            plan.Add((id, null, removed));
        }

        plan.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        // Transactional outbox: snapshot the raised domain events into outbox
        // rows BEFORE the write. The rows are inserted inside the same
        // transaction, so an event is persisted iff the aggregate write
        // commits. (The in-memory subscribers still see the same events after
        // the commit, below.)
        var now = _time.GetUtcNow().UtcDateTime;
        var outboxRecords = new List<OutboxEventRecord>();

        foreach (var (_, tracked, removed) in plan)
        {
            var aggregate = removed is not null
                ? removed.Value.Item1
                : tracked!.Aggregate;

            if (aggregate.DomainEvents.Count == 0)
            {
                continue;
            }

            foreach (var domainEvent in aggregate.DomainEvents)
            {
                outboxRecords.Add(new OutboxEventRecord(
                    Guid.NewGuid(),
                    domainEvent.GetType().Name,
                    aggregate.Id,
                    domainEvent.OccurredOn,
                    OutboxEventEnvelope.SerializeEvent(domainEvent),
                    OutboxEventStatus.Pending,
                    Attempts: 0,
                    CreatedAtUtc: now,
                    NextRetryAtUtc: null,
                    LeaseUntilUtc: null,
                    PublishedAtUtc: null,
                    TopicPartition: null,
                    Offset: null,
                    LastError: null));
            }
        }

        // One client-session transaction for the whole unit of work: every
        // aggregate write AND the outbox inserts commit or abort together.
        // (Requires a replica set — see the class documentation.)
        //
        // WithTransactionAsync manages the lifecycle: it starts the
        // transaction, runs the body, commits on success, aborts on any
        // exception, and transparently retries the whole body on transient
        // transaction errors (e.g. the primary stepping down mid-commit).
        // The body only reads its captures (plan / outboxRecords), so a
        // retry re-runs the same writes in a fresh transaction.
        var session = _client.StartSession();

        try
        {
            affected = await session
                .WithTransactionAsync(
                    async (tx, txCancellationToken) =>
                    {
                        var count = 0;

                        foreach (var (id, tracked, removed) in plan)
                        {
                            if (removed is not null)
                            {
                                var (_, loadedVersion) = _toRemove[id];

                                // Compare-and-swap delete (same guard as the
                                // replace path, and matching EF, whose DELETE
                                // also carries the concurrency token in its
                                // WHERE): a concurrent writer that committed
                                // a newer version in the meantime makes the
                                // filter miss, so deleting would silently
                                // lose that update. A delete of a
                                // never-persisted aggregate (nothing can
                                // exist yet) is left unfiltered.
                                var filter = loadedVersion is int version
                                    ? Builders<ResourceDocument>.Filter.And(
                                        Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                                        Builders<ResourceDocument>.Filter.Eq(d => d.Version, version))
                                    : Builders<ResourceDocument>.Filter.Eq(d => d.Id, id);

                                var result = await _collection
                                    .DeleteOneAsync(tx, filter, options: null, txCancellationToken)
                                    .ConfigureAwait(false);

                                if (loadedVersion is not null && result.DeletedCount == 0)
                                {
                                    throw new ResourceConcurrencyException(id);
                                }

                                count += (int)result.DeletedCount;
                            }
                            else if (tracked!.Original is null)
                            {
                                // A brand-new aggregate. (Staging an AddAsync
                                // for an id that was previously loaded in the
                                // same unit of work would race the persisted
                                // document and surface as a duplicate-key
                                // error — a programming error, like EF's
                                // Add-on-detached conflicts; new ids are
                                // Guid.NewGuid, so this is unreachable
                                // through the API.)
                                await _collection
                                    .InsertOneAsync(
                                        tx,
                                        ResourceDocumentMapper.ToDocument(tracked.Aggregate),
                                        options: null,
                                        txCancellationToken)
                                    .ConfigureAwait(false);
                                count++;
                            }
                            else
                            {
                                var document = ResourceDocumentMapper.ToDocument(tracked.Aggregate);

                                if (DocumentsEqual(tracked.Original, document))
                                {
                                    // Unchanged read: no write, nothing to
                                    // dispatch.
                                    continue;
                                }

                                // Compare-and-swap on the version the
                                // aggregate was loaded with: a concurrent
                                // write that committed a newer version in the
                                // meantime makes the filter miss and
                                // ModifiedCount stays zero.
                                var filter = Builders<ResourceDocument>.Filter.And(
                                    Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                                    Builders<ResourceDocument>.Filter.Eq(
                                        d => d.Version,
                                        tracked.Original.Version));

                                var result = await _collection
                                    .ReplaceOneAsync(
                                        tx,
                                        filter,
                                        document,
                                        new ReplaceOptions(),
                                        txCancellationToken)
                                    .ConfigureAwait(false);

                                if (result.ModifiedCount == 0)
                                {
                                    throw new ResourceConcurrencyException(id);
                                }

                                count++;
                            }
                        }

                        if (outboxRecords.Count > 0)
                        {
                            await _outboxStore
                                .AppendWithinTransactionAsync(outboxRecords, tx, txCancellationToken)
                                .ConfigureAwait(false);
                        }

                        return count;
                    },
                    new TransactionOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }

        // Commit succeeded: dispatch and clear the in-memory subscribers for
        // every staged aggregate (the pre-existing contract — subscribers
        // only ever see events whose persistence succeeded).
        foreach (var (_, tracked, removed) in plan)
        {
            var aggregate = removed is not null
                ? removed.Value.Item1
                : tracked!.Aggregate;

            if (aggregate.DomainEvents.Count > 0)
            {
                await _dispatcher
                    .DispatchAsync(aggregate.DomainEvents, cancellationToken)
                    .ConfigureAwait(false);
            }

            ((IClearableDomainEvents)aggregate).ClearDomainEvents();
        }

        _staged.Clear();
        _toRemove.Clear();

        return affected;
    }

    /// <summary>
    /// Compares two resource documents field by field so that unchanged reads
    /// can be skipped at save time.
    /// </summary>
    private static bool DocumentsEqual(ResourceDocument left, ResourceDocument right)
    {
        return left.Id == right.Id
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
            && string.Equals(left.Status, right.Status, StringComparison.Ordinal)
            && left.Tags.SequenceEqual(right.Tags)
            && left.Version == right.Version;
    }
}
