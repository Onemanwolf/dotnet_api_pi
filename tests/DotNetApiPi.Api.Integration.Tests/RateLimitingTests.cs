using System.Net;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Verifies the global rate limiter: it is disabled in Development (so local
/// tooling and tests are not throttled) and returns 429 once the
/// fixed-window budget is exhausted outside Development.
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
    public async Task Production_Returns429_WhenWindowIsExhausted()
    {
        var client = _productionFactory.CreateClient();

        HttpStatusCode lastStatus = HttpStatusCode.OK;

        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/api/resources");
            lastStatus = response.StatusCode;

            if (lastStatus == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    }
}
