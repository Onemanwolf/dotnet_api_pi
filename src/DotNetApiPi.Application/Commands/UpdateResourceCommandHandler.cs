using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Handles the <see cref="UpdateResourceCommand"/> by loading the aggregate,
/// applying the requested changes through its behaviour and persisting it.
/// </summary>
public sealed class UpdateResourceCommandHandler : ICommandHandler<UpdateResourceCommand, ResourceDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public UpdateResourceCommandHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly IResourceRepository _repository;

    /// <inheritdoc />
    public async Task<ResourceDto> HandleAsync(
        UpdateResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var resource = await _repository.GetByIdAsync(
            command.Id,
            cancellationToken).ConfigureAwait(false);

        if (resource is null)
        {
            throw new ResourceNotFoundException(command.Id);
        }

        resource.Rename(new ResourceName(command.Name));
        resource.SetDescription(command.Description);

        if (command.Tags is not null)
        {
            resource.SetTags(
                command.Tags.Select(static tag => new ResourceTag(tag)));
        }

        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ResourceMapper.ToDto(resource);
    }
}
