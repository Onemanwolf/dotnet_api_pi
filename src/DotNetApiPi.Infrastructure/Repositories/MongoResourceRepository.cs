using DotNetApiPi.Application.Common;
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
/// mutated — are written when <see cref="SaveChangesAsync"/> completes, at
/// which point the domain events they raised are dispatched and cleared.
/// Loaded-but-unmodified aggregates are skipped (the persisted document is
/// compared against the aggregate's current state), so a plain read followed
/// by a save performs no writes.
/// </para>
/// <para>
/// Known limitation: a single <see cref="SaveChangesAsync"/> call issues
/// independent insert/replace/delete commands. MongoDB only guarantees
/// atomicity across writes when a multi-document transaction is used, which
/// in turn requires a replica set. This scaffold targets a standalone single
/// server, so a failure between commands can leave the change set partially
/// applied (and domain events for the aggregates already written are still
/// dispatched). If a replica set is available, wrap the commands in a
/// client-session transaction to make the unit of work atomic.
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
    private readonly Dictionary<Guid, Resource> _toRemove = [];

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
        // the delete is applied to the database on SaveChangesAsync.
        _staged.Remove(resource.Id);
        _toRemove[resource.Id] = resource;

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
    public async Task<IReadOnlyList<Resource>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var documents = await _collection
            .AsQueryable()
            .OrderBy(document => document.Id)
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

        return resources;
    }

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        var affected = 0;

        // Plan the writes: inserts for new aggregates, replacements only for
        // aggregates whose state actually diverged from the persisted document
        // (a plain read with no mutation therefore triggers no writes).
        var inserts = new List<ResourceDocument>();
        var replacements = new List<(Guid Id, ResourceDocument Document)>();

        foreach (var (id, tracked) in _staged)
        {
            if (_toRemove.ContainsKey(id))
            {
                continue;
            }

            var document = ResourceDocumentMapper.ToDocument(tracked.Aggregate);

            if (tracked.Original is null)
            {
                inserts.Add(document);
            }
            else if (!DocumentsEqual(tracked.Original, document))
            {
                replacements.Add((id, document));
            }
        }

        if (inserts.Count > 0)
        {
            await _collection
                .InsertManyAsync(inserts, options: null, cancellationToken)
                .ConfigureAwait(false);
            affected += inserts.Count;
        }

        foreach (var (id, document) in replacements)
        {
            await _collection
                .ReplaceOneAsync(
                    Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                    document,
                    new ReplaceOptions(),
                    cancellationToken)
                .ConfigureAwait(false);
            affected++;
        }

        foreach (var id in _toRemove.Keys)
        {
            var result = await _collection
                .DeleteOneAsync(
                    Builders<ResourceDocument>.Filter.Eq(d => d.Id, id),
                    cancellationToken)
                .ConfigureAwait(false);
            affected += (int)result.DeletedCount;
        }

        // Dispatch the domain events raised by every staged aggregate, then
        // clear them so they are not dispatched twice (EF Core parity).
        var stagedAggregates = GetStagedAggregates();
        var events = stagedAggregates
            .Where(static aggregate => aggregate.DomainEvents.Count > 0)
            .SelectMany(static aggregate => aggregate.DomainEvents)
            .ToList();

        if (events.Count > 0)
        {
            await _dispatcher.DispatchAsync(events, cancellationToken).ConfigureAwait(false);
        }

        foreach (var aggregate in stagedAggregates)
        {
            ((IClearableDomainEvents)aggregate).ClearDomainEvents();
        }

        _staged.Clear();
        _toRemove.Clear();

        return affected;
    }

    /// <summary>
    /// Collects the aggregates touched by the current unit of work (inserts,
    /// updates and removals), each exactly once.
    /// </summary>
    private List<Resource> GetStagedAggregates()
    {
        var staged = new List<Resource>(_staged.Count + _toRemove.Count);

        foreach (var (id, tracked) in _staged)
        {
            if (!_toRemove.ContainsKey(id))
            {
                staged.Add(tracked.Aggregate);
            }
        }

        foreach (var resource in _toRemove.Values)
        {
            staged.Add(resource);
        }

        return staged;
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
            && left.Tags.SequenceEqual(right.Tags);
    }
}
