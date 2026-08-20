using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Entities;
using DotNetApiPi.Domain.Repositories;
using DotNetApiPi.Domain.ValueObjects;

namespace DotNetApiPi.Application.Commands;

/// <summary>
/// Handles the <see cref="CreateResourceCommand"/> by creating a new
/// <see cref="Resource"/> aggregate and persisting it. Domain events raised
/// by the aggregate are dispatched when the change is saved.
/// </summary>
public sealed class CreateResourceCommandHandler : ICommandHandler<CreateResourceCommand, ResourceDto>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateResourceCommandHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public CreateResourceCommandHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    private readonly IResourceRepository _repository;

    /// <inheritdoc />
    public async Task<ResourceDto> HandleAsync(
        CreateResourceCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tags = command.Tags?.Select(static tag => new ResourceTag(tag)).ToArray() ?? [];

        var resource = Resource.Create(
            new ResourceName(command.Name),
            command.Description,
            tags);

        await _repository.AddAsync(resource, cancellationToken).ConfigureAwait(false);
        await _repository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ResourceMapper.ToDto(resource);
    }
}
