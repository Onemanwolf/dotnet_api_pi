namespace DotNetApiPi.Application.Dtos;

/// <summary>
/// Read model representing a resource. This is the shape returned to API
/// consumers and is decoupled from the domain entity.
/// </summary>
public sealed class ResourceDto
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceDto"/> class.
    /// </summary>
    /// <param name="id">The identity of the resource.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="description">The optional description of the resource.</param>
    /// <param name="status">The status of the resource (the domain enum's name).</param>
    /// <param name="tags">The tags of the resource.</param>
    public ResourceDto(
        Guid id,
        string name,
        string? description,
        string status,
        IReadOnlyList<string> tags)
    {
        Id = id;
        Name = name;
        Description = description;
        Status = status;
        Tags = tags;
    }

    /// <summary>
    /// Gets the identity of the resource.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets the name of the resource.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the optional description of the resource.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the status of the resource, as a stable string such as
    /// <c>"Draft"</c>, <c>"Active"</c> or <c>"Archived"</c>. Exposing a
    /// string rather than the domain enum keeps the wire contract stable even
    /// if the domain model's enum changes.
    /// </summary>
    public string Status { get; }

    /// <summary>
    /// Gets the tags of the resource.
    /// </summary>
    public IReadOnlyList<string> Tags { get; }
}
