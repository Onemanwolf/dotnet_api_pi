using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DotNetApiPi.Api.Middleware;
using DotNetApiPi.Api.RateLimiting;
using DotNetApiPi.Api.Results;
using DotNetApiPi.Application;
using DotNetApiPi.Infrastructure;
using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// The persistence provider is selected from the "Storage" configuration
// section. By default the API uses an embedded SQLite database so it can run
// without external services; set Storage__Provider=mongo (plus the Mongo
// connection string) to use a MongoDB server instead (see docker-compose.yml).
var storageOptions = builder.Configuration
    .GetSection(PersistenceOptions.SectionName)
    .Get<PersistenceOptions>()
    ?? new PersistenceOptions();

// Register the application layer (command and query handlers).
builder.Services.AddApplication();

// Register the infrastructure layer (persistence, repositories, the domain
// event dispatcher and the provider-specific infrastructure initializer).
builder.Services.AddInfrastructure(storageOptions);

// Configure MVC / controllers.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;

        // Raw input-formatter exception messages (which embed .NET type
        // names) are only surfaced in binding errors under Development.
        options.AllowInputFormatterExceptionMessages =
            builder.Environment.IsDevelopment();
    });

// A single error contract for MVC model-binding failures (invalid or
// missing body fields, unparseable JSON): the same RFC 7807 problem+json
// document shape as ExceptionHandlingMiddleware, instead of MVC's default
// application/json { errors, type, traceId } document.
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad request",
            // Must match the stable error-document URI scheme used by
            // ExceptionHandlingMiddleware.
            Type = "https://dotnet-api-pi.example/errors/bad-request",
            Detail = "One or more validation errors occurred."
        };

        var errors = new Dictionary<string, string[]>();

        foreach (var entry in context.ModelState)
        {
            var messages = entry.Value
                .Errors
                .Select(static error => error.ErrorMessage)
                .Where(static message => !string.IsNullOrWhiteSpace(message))
                .Select(static message => message!)
                .ToArray();

            if (messages.Length > 0)
            {
                errors[entry.Key] = messages;
            }
        }

        if (errors.Count > 0)
        {
            // RFC 7807 extension: per-property validation issues.
            problem.Extensions["errors"] = errors;
        }

        return new ProblemJsonResult(problem);
    };
});

// Add Open API support (Swagger) for convenience. It is only *surfaced* in
// Development (see below) so the API documentation is not exposed in
// production.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Health checks for container orchestrators and load balancers.
builder.Services.AddHealthChecks();

// A permissive CORS policy so browser clients can consume the API
// cross-origin. Tighten it (explicit origins) before exposing the API on the
// Internet.
builder.Services.AddCors(options => options.AddPolicy(
    CorsPolicyNames.Default,
    policy => policy
        .AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader()));

// A fixed-window rate limiter as a first line of defence against abuse,
// partitioned per caller: each client IP gets its own budget (100
// requests/minute), so a single noisy client cannot exhaust a global budget
// and lock out everyone else. It is only applied outside Development so
// local tooling and integration tests are not throttled. Rejected requests
// receive the API's standard problem+json contract with a Retry-After
// header.
var fixedWindowOptions = new FixedWindowRateLimiterOptions
{
    Window = TimeSpan.FromMinutes(1),
    PermitLimit = 100,
    // QueueLimit = 0: once a caller's window budget is exhausted the request
    // is rejected immediately with 429 (Retry-After below) instead of being
    // queued.
    QueueLimit = 0
};

// RFC 7807 serialization options for the rate-limit rejection handler
// (ProblemDetails carries explicit [JsonPropertyName] attributes; null
// members are omitted to match the exception middleware's output).
var problemJsonOptions = new JsonSerializerOptions
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // One fixed-window budget per caller, partitioned by the caller's remote
    // IP address; requests without a resolvable address share the
    // "anonymous" partition (see RateLimitKeys).
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter
        .Create<HttpContext, string>(
            httpContext => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitKeys.For(httpContext),
                _ => fixedWindowOptions));

    // Rejections follow the same RFC 7807 problem+json contract as
    // ExceptionHandlingMiddleware. Retry-After is the window length: the
    // exact fixed-window boundary is internal to the limiter, so reporting
    // the full window is a safe upper bound rather than telling the client
    // to retry too early.
    options.OnRejected = async (rejectionContext, cancellationToken) =>
    {
        var response = rejectionContext.HttpContext.Response;

        if (response.HasStarted)
        {
            return;
        }

        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/problem+json";
        response.Headers["Retry-After"] =
            ((int)fixedWindowOptions.Window.TotalSeconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Type = "https://dotnet-api-pi.example/errors/too-many-requests"
        };

        var json = JsonSerializer.Serialize(problem, problemJsonOptions);
        await response
            .WriteAsync(json, cancellationToken)
            .ConfigureAwait(false);
    };
});

// HTTPS redirection is enabled only when an HTTPS port is configured through
// ASPNETCORE_HTTPS_PORT — the case under `dotnet run` with the https launch
// profile, or in a container when both ASPNETCORE_HTTPS_PORT and an HTTPS
// entry in ASPNETCORE_URLS are set. Without a configured port, forcing the
// middleware would crash the host at startup.
var httpsPort = int.TryParse(
        builder.Configuration["ASPNETCORE_HTTPS_PORT"], out var configuredPort)
    ? (int?)configuredPort
    : null;

if (httpsPort.HasValue)
{
    builder.Services.AddHttpsRedirection(options =>
        options.HttpsPort = httpsPort.Value);
}

var app = builder.Build();

// Prepare the selected persistence provider (e.g. create the SQLite schema).
// The API depends only on the IInfrastructureInitializer abstraction, so no
// provider-specific (EF Core) types leak into the composition root.
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider
        .GetRequiredService<IInfrastructureInitializer>();
    await initializer.InitializeAsync();
}

// The exception handler runs outermost so it can catch exceptions thrown by
// everything downstream (including the correlation-id middleware).
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<CorrelationIdMiddleware>();

if (httpsPort.HasValue)
{
    app.UseHttpsRedirection();
}

app.UseCors(CorsPolicyNames.Default);

if (!app.Environment.IsDevelopment())
{
    app.UseRateLimiter();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();

// The liveness/readiness probe must stay reachable even while rate limiting
// is active (a throttled pod would otherwise be killed by the container
// orchestrator, and one noisy client could take down the whole deployment).
app.MapHealthChecks("/health").DisableRateLimiting();

app.Run();

/// <summary>
/// Explicitly exposes the entry point so <c>WebApplicationFactory&lt;Program&gt;</c>
/// can host the API in integration tests.
/// </summary>
public partial class Program;

/// <summary>
/// Well-known policy names used by the web host configuration.
/// </summary>
public static class CorsPolicyNames
{
    /// <summary>
    /// The default (permissive, development-oriented) CORS policy name.
    /// </summary>
    public const string Default = "Default";
}
