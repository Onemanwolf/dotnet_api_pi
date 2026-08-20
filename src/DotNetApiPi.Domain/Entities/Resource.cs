using System.Collections.Immutable;
using DotNetApiPi.Domain.Common;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.ValueObjects;
using DotNetApiPi.Domain.Events;

namespace DotNetApiPi.Domain.Entities;

/// <summary>
/// Aggregate root representing a generic resource.
/// <para>
/// This aggregate is intentionally domain-agnostic: it demonstrates clean,
/// expressive modeling (value objects, invariants, lifecycle transitions and
/// domain events) without being tied to a specific business domain.
/// </para>
/// <para>
/// <c>Archived</c> is a terminal state: once archived, the resource is
/// immutable and every mutator (<see cref="Rename"/>,
/// <see cref="SetDescription"/>, <see cref="AddTag"/>,
/// <see cref="SetTags"/>) throws <see cref="DomainException"/>
/// (HTTP 409 via the application layer's exception mapping).
/// </para>
/// <para>
/// <see cref="Version"/> is the aggregate's optimistic-concurrency token:
/// it starts at <c>0</c> and is incremented by exactly one for every actual
/// state change (mutators that are a no-op for the current state — e.g.
/// renaming to the same name — do not bump it). Persistence layers use it as
/// a compare-and-swap guard so concurrent writers cannot silently overwrite
/// each other.
/// </para>
/// </summary>
public sealed class Resource : AggregateRoot<Guid>
{
    /// <summary>
    /// The maximum number of characters a resource description may contain.
    /// Mirrors the EF Core <c>HasMaxLength(2048)</c> configuration so that the
    /// domain rejects over-length input before it ever reaches persistence.
    /// </summary>
    public const int MaxDescriptionLength = 2048;

    /// <summary>
    /// The maximum number of tags a resource may carry. Together with
    /// <see cref="ResourceTag.MaxLength"/> (64) this bounds the worst-case
    /// serialized tag blob at 50 × 64 = 3,200 characters plus JSON
    /// punctuation and escaping (≈3,400 characters), which fits within the
    /// persistence layer's 4,096-character cap on the tags column.
    /// </summary>
    public const int MaxTagCount = 50;

    /// <summary>
    /// Initializes a new instance of the <see cref="Resource"/> class.
    /// Used by the persistence layer. Application code must use
    /// <see cref="Create"/>.
    /// </summary>
    /// <param name="id">The identity of the resource.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="status">The status of the resource.</param>
    /// <param name="tags">The tags of the resource.</param>
    private Resource(
        Guid id,
        ResourceName name,
        string? description,
        ResourceStatus status,
        ImmutableArray<ResourceTag> tags)
    {
        Id = id;
        Name = name;
        Description = description;
        Status = status;
        Tags = tags;
    }

    /// <summary>
    /// Gets the name of the resource.
    /// </summary>
    public ResourceName Name { get; private set; }

    /// <summary>
    /// Gets the optional description of the resource.
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Gets the status of the resource.
    /// </summary>
    public ResourceStatus Status { get; private set; }

    /// <summary>
    /// Gets the tags of the resource.
    /// </summary>
    public ImmutableArray<ResourceTag> Tags { get; private set; } =
        ImmutableArray.Create<ResourceTag>();

