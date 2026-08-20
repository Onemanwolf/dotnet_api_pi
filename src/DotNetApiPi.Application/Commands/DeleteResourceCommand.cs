using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to delete a resource.
/// </summary>
/// <param name="Id">The identity of the resource to delete.</param>
public sealed record DeleteResourceCommand(
    Guid Id) : ICommand
{
}
