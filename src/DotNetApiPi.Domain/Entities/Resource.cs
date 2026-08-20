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
    /// <see cref="ResourceTag.MaxLength"/> this keeps the serialized tag blob
    /// well within the persistence layer's column cap.
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
    /// <returns>The reconstituted resource.</returns>
    internal static Resource Reconstitute(
        Guid id,
        ResourceName name,
        string? description,
        ResourceStatus status,
        IEnumerable<ResourceTag>? tags)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new Resource(id, name, description, status, NormalizeTags(tags));
    }

    /// <summary>
    /// Renames the resource.
    /// </summary>
    /// <param name="name">The new name of the resource.</param>
    public void Rename(ResourceName name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (Name == name)
        {
            return;
        }

        Name = name;
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
    public void SetDescription(string? description)
    {
        Description = ValidateDescription(description);
    }

    /// <summary>
    /// Activates the resource. Only a resource in the draft state may be
    /// activated.
    /// </summary>
    /// <exception cref="DomainException">
    /// Thrown when the resource is not in the draft state.
    /// </exception>
    public void Activate()
    {
        if (Status != ResourceStatus.Draft)
        {
            throw new DomainException(
                $"Only a resource in the 'Draft' state can be activated. Current state: '{Status}'.");
        }

        Status = ResourceStatus.Active;
    }

    /// <summary>
    /// Archives the resource. Only an active resource may be archived.
    /// </summary>
    /// <exception cref="DomainException">
    /// Thrown when the resource is not in the active state.
    /// </exception>
    public void Archive()
    {
        if (Status != ResourceStatus.Active)
        {
            throw new DomainException(
                $"Only an active resource can be archived. Current state: '{Status}'.");
        }

        Status = ResourceStatus.Archived;
    }

    /// <summary>
    /// Adds a tag to the resource if it is not already present.
    /// </summary>
    /// <param name="tag">The tag to add.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the resource already has <see cref="MaxTagCount"/> tags.
    /// </exception>
    public void AddTag(ResourceTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

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
    }

    /// <summary>
    /// Replaces the current set of tags with the supplied collection.
    /// </summary>
    /// <param name="tags">The new set of tags, or <c>null</c> to clear them.</param>
    /// <exception cref="DomainInputException">
    /// Thrown when the collection holds more than <see cref="MaxTagCount"/> distinct tags.
    /// </exception>
    public void SetTags(IEnumerable<ResourceTag>? tags)
    {
        Tags = NormalizeTags(tags);
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
