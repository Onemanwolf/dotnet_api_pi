using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to archive a resource.
/// </summary>
/// <param name="Id">The identity of the resource to archive.</param>
public sealed record ArchiveResourceCommand(
    Guid Id) : ICommand
{
}