    /// <summary>
    /// Gets the optimistic-concurrency version of the resource. Starts at
    /// <c>0</c> and is incremented by exactly one for every actual state
    /// change (see the class-level documentation for the no-op rule).
    /// <para>
    /// The persistence layer uses this value as a compare-and-swap guard
    /// (EF Core concurrency token; MongoDB replacement filter) so that a
    /// client whose view of the aggregate is stale fails with a conflict
    /// instead of silently overwriting a concurrent change. Set by the
    /// persistence layer on load (private setter); never by application code.
    /// </para>
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Creates a new <see cref="Resource"/> aggregate in the draft state and
    /// raises a <see cref="ResourceCreatedEvent"/>.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="tags">The optional set of tags to attach to the resource.</param>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> used to stamp the raised
    /// <see cref="ResourceCreatedEvent"/>. Defaults to
    /// <see cref="TimeProvider.System"/>; tests may supply a fixed clock.
    /// </param>
    /// <returns>A newly created resource.</returns>
    public static Resource Create(
        ResourceName name,
        string? description = null,
        IEnumerable<ResourceTag>? tags = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        var resource = new Resource(
            Guid.NewGuid(),
            name,
            ValidateDescription(description),
            ResourceStatus.Draft,
            NormalizeTags(tags));

        resource.AddDomainEvent(new ResourceCreatedEvent(
            resource.Id,
            (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime));

        return resource;
    }

    /// <summary>
    /// Reconstitutes an existing <see cref="Resource"/> from its persisted
    /// state without raising domain events. Intended for the persistence
    /// layer only (exposed as <c>internal</c> and made visible to the
    /// infrastructure assembly via <c>InternalsVisibleTo</c>); application
    /// code must go through <see cref="Create"/> or load the aggregate
    /// through a repository.
    /// </summary>
    /// <param name="id">The identity of the resource.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="status">The status of the resource.</param>
    /// <param name="tags">The tags of the resource.</param>
    /// <param name="version">The persisted optimistic-concurrency version.</param>
    /// <returns>The reconstituted resource.</returns>
    internal static Resource Reconstitute(
        Guid id,
        ResourceName name,
        string? description,
        ResourceStatus status,
        IEnumerable<ResourceTag>? tags,
        int version)
    {
        ArgumentNullException.ThrowIfNull(name);

        var resource = new Resource(id, name, description, status, NormalizeTags(tags));

        // Restore the persisted version without going through a mutator:
        // reconstitution must not (and cannot) re-raise domain events.
        resource.Version = version;
        return resource;
    }

    /// <summary>
    /// Renames the resource.
    /// </summary>
    /// <param name="name">The new name of the resource.</param>
    /// <exception cref="DomainException">
    /// Thrown when the resource is archived.
    /// </exception>
    public void Rename(ResourceName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        EnsureMutable();

        if (Name == name)
        {
            return;
        }

        Name = name;
        Version++;
    }

    /// <summary>
    /// Sets the description of the resource. Passing <c>null</c> clears the
    /// description.
    /// </summary>
    /// <param name="description">
    /// The new description of the resource, or <c>null</c> to clear it. Must
    /// not exceed <see cref="MaxDescriptionLength"/> characters.
    /// </param>
    /// <exception cref="DomainInputException">
    /// Thrown when the description exceeds <see cref="MaxDescriptionLength"/>.
    /// </exception>
    /// <exception cref="DomainException">
    /// Thrown when the resource is archived.
    /// </exception>
    public void SetDescription(string? description)
    {
        EnsureMutable();

        var validated = ValidateDescription(description);

        if (Description == validated)
        {
            // No actual state change: keep the version stable so that
            // no-op writes do not invalidate other clients' ETags.
            return;
        }

        Description = validated;
        Version++;
    }

    /// <summary>
    /// Activates the resource and raises a <see cref="ResourceActivatedEvent"/>.
    /// Only a resource in the draft state may be activated.
    /// </summary>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> used to stamp the raised event.
    /// Defaults to <see cref="TimeProvider.System"/>; tests may supply a
    /// fixed clock.
    /// </param>
    /// <exception cref="DomainException">
    /// Thrown when the resource is not in the draft state.
    /// </exception>
    public void Activate(TimeProvider? timeProvider = null)
    {
        if (Status != ResourceStatus.Draft)
        {
            throw new DomainException(
                $"Only a resource in the 'Draft' state can be activated. Current state: '{Status}'.");
        }

        Status = ResourceStatus.Active;
        Version++;
        AddDomainEvent(new ResourceActivatedEvent(
            Id,
            (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Archives the resource (terminal state) and raises a
    /// <see cref="ResourceArchivedEvent"/>. Only an active resource may be
    /// archived.
    /// </summary>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> used to stamp the raised event.
    /// Defaults to <see cref="TimeProvider.System"/>; tests may supply a
    /// fixed clock.
    /// </param>
    /// <exception cref="DomainException">
    /// Thrown when the resource is not in the active state.
    /// </exception>
    public void Archive(TimeProvider? timeProvider = null)
    {
        if (Status != ResourceStatus.Active)
        {
            throw new DomainException(
                $"Only an active resource can be archived. Current state: '{Status}'.");
        }

        Status = ResourceStatus.Archived;
        Version++;
        AddDomainEvent(new ResourceArchivedEvent(
            Id,
            (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Stages the deletion of the resource by raising a
    /// <see cref="ResourceDeletedEvent"/>. Deletion is a remove, not a state
    /// change: the aggregate's version is deliberately <em>not</em> bumped
    /// (the remove path is version-guarded via the version loaded in this
    /// unit of work, mirroring the persistence layer's compare-and-swap
    /// guard). The event is persisted through the outbox within the same
    /// transaction that removes the aggregate, so consumers are informed of
    /// the deletion even though the document no longer exists afterwards.
    /// </summary>
    /// <param name="timeProvider">
    /// An optional <see cref="TimeProvider"/> used to stamp the raised event.
    /// Defaults to <see cref="TimeProvider.System"/>; tests may supply a
    /// fixed clock.
    /// </param>
    public void Delete(TimeProvider? timeProvider = null)
    {
        AddDomainEvent(new ResourceDeletedEvent(
            Id,
            (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Adds a tag to the resource if it is not already present.
    /// </summary>
    /// <param name="tag">The tag to add.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the resource already has <see cref="MaxTagCount"/> tags.
    /// </exception>
    /// <exception cref="DomainException">
    /// Thrown when the resource is archived.
    /// </exception>
    public void AddTag(ResourceTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        EnsureMutable();

        if (Tags.Contains(tag))
        {
            return;
        }

        if (Tags.Length >= MaxTagCount)
        {
            throw new DomainInputException(
                $"A resource must not have more than {MaxTagCount} tags.");
        }

        Tags = Tags.Add(tag);
        Version++;
    }

    /// <summary>
    /// Replaces the current set of tags with the supplied collection.
    /// </summary>
    /// <param name="tags">The new set of tags, or <c>null</c> to clear them.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the collection holds more than <see cref="MaxTagCount"/> distinct tags.
    /// </exception>
    /// <exception cref="DomainException">
    /// Thrown when the resource is archived.
    /// </exception>
    public void SetTags(IEnumerable<ResourceTag>? tags)
    {
        EnsureMutable();

        var normalized = NormalizeTags(tags);

        if (Tags.SequenceEqual(normalized))
        {
            // Replacing the tag set with an identical one is a no-op: the
            // version stays stable (see the class-level version semantics).
            return;
        }

        Tags = normalized;
        Version++;
    }

    /// <summary>
    /// State guard: archived resources are immutable.
    /// <para>
    /// Called before input validation on every mutator so that an archived
    /// resource is always rejected with <see cref="DomainException"/>
    /// (HTTP 409), even when the supplied input would also be invalid
    /// (<see cref="DomainInputException"/> / HTTP 400). Null-argument checks
    /// (a caller programming error) still precede this guard.
    /// </para>
    /// </summary>
    private void EnsureMutable()
    {
        if (Status == ResourceStatus.Archived)
        {
            throw new DomainException("Archived resources are immutable.");
        }
    }

    /// <summary>
    /// Normalizes the supplied tags into a deduplicated, immutable collection.
    /// </summary>
    private static ImmutableArray<ResourceTag> NormalizeTags(IEnumerable<ResourceTag>? tags)
    {
        if (tags is null)
        {
            return ImmutableArray.Create<ResourceTag>();
        }

        // ToHashSet() already deduplicates, so no separate Distinct() pass is
        // needed.
        var distinct = tags
            .Where(static tag => tag is not null)
            .ToHashSet();

        if (distinct.Count > MaxTagCount)
        {
            throw new DomainInputException(
                $"A resource must not have more than {MaxTagCount} tags.");
        }

        return ImmutableArray.CreateRange(distinct);
    }

    /// <summary>
    /// Validates a description against the domain's length invariant.
    /// </summary>
    private static string? ValidateDescription(string? description)
    {
        if (description is null || description.Length <= MaxDescriptionLength)
        {
            return description;
        }

        throw new DomainInputException(
            $"A resource description must not exceed {MaxDescriptionLength} characters.");
    }
}
