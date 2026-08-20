using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Enums;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Infrastructure.Persistence.Mongo;

/// <summary>
/// Maps between the <see cref="ResourceDocument"/> persistence model and the
/// <see cref="Resource"/> aggregate. The aggregate is rebuilt through its
/// internal <c>Reconstitute</c> factory, so no domain invariants are bypassed
/// and no domain events are raised on load.
/// </summary>
public static class ResourceDocumentMapper
{
    /// <summary>
    /// Projects the aggregate onto its document representation.
    /// </summary>
    /// <param name="resource">The resource aggregate.</param>
    /// <returns>The document to persist.</returns>
    public static ResourceDocument ToDocument(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new ResourceDocument
        {
            Id = resource.Id,
            Name = resource.Name.Value,
            Description = resource.Description,
            Status = resource.Status.ToString(),
            Tags = resource.Tags
                .Select(static tag => tag.Value)
                .ToList()
        };
    }

    /// <summary>
    /// Reconstitutes the aggregate from its document representation.
    /// </summary>
    /// <param name="document">The persisted document.</param>
    /// <returns>The reconstituted resource aggregate.</returns>
    public static Resource ToAggregate(ResourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return Resource.Reconstitute(
            document.Id,
            new ResourceName(document.Name),
            document.Description,
            ParseStatus(document.Status),
            document.Tags
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => new ResourceTag(value)));
    }

    /// <summary>
    /// Parses a stored string value back into a <see cref="ResourceStatus"/>.
    /// </summary>
    private static ResourceStatus ParseStatus(string? value)
    {
        return value is null
            ? ResourceStatus.Draft
            : Enum.Parse<ResourceStatus>(value, ignoreCase: true);
    }
}
