using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Application.Queries;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Queries;

/// <summary>
/// Handles the <see cref="GetResourceByIdQuery"/> by returning the matching
/// resource as a DTO.
/// </summary>
public sealed class GetResourceByIdQueryHandler : IQueryHandler<GetResourceByIdQuery, ResourceDto>
{
    private readonly IResourceRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetResourceByIdQueryHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public GetResourceByIdQueryHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc />
    public async Task<ResourceDto> HandleAsync(
        GetResourceByIdQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resource = await _repository.GetByIdAsync(
            query.Id,
            cancellationToken).ConfigureAwait(false);

        if (resource is null)
        {
            throw new ResourceNotFoundException(query.Id);
        }

        return ResourceMapper.ToDto(resource);
    }
}
