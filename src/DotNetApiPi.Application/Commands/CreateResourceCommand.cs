using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to create a new resource.
/// </summary>
/// <param name="Name">The name of the resource.</param>
/// <param name="Description">The optional description of the resource.</param>
/// <param name="Tags">The optional set of tags to attach to the resource.</param>
public sealed record CreateResourceCommand(
    string Name,
    string? Description,
    IReadOnlyCollection<string>? Tags) : ICommand
{
}
