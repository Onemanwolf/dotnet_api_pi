namespace DotNetApiPi.Application.Common.Exceptions;

/// <summary>
/// Thrown when a unit of work is rejected because the aggregate it was based
/// on was modified concurrently (optimistic-concurrency conflict). The
/// presentation layer maps this exception to an HTTP 412 (Precondition
/// Failed) response.
/// </summary>
public sealed class ResourceConcurrencyException : Exception
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="ResourceConcurrencyException"/> class.
    /// </summary>
    /// <param name="resourceId">The identity of the conflicting resource.</param>
    /// <param name="innerException">
    /// The underlying persistence failure, when the conflict was detected at
    /// the persistence boundary (for example an EF Core
    /// <c>DbUpdateConcurrencyException</c>). May be <c>null</c>.
    /// </param>
    public ResourceConcurrencyException(
        Guid resourceId,
        Exception? innerException = null)
        : base(
            $"A concurrent modification was detected for the resource with the identity '{resourceId}'. " +
            "The client's view of the resource is stale; reload it and retry the operation.",
            innerException)
    {
        ResourceId = resourceId;
    }

    /// <summary>
    /// Gets the identity of the resource the conflict was detected for.
    /// </summary>
    public Guid ResourceId { get; }
}
