using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to update an existing resource.
/// </summary>
/// <param name="Id">The identity of the resource to update.</param>
/// <param name="Name">The new name of the resource.</param>
/// <param name="Description">The new description of the resource.</param>
/// <param name="Tags">The new set of tags to attach to the resource.</param>
public sealed record UpdateResourceCommand(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyCollection<string>? Tags) : ICommand
{
}
