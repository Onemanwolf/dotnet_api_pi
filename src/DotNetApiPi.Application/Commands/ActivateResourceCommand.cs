using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to activate a resource.
/// </summary>
/// <param name="Id">The identity of the resource to activate.</param>
/// <param name="ExpectedVersion">
/// The version the client based its request on (from the <c>If-Match</c>
/// header), or <c>null</c> when the client asserted no version precondition.
/// The handler rejects the activation with a conflict when this value does
/// not match the loaded aggregate's version (optimistic concurrency).
/// </param>
public sealed record ActivateResourceCommand(
    Guid Id,
    int? ExpectedVersion = null) : ICommand;
