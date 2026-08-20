using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Command to archive a resource.
/// </summary>
/// <param name="Id">The identity of the resource to archive.</param>
/// <param name="ExpectedVersion">
/// The version the client based its request on (from the <c>If-Match</c>
/// header), or <c>null</c> when the client asserted no version precondition.
/// The handler rejects the archive with a conflict when this value does not
/// match the loaded aggregate's version (optimistic concurrency).
/// </param>
public sealed record ArchiveResourceCommand(
    Guid Id,
    int? ExpectedVersion = null) : ICommand;
