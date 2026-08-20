using DotNetApiPi.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Verifies the CORS origin allowlist (audit finding F-14): a request whose
/// Origin header is in <c>Cors__AllowedOrigins</c> receives
/// <c>Access-Control-Allow-Origin</c>; an unlisted Origin receives no
/// cross-origin grant; a request without an Origin header is untouched
/// (which is exactly what the rest of the integration suite does).
/// <para>
/// The CORS policy never falls back to a wildcard: with the default (empty)
/// allowlist no cross-origin request is granted.
/// </para>
/// </summary>
public sealed class CorsTests
{
    private const string AllowedOrigin = "https://admin.example.com";
    private const string UnlistedOrigin = "https://evil.example.com";

    [Fact]
    public async Task ListedOrigin_Get_RespondsWithAccessControlAllowOrigin()
    {
        await using var factory = new CorsApiFactory(AllowedOrigin);
        var client = factory.CreateClient();

        using var response = await SendWithOriginAsync(
            client,
            HttpMethod.Get,
            "/health",
            AllowedOrigin);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(
            AllowedOrigin,
            GetHeaderValue(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task UnlistedOrigin_Get_RespondsWithoutAccessControlAllowOrigin()
    {
        await using var factory = new CorsApiFactory(AllowedOrigin);
        var client = factory.CreateClient();

        using var response = await SendWithOriginAsync(
            client,
            HttpMethod.Get,
            "/health",
            UnlistedOrigin);

        Assert.Equal(200, (int)response.StatusCode);

        // No Access-Control-Allow-Origin header: the browser will block the
        // cross-origin read. (CORS does not fail the request itself.)
        Assert.DoesNotContain(
            response.Headers,
            header => string.Equals(
                header.Key,
                "Access-Control-Allow-Origin",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoOriginHeader_Get_RespondsWithoutAccessControlAllowOrigin()
    {
        // Existing integration tests send no Origin header at all; this
        // documents (and pins) that they are unaffected by the allowlist.
        await using var factory = new CorsApiFactory(AllowedOrigin);
        var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Null(GetHeaderValue(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ListedOrigin_Preflight_AllowsRequestedMethod()
    {
        await using var factory = new CorsApiFactory(AllowedOrigin);
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(
            HttpMethod.Options,
            "/api/resources");
        request.Headers.TryAddWithoutValidation("Origin", AllowedOrigin);
        request.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Method",
            "POST");

        using var response = await client.SendAsync(request);

        // The preflight is allowed for the listed origin…
        Assert.Equal(204, (int)response.StatusCode);
        Assert.Equal(
            AllowedOrigin,
            GetHeaderValue(response, "Access-Control-Allow-Origin"));

        // …and the requested method is among the granted methods.
        var allowMethods = GetHeaderValue(
                response,
                "Access-Control-Allow-Methods")
            ?.Split(',');

        Assert.NotNull(allowMethods);
        Assert.Contains("POST", allowMethods);
    }

    [Fact]
    public async Task EmptyAllowlist_NeverGrantsCrossOriginAccess()
    {
        // The default configuration (no Cors__AllowedOrigins entries) must
        // not fall back to a wildcard.
        await using var factory = new CorsApiFactory();
        var client = factory.CreateClient();

        using var response = await SendWithOriginAsync(
            client,
            HttpMethod.Get,
            "/health",
            AllowedOrigin);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Null(GetHeaderValue(response, "Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// Gets the first value of a response header, or null when the header is
    /// absent. (System.Net.Http's <see cref="HttpResponseHeaders"/> has no
    /// strongly-typed CORS properties, so the header is read by name.)
    /// </summary>
    private static string? GetHeaderValue(
        HttpResponseMessage response,
        string name)
    {
        // HttpHeaders.GetValues throws when the header is absent, so probe
        // with Contains first.
        return response.Headers.Contains(name)
            ? response.Headers.GetValues(name).First()
            : null;
    }

    /// <summary>
    /// Sends a GET/POST/etc. request with an explicit Origin header.
    /// </summary>
    private static async Task<HttpResponseMessage> SendWithOriginAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string origin)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    /// <summary>
    /// A test host that mirrors <see cref="ApiFactory"/> (same ConfigureWebHost
    /// pattern and private SQLite file) and additionally configures the
    /// <c>Cors:AllowedOrigins</c> allowlist. A separate factory is used
    /// because the CORS tests must not modify the shared <c>ApiFactory</c>.
    /// </summary>
    private sealed class CorsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string[] _allowedOrigins;

        private readonly string _databaseFile;

        public CorsApiFactory(params string[] allowedOrigins)
        {
            _allowedOrigins = allowedOrigins ?? [];
            _databaseFile = Path.Combine(
                Path.GetTempPath(),
                $"dotnet-api-pi-cors-tests-{Guid.NewGuid():N}.db");
        }

        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.UseEnvironment("Development");
            builder.UseSetting(
                $"{PersistenceOptions.SectionName}:SqliteConnectionString",
                $"Data Source={_databaseFile}");
            builder.UseSetting(
                $"{PersistenceOptions.SectionName}:Provider",
                StorageProvider.Sqlite.ToString());

            for (var i = 0; i < _allowedOrigins.Length; i++)
            {
                builder.UseSetting(
                    $"Cors:AllowedOrigins:{i}",
                    _allowedOrigins[i]);
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing && File.Exists(_databaseFile))
            {
                try
                {
                    File.Delete(_databaseFile);
                }
                catch (IOException)
                {
                    // Best effort: the file is in a temp directory and will
                    // be cleaned up by the OS.
                }
            }
        }
    }
}
