using System.Net;
using DotNetApiPi.Api.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Unit tests for the rate-limiter partition key selector. (The in-process
/// test server does not expose per-client remote IP addresses, so partition
/// isolation itself is exercised by the limiter's per-host behaviour in
/// <see cref="RateLimitingTests"/>.)
/// </summary>
public sealed class RateLimitKeysTests
{
    [Fact]
    public void For_UsesRemoteIpAddress_WhenAvailable()
    {
        var context = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.7") }
        };

        Assert.Equal("203.0.113.7", RateLimitKeys.For(context));
    }

    [Fact]
    public void For_ReturnsAnonymousKey_WhenRemoteIpAddressIsMissing()
    {
        // DefaultHttpContext has no connection address by default.
        var context = new DefaultHttpContext();

        Assert.Equal(RateLimitKeys.AnonymousKey, RateLimitKeys.For(context));
    }

    [Fact]
    public void For_DistinctCallers_YieldDistinctKeys()
    {
        var first = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.1") }
        };
        var second = new DefaultHttpContext
        {
            Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.2") }
        };

        Assert.NotEqual(RateLimitKeys.For(first), RateLimitKeys.For(second));
    }
}
