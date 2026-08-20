using System.Text.Json;
using System.Text.Json.Serialization;
using DotNetApiPi.Application.Common.Exceptions;
using DotNetApiPi.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace DotNetApiPi.Api.Middleware;

/// <summary>
/// A middleware that translates known domain and application exceptions into
/// well-formed RFC 7807 problem+json responses.
/// <para>
/// Mapping:
/// <list type="bullet">
/// <item>
/// <see cref="DomainInputException"/> → 400 Bad Request (the client sent
/// invalid input).
/// </item>
/// <item>
/// <see cref="ResourceNotFoundException"/> → 404 Not Found.
/// </item>
/// <item>
/// <see cref="ResourceConcurrencyException"/> → 412 Precondition Failed (an
/// optimistic-concurrency conflict: the client's <c>If-Match</c> version no
/// longer matches the stored aggregate).
/// </item>
/// <item>
/// <see cref="DomainException"/> → 409 Conflict (a state-transition conflict,
/// e.g. activating an already archived resource).
/// </item>
/// <item>
/// Anything else → 500 Internal Server Error, logged with its stack trace.
/// </item>
/// </list>
/// </para>
/// Every handled exception is logged so failures are not silent in production.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExceptionHandlingMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="logger">The logger used to record every mapped exception.</param>
    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger)
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
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // A catch-all so that unexpected exceptions produce a well-formed
            // 500 problem+json response (and a log entry) instead of an empty
            // host-level 500 with the stack lost in production.
            await HandleExceptionAsync(context, exception)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Maps an exception to an HTTP status, logs it and writes the problem
    /// details response.
    /// </summary>
    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var statusCode = exception switch
        {
            DomainInputException => StatusCodes.Status400BadRequest,
            ResourceNotFoundException => StatusCodes.Status404NotFound,
            ResourceConcurrencyException => StatusCodes.Status412PreconditionFailed,
            DomainException => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError
        };

        LogException(context, statusCode, exception);

        await WriteProblemAsync(
            context,
            statusCode,
            exception.Message,
            context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Logs the exception at the severity appropriate to its mapped status.
    /// Client-caused 4xx errors are warnings (they are expected in normal
    /// operation); anything mapped to 5xx is a server fault and is logged
    /// with its full stack trace.
    /// </summary>
    private void LogException(HttpContext context, int statusCode, Exception exception)
    {
        if (statusCode >= 500)
        {
            _logger.LogError(
                exception,
                "Unhandled exception while processing {Method} {Path}.",
                context.Request.Method,
                context.Request.Path);
        }
        else
        {
            _logger.LogWarning(
                "Exception mapped to {StatusCode}: {ExceptionType}: {Message}",
                statusCode,
                exception.GetType().Name,
                exception.Message);
        }
    }

    /// <summary>
    /// Writes a problem details response to the response stream.
    /// </summary>
    private static async Task WriteProblemAsync(
        HttpContext context,
        int statusCode,
        string detail,
        CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = ProblemTitles.Get(statusCode),
            Detail = detail,
            // RFC 7807: `type` must be a URI reference. A stable, versioned
            // error-document URI is used instead of a playful third-party one.
            Type = ProblemTypes.Get(statusCode)
        };

        // Response.Clear() wiped the headers set earlier — including the
        // X-Correlation-Id that CorrelationIdMiddleware assigned before the
        // exception was thrown. Re-attach it (response header and
        // problem+json extension) so error responses stay correlatable with
        // the same log line as successful responses.
        if (context.Items.TryGetValue(
                CorrelationIdMiddleware.ContextItemKey,
                out object? correlationId))
        {
            // CorrelationIdMiddleware always stores a non-null string here.
            var correlationValue = (string)correlationId!;
            context.Response.Headers[CorrelationIdMiddleware.HeaderName] =
                correlationValue;
            problem.Extensions["correlationId"] = correlationValue;
        }

        var json = JsonSerializer.Serialize(problem, JsonOptions);
        await context.Response
            .WriteAsync(json, cancellationToken)
            .ConfigureAwait(false);
    }

    private static class ProblemTitles
    {
        private static readonly Dictionary<int, string> Titles = new()
        {
            [StatusCodes.Status400BadRequest] = "Bad request",
            [StatusCodes.Status404NotFound] = "Not found",
            [StatusCodes.Status409Conflict] = "Conflict",
            [StatusCodes.Status412PreconditionFailed] = "Precondition failed",
            [StatusCodes.Status500InternalServerError] = "Internal server error"
        };

        public static string Get(int statusCode)
        {
            return Titles.TryGetValue(statusCode, out var title) ? title : "Error";
        }
    }

    private static class ProblemTypes
    {
        /// <summary>
        /// Stable base URI for this API's error documents. Each status maps to
        /// a unique, resolvable URI as recommended by RFC 7807.
        /// </summary>
        private const string BaseUri = "https://dotnet-api-pi.example/errors";

        public static string Get(int statusCode)
        {
            return statusCode switch
            {
                StatusCodes.Status400BadRequest => $"{BaseUri}/bad-request",
                StatusCodes.Status404NotFound => $"{BaseUri}/not-found",
                StatusCodes.Status409Conflict => $"{BaseUri}/conflict",
                StatusCodes.Status412PreconditionFailed => $"{BaseUri}/precondition-failed",
                StatusCodes.Status500InternalServerError => $"{BaseUri}/internal-server-error",
                _ => "about:blank"
            };
        }
    }
}
