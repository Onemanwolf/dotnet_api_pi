using Microsoft.AspNetCore.Http;

namespace DotNetApiPi.Api.RateLimiting;

/// <summary>
/// Computes the rate-limiting partition key for a request: the caller's
/// remote IP address, so every caller gets an independent fixed-window
/// budget instead of sharing one global bucket.
/// </summary>
public static class RateLimitKeys
{
    /// <summary>
    /// The key used when no remote IP address is available (for example,
    /// some in-process test hosts). Such requests share a single partition.
    /// </summary>
    public const string AnonymousKey = "anonymous";

    /// <summary>
    /// Gets the rate-limiting partition key for <paramref name="context"/>.
    /// </summary>
    /// <param name="context">The HTTP context to compute the key for.</param>
    /// <returns>
    /// The remote IP address as a string, or
    /// <see cref="AnonymousKey"/> when no address is available.
    /// </returns>
    public static string For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Connection.RemoteIpAddress?.ToString() ?? AnonymousKey;
    }
}
