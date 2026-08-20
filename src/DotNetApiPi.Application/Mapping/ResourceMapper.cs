using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Domain.Entities;

namespace DotNetApiPi.Application.Mapping;

/// <summary>
/// Maps domain aggregates to application view models (DTOs).
/// Mapping keeps the application layer independent of the way data is
/// persisted and prevents leaking domain internals to the presentation layer.
/// </summary>
public static class ResourceMapper
{
    /// <summary>
    /// Maps a single <see cref="Resource"/> aggregate to a
    /// <see cref="ResourceDto"/>.
    /// </summary>
    /// <param name="resource">The resource to map.</param>
    /// <returns>The mapped <see cref="ResourceDto"/>.</returns>
    public static ResourceDto ToDto(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new ResourceDto(
            resource.Id,
            resource.Name.Value,
            resource.Description,
            // Map the domain enum to its string name at the DTO boundary so
            // the wire contract does not leak the domain type.
            resource.Status.ToString(),
            resource.Tags.Select(static tag => tag.Value).ToList());
    }

    /// <summary>
    /// Maps a possibly-<c>null</c> <see cref="Resource"/> aggregate to a
    /// <see cref="ResourceDto"/>, returning <c>null</c> when the input is
    /// <c>null</c>.
    /// </summary>
    /// <param name="resource">The resource to map. May be <c>null</c>.</param>
    /// <returns>A <see cref="ResourceDto"/>, or <c>null</c> when the input is <c>null</c>.</returns>
    public static ResourceDto? ToDtoOrNull(Resource? resource)
        => resource is null ? null : ToDto(resource);

    /// <summary>
    /// Maps a collection of <see cref="Resource"/> aggregates to DTOs.
    /// </summary>
    /// <param name="resources">The resources to map.</param>
    /// <returns>A list of <see cref="ResourceDto"/>.</returns>
    public static IReadOnlyList<ResourceDto> ToDto(IEnumerable<Resource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);

        return resources
            .Select(ToDto)
            .ToList();
    }
}
