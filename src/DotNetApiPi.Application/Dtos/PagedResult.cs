namespace DotNetApiPi.Application.Dtos;

/// <summary>
/// A page of read-model items together with the paging metadata the
/// presentation layer needs to expose to clients (total count / total
/// pages).
/// </summary>
/// <typeparam name="TItem">The type of the read-model items.</typeparam>
/// <param name="Items">The items on the requested page.</param>
/// <param name="TotalCount">The total number of items in the store (before paging).</param>
/// <param name="Page">The 1-based page number this page was read from.</param>
/// <param name="PageSize">The maximum number of items per page.</param>
public sealed record PagedResult<TItem>(
    IReadOnlyList<TItem> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    /// <summary>
    /// Gets the total number of pages for the current page size.
    /// </summary>
    public int TotalPages
        => PageSize <= 0
            ? 0
            : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
