namespace DotNetApiPi.Api.Dtos;

/// <summary>
/// Request model for updating a resource.
/// </summary>
/// <param name="Name">The new name of the resource.</param>
/// <param name="Description">The new optional description of the resource.</param>
/// <param name="Tags">The new set of tags to attach to the resource.</param>
public sealed record UpdateResourceRequest(
    string Name,
    string? Description,
    IReadOnlyList<string>? Tags)
{
}
