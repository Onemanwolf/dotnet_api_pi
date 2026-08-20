using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to update an existing resource's name, description or tags.
/// </summary>
/// <param name="Id">The identity of the resource to update.</param>
/// <param name="Name">The new name of the resource.</param>
/// <param name="Description">The new description of the resource.</param>
/// <param name="Tags">The new tags of the resource.</param>
/// <param name="ExpectedVersion">
/// The version the client based its request on (from the <c>If-Match</c>
/// header), or <c>null</c> when the client asserted no version precondition.
/// The handler rejects the update with a conflict when this value does not
/// match the loaded aggregate's version (optimistic concurrency).
/// </param>
public sealed record UpdateResourceCommand(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<string>? Tags,
    int? ExpectedVersion = null) : ICommand;
