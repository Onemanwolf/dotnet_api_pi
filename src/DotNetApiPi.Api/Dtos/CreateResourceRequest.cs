namespace DotNetApiPi.Api.Dtos;

/// <summary>
/// Request model for creating a resource.
/// </summary>
/// <param name="Name">The name of the resource.</param>
/// <param name="Description">The optional description of the resource.</param>
/// <param name="Tags">The optional set of tags to attach to the resource.</param>
public sealed record CreateResourceRequest(
    string Name,
    string? Description,
    IReadOnlyList<string>? Tags)
{
}
