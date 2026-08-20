using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;

namespace DotNetApiPi.Api.Results;

/// <summary>
/// Writes the API's RFC 7807 problem+json contract directly to the response.
/// <para>
/// Used for MVC model-binding failures so the error contract does not depend
/// on output-formatter content-type negotiation: a
/// <c>[Produces("application/json")]</c> attribute on the controller would
/// otherwise force the default <c>application/json</c> content type on the
/// problem document. Writing the response directly mirrors what
/// <c>ExceptionHandlingMiddleware</c> does for thrown exceptions, so both
/// error paths emit byte-for-byte the same document shape.
/// </para>
/// </summary>
public sealed class ProblemJsonResult : IActionResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ProblemDetails _problem;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProblemJsonResult"/> class.
    /// </summary>
    /// <param name="problem">The problem details to write.</param>
    public ProblemJsonResult(ProblemDetails problem)
    {
        _problem = problem ?? throw new ArgumentNullException(nameof(problem));
    }

    /// <inheritdoc />
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;

        response.StatusCode = _problem.Status ?? StatusCodes.Status400BadRequest;
        response.ContentType = "application/problem+json";

        var json = JsonSerializer.Serialize(_problem, JsonOptions);
        await response
            .WriteAsync(json, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);
    }
}
