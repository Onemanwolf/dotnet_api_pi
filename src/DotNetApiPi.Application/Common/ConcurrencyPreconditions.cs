using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;

namespace DotNetApiPi.Application.Common;

/// <summary>
/// Shared precondition check for optimistic concurrency: verifies that the
/// <c>If-Match</c>-derived version a client supplied still matches the
/// loaded aggregate before the aggregate is mutated.
/// <para>
/// This is the application-layer line of defence (it rejects a stale client
/// view before any mutation is applied). Persistence layers add a second
/// line — an EF Core concurrency token / a MongoDB version filter — which
/// covers the narrow window between loading the aggregate and writing it
/// back within the same request.
/// </para>
/// </summary>
public static class ConcurrencyPreconditions
{
    /// <summary>
    /// Throws <see cref="ResourceConcurrencyException"/> when the client's
    /// expected version does not match the aggregate's current version.
    /// </summary>
    /// <param name="resource">The freshly loaded aggregate.</param>
    /// <param name="expectedVersion">
    /// The version the client based its request on (parsed from
    /// <c>If-Match</c>), or <c>null</c> when the client asserted no
    /// precondition (for example <c>If-Match: *</c>), in which case the
    /// check is skipped.
    /// </param>
    /// <exception cref="ResourceConcurrencyException">
    /// Thrown when <paramref name="expectedVersion"/> does not equal
    /// <see cref="Resource.Version"/>.
    /// </exception>
    public static void EnsureMatches(
        Resource resource,
        int? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (expectedVersion is { } expected
            && expected != resource.Version)
        {
            throw new ResourceConcurrencyException(resource.Id);
        }
    }
}
