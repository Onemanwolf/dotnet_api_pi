using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to activate a resource (moves it from the draft state to active).
/// </summary>
/// <param name="Id">The identity of the resource to activate.</param>
public sealed record ActivateResourceCommand(Guid Id) : ICommand
{
}
