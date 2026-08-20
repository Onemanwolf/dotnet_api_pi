using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Handles the <see cref="ArchiveResourceCommand"/> by loading the aggregate,
/// archiving it and persisting the change.
/// </summary>
public sealed class ArchiveResourceCommandHandler : ICommandHandler<ArchiveResourceCommand, ResourceDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public ArchiveResourceCommandHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly IResourceRepository _repository;

    /// <inheritdoc />
    public async Task<ResourceDto> HandleAsync(
        ArchiveResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resource = await _repository.GetByIdAsync(
            command.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException(command.Id);

        // Optimistic concurrency (application layer): reject a request that
        // is based on a stale version before any mutation is applied. Maps
        // to HTTP 412 via the exception-mapping middleware.
        ConcurrencyPreconditions.EnsureMatches(resource, command.ExpectedVersion);

        resource.Archive();
        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ResourceMapper.ToDto(resource);
    }
}
