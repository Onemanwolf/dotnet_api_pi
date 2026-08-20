using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Handles the <see cref="ActivateResourceCommand"/> by loading the aggregate,
/// activating it and persisting the change.
/// </summary>
public sealed class ActivateResourceCommandHandler : ICommandHandler<ActivateResourceCommand, ResourceDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ActivateResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public ActivateResourceCommandHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly IResourceRepository _repository;

    /// <inheritdoc />
    public async Task<ResourceDto> HandleAsync(
        ActivateResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resource = await _repository.GetByIdAsync(
            command.Id,
            cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException(command.Id);

        resource.Activate();
        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ResourceMapper.ToDto(resource);
    }
}
