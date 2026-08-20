using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Common;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
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
/// <b>Per-aggregate atomicity.</b> <see cref="SaveChangesAsync"/> processes the
/// staged change set one aggregate at a time, in a stable (identity-sorted)
/// order: for each aggregate it issues the single write command that covers
/// that aggregate (insert, version-guarded replace, or delete) and only then
/// dispatches and clears <i>that aggregate's</i> domain events. The unit of
/// work therefore degrades in a predictable way: if a write fails or the
/// process dies mid-save, every earlier aggregate is fully persisted <i>and</i>
/// fully dispatched, and no later aggregate has been touched — events are
/// never dispatched for an aggregate whose write did not succeed.
/// </para>
/// <para>
/// <b>Optimistic concurrency.</b> Replacements and removals are filtered on
/// both the identity and the version the aggregate was loaded with
/// (<c>Id == … AND Version == …</c>). If another writer committed a newer
/// version in the meantime, the filter matches nothing, the affected count is
/// zero, and <see cref="ResourceConcurrencyException"/> is thrown (HTTP 412)
/// instead of silently overwriting or deleting the concurrent change.
/// </para>
/// <para>
/// <b>Residual risk (accepted for this scaffold).</b> The writes above are
/// still independent commands, so a failure <i>between</i> aggregates leaves
/// the change set partially applied. True multi-document atomicity requires a
/// MongoDB transaction, which requires a replica set; this scaffold targets a
/// standalone single server, where transactions are unavailable. If a replica
/// set becomes available, wrap the per-aggregate commands in a client-session
/// transaction (one <c>IClientSessionHandle</c>, <c>StartTransactionAsync</c>
/// before the first write, <c>CommitTransactionAsync</c> after the last) to
/// make the whole unit of work atomic.
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
    /// <param name="collection">The collection that stores the resource documents.</param>
    /// <param name="dispatcher">The domain event dispatcher.</param>
    public MongoResourceRepository(
        IMongoCollection<ResourceDocument> collection,
        IDomainEventDispatcher dispatcher)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    private readonly IMongoCollection<ResourceDocument> _collection;
    private readonly IDomainEventDispatcher _dispatcher;

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
        // The plan is processed one aggregate at a time, in identity-sorted
        // (stable) order: each aggregate's write completes and its domain
        // events are dispatched and cleared before the next aggregate starts,
        // so a failure mid-save leaves the change set in a predictable
        // per-aggregate state (see the class documentation).
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

        foreach (var (id, tracked, removed) in plan)
        {
            if (removed is not null)
            {
                var (_, loadedVersion) = _toRemove[id];

                // Compare-and-swap delete (same guard as the replace path,
                // and matching EF, whose DELETE also carries the concurrency
                // token in its WHERE): a concurrent writer that committed a
                // newer version in the meantime makes the filter miss, so
                // deleting would silently lose that update. A delete of a
                // never-persisted aggregate (nothing can exist yet) is left
                // unfiltered.
                var filter = loadedVersion is int version
                    ? Builders<ResourceDocument>.Filter.And(
                        Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                        Builders<ResourceDocument>.Filter.Eq(d => d.Version, version))
                    : Builders<ResourceDocument>.Filter.Eq(d => d.Id, id);

                var result = await _collection
                    .DeleteOneAsync(filter, cancellationToken)
                    .ConfigureAwait(false);

                if (loadedVersion is not null && result.DeletedCount == 0)
                {
                    throw new ResourceConcurrencyException(id);
                }

                affected += (int)result.DeletedCount;
            }
            else if (tracked!.Original is null)
            {
                // A brand-new aggregate. (Staging an AddAsync for an id that
                // was previously loaded in the same unit of work would race
                // the persisted document and surface as a duplicate-key
                // error — a programming error, like EF's Add-on-detached
                // conflicts; new ids are Guid.NewGuid, so this is
                // unreachable through the API.)
                await _collection
                    .InsertOneAsync(
                        ResourceDocumentMapper.ToDocument(tracked.Aggregate),
                        options: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                affected++;
            }
            else
            {
                var document = ResourceDocumentMapper.ToDocument(tracked.Aggregate);

                if (DocumentsEqual(tracked.Original, document))
                {
                    // Unchanged read: no write, nothing to dispatch.
                    continue;
                }

                // Compare-and-swap on the version the aggregate was loaded
                // with: a concurrent write that committed a newer version in
                // the meantime makes the filter miss and ModifiedCount stays
                // zero.
                var filter = Builders<ResourceDocument>.Filter.And(
                    Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                    Builders<ResourceDocument>.Filter.Eq(
                        d => d.Version,
                        tracked.Original.Version));

                var result = await _collection
                    .ReplaceOneAsync(
                        filter,
                        document,
                        new ReplaceOptions(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (result.ModifiedCount == 0)
                {
                    throw new ResourceConcurrencyException(id);
                }

                affected++;
            }

            // Per-aggregate unit of work: dispatch and clear only the events
            // raised by this aggregate now that its own write has succeeded
            // (a later aggregate's failure cannot leave this aggregate's
            // events behind, and a write failure above skips dispatching
            // entirely for the failed aggregate).
            var aggregate = removed.HasValue
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
