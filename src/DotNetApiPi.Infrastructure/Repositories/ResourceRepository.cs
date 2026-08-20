using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DotNetApiPi.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of the <see cref="IResourceRepository"/> interface.
/// Persists <see cref="Resource"/> aggregates using the application's
/// <see cref="ApiDbContext"/>.
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
    public async Task<IReadOnlyList<Resource>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return await _context.Resources
            .OrderBy(resource => resource.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
