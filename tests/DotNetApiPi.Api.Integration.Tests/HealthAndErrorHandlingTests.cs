using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetApiPi.Api.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// A controller that unconditionally throws, used to verify the exception
/// middleware's 500 fallback (unexpected server faults produce a well-formed
/// problem+json response instead of an opaque host-level error).
/// </summary>
[ApiController]
[Route("api/boom")]
public sealed class BoomController : ControllerBase
{
    /// <summary>
    /// Always throws an unhandled exception.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Always thrown, to simulate a server fault.
    /// </exception>
    [HttpGet]
    public IActionResult Boom()
    {
        throw new InvalidOperationException("Simulated server fault.");
    }
}

/// <summary>
/// An <see cref="ApiFactory"/> that additionally registers the test-only
/// <see cref="BoomController"/> assembly part.
/// </summary>
public sealed class BoomApiFactory : ApiFactory
{
    /// <inheritdoc />
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
            services.AddControllers()
                .AddApplicationPart(typeof(BoomController).Assembly));
    }}

/// <summary>
/// Tests for the health endpoint and the exception-mapping fallback.
/// </summary>
public sealed class HealthAndErrorHandlingTests : IAsyncLifetime
{
    private BoomApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// Creates a dedicated in-process host per test.
    /// </summary>
    public Task InitializeAsync()
    {
        _factory = new BoomApiFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes the in-process host and its database file.
    /// </summary>
    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Health_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnhandledException_Returns500ProblemJson()
    {
        var response = await _client.GetAsync("/api/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            "Internal server error",
            problem.GetProperty("title").GetString());
        Assert.False(
            string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        Assert.False(
            string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task NotFound_Response_CarriesCorrelationId()
    {
        const string correlationId = "test-correlation-not-found";

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/resources/{Guid.NewGuid():N}");
        request.Headers.TryAddWithoutValidation(
            CorrelationIdMiddleware.HeaderName,
            correlationId);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The correlation id survives the exception path (where
        // Response.Clear() wipes previously set headers)…
        var headerValues = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName);
        Assert.Contains(correlationId, headerValues ?? Array.Empty<string>());

        // …and is embedded in the problem+json body for log cross-reference.
        var problem = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            correlationId,
            problem.GetProperty("correlationId").GetString());
        Assert.Equal("Not found", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Conflict_Response_CarriesCorrelationId()
    {
        const string correlationId = "test-correlation-conflict";

        var id = await CreateResourceAsync(_client);

        // First activation succeeds under the fresh ETag; a second is a
        // state conflict (409).
        var first = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/activate");
        first.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using (var firstResponse = await _client.SendAsync(first))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        }

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/activate");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        request.Headers.TryAddWithoutValidation(
            CorrelationIdMiddleware.HeaderName,
            correlationId);

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var headerValues = response.Headers.GetValues(CorrelationIdMiddleware.HeaderName);
        Assert.Contains(correlationId, headerValues ?? Array.Empty<string>());

        var problem = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(
            correlationId,
            problem.GetProperty("correlationId").GetString());
        Assert.Equal("Conflict", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task InvalidModelState_Returns400ProblemJson()
    {
        // A JSON number where a string is expected: the input formatter
        // rejects the body and MVC must report it through the shared
        // problem+json contract — not the default application/json
        // validation document.
        using var content = new StringContent(
            "{\"name\": 12345}",
            System.Text.Encoding.UTF8,
            "application/json");

        using var response = await _client.PostAsync("/api/resources", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var problem = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("Bad request", problem.GetProperty("title").GetString());
        Assert.Equal(
            "https://dotnet-api-pi.example/errors/bad-request",
            problem.GetProperty("type").GetString());

        // Validation issues are exposed under the RFC 7807 `errors`
        // extension.
        var errors = problem.GetProperty("errors");
        var errorKeys = errors.EnumerateObject().Select(static p => p.Name).ToList();
        Assert.NotEmpty(errorKeys);
    }

    /// <summary>
    /// Creates a resource and returns its identifier.
    /// </summary>
    private static async Task<string> CreateResourceAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/resources",
            new { name = "Correlation test resource" });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body!.GetProperty("id").GetString()!;
    }
}
