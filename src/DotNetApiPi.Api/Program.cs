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
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
// event dispatcher, the provider-specific infrastructure initializer, and —
// for the MongoDB provider — the transactional outbox + Kafka relay,
// configured from the "Kafka" and "Outbox" configuration sections).
builder.Services.AddInfrastructure(storageOptions, builder.Configuration);

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

// CORS (audit finding F-14): an explicit origin allowlist read from the
// "Cors:AllowedOrigins" configuration section, e.g.
//   appsettings.json:  "Cors": { "AllowedOrigins": ["https://admin.example.com"] }
//   environment:       Cors__AllowedOrigins__0=https://admin.example.com
// An empty or absent list allows NO cross-origin requests (same-origin
// only) — the policy never falls back to a wildcard. Only browsers with an
// Origin that is in the list may make cross-origin requests; for those
// allowed origins all methods and headers are permitted.
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? [];

builder.Services.AddCors(options => options.AddPolicy(
    CorsPolicyNames.Default,
    policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyMethod()
        .AllowAnyHeader()));

// AUTHENTICATION POSTURE (explicit decision, audit finding F-14): this
// scaffold ships WITHOUT authentication by design — it is not an
// internet-facing product yet, and half-built auth middleware would create
// a false sense of security. The current abuse mitigations are per-caller
// rate limiting (below) and the unified RFC 7807 error contract (no stack
// traces or internal type names in responses). Add real authentication
// (e.g. OIDC bearer tokens) before exposing the API publicly.

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

// Observability (audit finding F-21): W3C trace-context propagation is
// active for every request (the AspNetCore instrumentation emits/propagates
// the traceparent header), and metrics/traces are exported over OTLP only
// when explicitly enabled — through the Otel:Enabled configuration key
// (Otel__Enabled=true) or the OTEL_ENABLED=true environment variable — so
// the scaffold runs with zero overhead and no failing exporter connections
// by default. The trace context coexists with the X-Correlation-Id
// middleware: the correlation id correlates operational log lines, the W3C
// trace context is for distributed tracing.
const string otelServiceName = "dotnet-api-pi";
const string otelServiceVersion = "1.0.0";
var otelEnabled = builder.Configuration["Otel:Enabled"] == "true"
    || Environment.GetEnvironmentVariable("OTEL_ENABLED") == "true";
var otelEndpoint = new Uri(
    builder.Configuration["Otel:Exporter:Otlp:Endpoint"]
    ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")
    ?? "http://localhost:4317");

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        otelServiceName,
        otelServiceVersion))
    .WithTracing(tracing =>
    {
        var pipeline = tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (otelEnabled)
        {
            pipeline.AddOtlpExporter(options => options.Endpoint = otelEndpoint);
        }
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
    /// The default CORS policy: an explicit origin allowlist configured
    /// through <c>Cors__AllowedOrigins</c> (empty = same-origin only, never
    /// a wildcard).
    /// </summary>
    public const string Default = "Default";
}
