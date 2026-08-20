namespace DotNetApiPi.Application.Common;

/// <summary>
/// Defines a handler that executes a query and returns a result.
/// </summary>
/// <typeparam name="TQuery">The type of the query being handled.</typeparam>
/// <typeparam name="TResult">The type of the result produced by the handler.</typeparam>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery
{
    /// <summary>
    /// Asynchronously handles the given query and returns a result.
    /// </summary>
    /// <param name="query">The query to handle.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation, containing the result.</returns>
    Task<TResult> HandleAsync(
        TQuery query,
        CancellationToken cancellationToken = default);
}
