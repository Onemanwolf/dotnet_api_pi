using System.Net;
using System.Text.Json;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Verifies the per-caller rate limiter: it is disabled in Development (so
/// local tooling and tests are not throttled), returns 429 problem+json with
/// a Retry-After header once a caller's fixed-window budget is exhausted
/// outside Development, and never throttles the health probe.
/// </summary>
public sealed class RateLimitingTests : IAsyncLifetime
{
    private ApiFactory _developmentFactory = null!;
    private ApiFactory _productionFactory = null!;

    /// <summary>
    /// Creates dedicated in-process hosts for both environments.
    /// </summary>
    public Task InitializeAsync()
    {
        _developmentFactory = new ApiFactory();
        _productionFactory = new ApiFactory("Production");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes both in-process hosts and their database files.
    /// </summary>
    public Task DisposeAsync()
    {
        _developmentFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _productionFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Development_DoesNotThrottle()
    {
        var client = _developmentFactory.CreateClient();

        // More requests than the production window allows; in Development the
        // limiter is bypassed entirely, so every request succeeds.
        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/api/resources");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Production_Returns429ProblemJson_WhenWindowIsExhausted()
    {
        var client = _productionFactory.CreateClient();

        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/api/resources");

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                continue;
            }

            // Capture everything before the response (and its content
            // stream) is disposed at the end of this iteration.
            Assert.Equal(
                "application/problem+json",
                response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                TimeSpan.FromSeconds(60),
                response.Headers.RetryAfter?.Delta);

            var problem = JsonSerializer.Deserialize<JsonElement>(
                await response.Content.ReadAsStringAsync());

            Assert.Equal(429, problem.GetProperty("status").GetInt32());
            Assert.Equal(
                "Too many requests",
                problem.GetProperty("title").GetString());
            Assert.Equal(
                "https://dotnet-api-pi.example/errors/too-many-requests",
                problem.GetProperty("type").GetString());
            return;
        }

        Assert.Fail("The rate-limit window was never exhausted.");
    }

    [Fact]
    public async Task Production_HealthEndpoint_IsExemptFromRateLimiting()
    {
        var client = _productionFactory.CreateClient();

        // Exhaust the caller's window on the API surface…
        var sawRejection = false;

        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/api/resources");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                sawRejection = true;
                break;
            }
        }

        Assert.True(sawRejection, "expected the window to be exhausted first");

        // …while the same caller's health probe still succeeds: the probe is
        // exempt from rate limiting so container orchestrators keep the pod
        // alive even while traffic is being throttled.
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
