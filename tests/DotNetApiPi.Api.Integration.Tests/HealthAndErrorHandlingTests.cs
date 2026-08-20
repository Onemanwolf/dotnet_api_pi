using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
}
