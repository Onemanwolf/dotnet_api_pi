using DotNetApiPi.Application.Common;
using DotNetApiPi.Application.Dtos;
using DotNetApiPi.Application.Mapping;
using DotNetApiPi.Domain.Exceptions;
using DotNetApiPi.Domain.Repositories;

namespace DotNetApiPi.Application.Queries;

/// <summary>
/// Query to retrieve one page of resources.
/// </summary>
/// <param name="Page">The 1-based page number (defaults to the first page).</param>
/// <param name="PageSize">The maximum number of items per page.</param>
public sealed record GetAllResourcesQuery(
    int Page = 1,
    int PageSize = 20) : IQuery
{
    /// <summary>
    /// The default page number.
    /// </summary>
    public const int DefaultPage = 1;

    /// <summary>
    /// The default number of items per page.
    /// </summary>
    public const int DefaultPageSize = 20;

    /// <summary>
    /// The largest page size the API accepts (the list endpoint is bounded:
    /// clients cannot pull the whole store in a single request).
    /// </summary>
    public const int MaxPageSize = 100;
}

/// <summary>
/// Handles the <see cref="GetAllResourcesQuery"/> by returning one page of
/// resources as DTOs together with the total count.
/// </summary>
public sealed class GetAllResourcesQueryHandler : IQueryHandler<GetAllResourcesQuery, PagedResult<ResourceDto>>
{
    private readonly IResourceRepository _repository;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="GetAllResourcesQueryHandler"/> class.
    /// </summary>
    /// <param name="repository">The resource repository.</param>
    public GetAllResourcesQueryHandler(IResourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <inheritdoc />
    public async Task<PagedResult<ResourceDto>> HandleAsync(
        GetAllResourcesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Defensive validation: the presentation layer already rejects
        // out-of-range paging parameters with a 400, but the handler must
        // not assume its caller is the HTTP boundary.
        if (query.Page < 1 || query.PageSize < 1 || query.PageSize > GetAllResourcesQuery.MaxPageSize)
        {
            throw new DomainInputException(
                $"Paging parameters are out of range: page must be >= {GetAllResourcesQuery.DefaultPage} and pageSize between 1 and {GetAllResourcesQuery.MaxPageSize}.");
        }

        var (items, totalCount) = await _repository
            .GetPageAsync(query.Page, query.PageSize, cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<ResourceDto>(
            ResourceMapper.ToDto(items),
            totalCount,
            query.Page,
            query.PageSize);
    }
}
