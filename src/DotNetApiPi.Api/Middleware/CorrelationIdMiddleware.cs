using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Api.Middleware;

/// <summary>
/// Lightweight request-logging middleware that assigns (or echoes) a
/// correlation identifier per request, propagates it into the logging scope so
/// every log line written while handling the request carries it, adds it to
/// the response header, stores it in <see cref="HttpContext.Items"/>, and
/// writes a single structured log entry describing the completed request
/// (method, path, status, duration).
/// <para>
/// The <see cref="HttpContext.Items"/> copy exists so the exception
/// middleware can re-attach the identifier after
/// <see cref="HttpResponse.Clear"/>, which wipes every previously set
/// response header.
/// </para>
/// </summary>
public sealed class CorrelationIdMiddleware
{
    /// <summary>
    /// The HTTP header used to carry the correlation identifier.
    /// </summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key under which the correlation
    /// identifier is stored for later re-attachment on error responses.
    /// </summary>
    public const string ContextItemKey = "DotNetApiPi.CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CorrelationIdMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger used for the structured request entry.</param>
    public CorrelationIdMiddleware(
        RequestDelegate next,
        ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Invokes the asynchronous middleware handler.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        // Echo a client-supplied correlation id when present (so a caller can
        // trace a request across services); otherwise generate one.
        var correlationId =
            context.Request.Headers[HeaderName].FirstOrDefault() ?? Guid.NewGuid().ToString("N");

        // Store the identifier for the exception middleware (which writes
        // the response after Response.Clear() wiped any earlier header) and
        // on the response for the normal path.
        context.Items[ContextItemKey] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object?>
               {
                   [TelemetryMetadataKeys.CorrelationId] = correlationId
               }))
        {
            var startedAt = DateTime.UtcNow;

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            finally
            {
                // Log even when the request threw — the outer
                // ExceptionHandlingMiddleware still converts the exception
                // into a response, and we want the request line either way.
                _logger.LogInformation(
                    "HTTP {Method} {Path} → {StatusCode} ({ElapsedMs} ms)",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    (DateTime.UtcNow - startedAt).TotalMilliseconds);
            }
        }
    }
}

/// <summary>
/// Well-known structured-log property names.
/// </summary>
public static class TelemetryMetadataKeys
{
    /// <summary>
    /// The key under which the correlation identifier is scoped.
    /// </summary>
    public const string CorrelationId = "CorrelationId";
}
