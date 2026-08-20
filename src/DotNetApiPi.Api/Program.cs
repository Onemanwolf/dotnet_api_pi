using System.Threading.RateLimiting;
using DotNetApiPi.Api.Middleware;
using DotNetApiPi.Application;
using DotNetApiPi.Infrastructure;
using DotNetApiPi.Infrastructure.Persistence;

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

// A global fixed-window rate limiter as a first line of defence against
// abuse. It is only applied outside Development so local tooling and
// integration tests are not throttled.
var fixedWindowOptions = new FixedWindowRateLimiterOptions
{
    Window = TimeSpan.FromMinutes(1),
    PermitLimit = 100,
    QueueLimit = 0
};
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // A single (global) fixed-window limiter for every request; the
    // partition key is a constant.
    options.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter
        .Create<HttpContext, string>(
            _ => RateLimitPartition.GetFixedWindowLimiter(
                "global",
                _ => fixedWindowOptions));
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
app.MapHealthChecks("/health");

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
