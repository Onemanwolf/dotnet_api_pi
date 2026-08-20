using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Application.Tests;

/// <summary>
/// Builds <see cref="Resource"/> aggregates in the various lifecycle states
/// using only the public aggregate API, so tests exercise the same
/// behaviour (validation, state transitions) that production code does.
/// </summary>
internal static class ResourceFactory
{
    /// <summary>
    /// Creates a resource in the draft state.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="tags">The optional tags of the resource.</param>
    /// <returns>A draft resource.</returns>
    public static Resource Draft(
        string name = "Test Resource",
        string? description = null,
        params string[] tags)
    {
        return Resource.Create(
            new ResourceName(name),
            description,
            ToTags(tags));
    }

    /// <summary>
    /// Creates and activates a resource.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="tags">The optional tags of the resource.</param>
    /// <returns>An active resource.</returns>
    public static Resource Active(
        string name = "Test Resource",
        string? description = null,
        params string[] tags)
    {
        var resource = Draft(name, description, tags);
        resource.Activate();
        return resource;
    }

    /// <summary>
    /// Creates, activates and archives a resource.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="tags">The optional tags of the resource.</param>
    /// <returns>An archived resource.</returns>
    public static Resource Archived(
        string name = "Test Resource",
        string? description = null,
        params string[] tags)
    {
        var resource = Active(name, description, tags);
        resource.Archive();
        return resource;
    }

    /// <summary>
    /// Converts raw tag strings into <see cref="ResourceTag"/> value objects.
    /// </summary>
    private static IEnumerable<ResourceTag>? ToTags(params string[] tags)
    {
        return tags.Length == 0 ? null : tags.Select(static tag => new ResourceTag(tag));
    }
}
