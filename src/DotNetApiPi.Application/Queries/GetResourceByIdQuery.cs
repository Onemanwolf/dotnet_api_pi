using DotNetApiPi.Application.Common;

namespace DotNetApiPi.Application.Queries;

/// <summary>
/// Query to retrieve a single resource by its identity.
/// </summary>
/// <param name="Id">The identity of the resource to retrieve.</param>
public sealed record GetResourceByIdQuery(Guid Id) : IQuery
{
}
