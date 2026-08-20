using DotNetApiPi.Domain.Entities;

namespace DotNetApiPi.Domain.Repositories;

/// <summary>
/// Defines the persistence contract for the <see cref="Resource"/> aggregate.
/// The interface lives in the domain so that the domain is not dependent on
/// any particular infrastructure technology (dependency inversion).
/// </summary>
public interface IResourceRepository
{
    /// <summary>
    /// Asynchronously adds a new aggregate to the repository.
    /// </summary>
    /// <param name="resource">The resource to add.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The persisted resource.</returns>
    Task<Resource> AddAsync(
        Resource resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously removes an aggregate from the repository.
    /// </summary>
    /// <param name="resource">The resource to remove.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    Task RemoveAsync(
        Resource resource,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves a resource by its identity.
    /// </summary>
    /// <param name="id">The identity of the resource.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The resource, or <c>null</c> when it does not exist.</returns>
    Task<Resource?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously retrieves one page of resources, ordered
    /// deterministically by identity (so pages do not shift under
    /// concurrent inserts), together with the total number of resources.
    /// </summary>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">The maximum number of items per page.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// The requested page of resources and the total number of resources in
    /// the store (before paging).
    /// </returns>
    Task<(IReadOnlyList<Resource> Items, int TotalCount)> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously persists all pending changes. Dispatches the domain
    /// events raised by any modified aggregates.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The number of rows affected by the save.</returns>
    Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default);
}
