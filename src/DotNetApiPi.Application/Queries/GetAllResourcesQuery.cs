using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Queries;

/// <summary>
/// Query to retrieve all resources.
/// </summary>
public sealed record GetAllResourcesQuery() : IQuery
{
}

/// <summary>
/// Handles the <see cref="GetAllResourcesQuery"/> by returning all resources
/// as DTOs.
/// </summary>
public sealed class GetAllResourcesQueryHandler : IQueryHandler<GetAllResourcesQuery, IReadOnlyList<ResourceDto>>
{
    private readonly IResourceRepository _repository;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetAllResourcesQueryHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public GetAllResourcesQueryHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResourceDto>> HandleAsync(
        GetAllResourcesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resources = await _repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return ResourceMapper.ToDto(resources);
    }
}
