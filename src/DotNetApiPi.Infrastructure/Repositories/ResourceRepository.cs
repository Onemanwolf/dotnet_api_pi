using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DotNetApiPi.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of the <see cref="IResourceRepository"/> interface.
/// Persists <see cref="Resource"/> aggregates using the application's
/// <see cref="ApiDbContext"/>.
/// <para>
/// Optimistic concurrency: the aggregate's <see cref="Resource.Version"/> is
/// mapped as an EF Core concurrency token (see
/// <see cref="ApiDbContext.OnModelCreating"/>), so EF includes the original
/// version in every <c>UPDATE</c>'s <c>WHERE</c> clause. A save whose
/// aggregate was modified concurrently fails with
/// <see cref="DbUpdateConcurrencyException"/>, which this repository
/// translates into <see cref="ResourceConcurrencyException"/> (mapped to
/// HTTP 412 by the presentation layer).
/// </para>
/// </summary>
public sealed class ResourceRepository : IResourceRepository
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceRepository"/> class.
    /// </summary>
    /// <param name="context">The application's database context.</param>
    public ResourceRepository(ApiDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    private readonly ApiDbContext _context;

    /// <inheritdoc />
    public async Task<Resource> AddAsync(
        Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        await _context.Resources.AddAsync(resource, cancellationToken).ConfigureAwait(false);
        return resource;
    }

    /// <inheritdoc />
    public Task RemoveAsync(
        Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        _context.Resources.Remove(resource);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Resource?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        return await _context.Resources
            .FindAsync(new object[] { id }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<Resource> Items, int TotalCount)> GetPageAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize < 1)
        {
            throw new ArgumentException(
                $"Both page and pageSize must be positive (page: {page}, pageSize: {pageSize}).",
                nameof(page));
        }

        // Ordering by identity alone is a deterministic total order: resources
        // are never re-ordered across pages and new inserts cannot shift an
        // already-materialised page (Id has no timestamps to collide on).
        var totalCount = await _context.Resources
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);

        var items = await _context.Resources
            .OrderBy(static resource => resource.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return (items, totalCount);
    }

    /// <inheritdoc />
    public async Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The concurrency token rejected this unit of work: another
            // writer committed a newer version of the aggregate in the
            // meantime. Translate the persistence detail into the
            // application-level conflict (HTTP 412) and keep the original
            // exception as the inner one for diagnostics.
            //
            // If the conflict did not involve a Resource aggregate the id
            // cannot be determined; the exception still carries the inner
            // failure for diagnosis.
            var resourceId = ex.Entries
                .Select(static entry => entry.Entity)
                .OfType<Resource>()
                .Select(static resource => resource.Id)
                .FirstOrDefault();

            throw new ResourceConcurrencyException(resourceId, ex);
        }
    }
}
