using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Exercises the ETag / If-Match optimistic-concurrency contract over the
/// full pipeline: <c>ETag: "&lt;version&gt;"</c> on single-resource reads,
/// mandatory <c>If-Match</c> on every mutating endpoint (428 when missing,
/// 412 when stale) and the <c>version</c> field on the wire.
/// </summary>
public sealed class EtagConcurrencyTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// Creates a resource through the API and returns its id and version.
    /// </summary>
    private async Task<Guid> CreateResourceAsync(string name = "ETag Resource")
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new { name, description = (string?)null, tags = (string[]?)null });

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(0, json.GetProperty("version").GetInt32());
        return json.GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage WithIfMatch(
        HttpRequestMessage request,
        int version)
    {
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
        return request;
    }

    [Fact]
    public async Task Get_ReturnsQuotedVersionEtag_MatchingTheBody()
    {
        var id = await CreateResourceAsync();

        using var response = await _client.GetAsync($"/api/resources/{id}");

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("\"0\"", response.Headers.ETag?.Tag);
        Assert.Equal(0, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Update_WithMatchingIfMatch_Succeeds_AndBumpsTheVersion()
    {
        var id = await CreateResourceAsync();

        using var response = await _client.SendAsync(
            WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
                {
                    Content = JsonContent.Create(
                        new { name = "Renamed Under ETag", description = (string?)null, tags = (string[]?)null })
                },
                version: 0));

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal(1, json.GetProperty("version").GetInt32());

        // The next read carries the bumped ETag.
        using var etagResponse = await _client.GetAsync($"/api/resources/{id}");
        Assert.Equal("\"1\"", etagResponse.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Update_WithStaleIfMatch_Returns412_AndDoesNotOverwrite()
    {
        var id = await CreateResourceAsync();
        await UpdateVersionAsync(id, "First Writer", expectedVersion: 0);

        using var response = await _client.SendAsync(
            WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
                {
                    Content = JsonContent.Create(
                        new { name = "Second Writer", description = (string?)null, tags = (string[]?)null })
                },
                version: 0)); // stale: the resource is now at version 1

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Precondition failed", "/precondition-failed");

        // The stale write did not clobber the first writer's change.
        using var getResponse = await _client.GetAsync($"/api/resources/{id}");
        getResponse.EnsureSuccessStatusCode();
        var json = (await getResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("First Writer", json.GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Update_WithoutIfMatch_Returns428()
    {
        var id = await CreateResourceAsync();

        using var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
            {
                Content = JsonContent.Create(
                    new { name = "No Precondition", description = (string?)null, tags = (string[]?)null })
            });

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Precondition required", "/precondition-required");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("\"-1\"")]
    [InlineData("\"1.5\"")]
    public async Task Update_WithMalformedIfMatch_Returns400(string ifMatch)
    {
        var id = await CreateResourceAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
        {
            Content = JsonContent.Create(
                new { name = "Malformed Header", description = (string?)null, tags = (string[]?)null })
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Bad request", "/bad-request");
    }

    [Fact]
    public async Task Update_WithWildcardIfMatch_ProceedsWithoutVersionCheck()
    {
        var id = await CreateResourceAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
        {
            Content = JsonContent.Create(
                new { name = "Wildcard Update", description = (string?)null, tags = (string[]?)null })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        using var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal("Wildcard Update", json.GetProperty("name").GetString());
        Assert.Equal(1, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Activate_WithoutIfMatch_Returns428()
    {
        var id = await CreateResourceAsync();

        using var response = await _client.PostAsync(
            $"/api/resources/{id}/activate",
            content: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Precondition required", "/precondition-required");
    }

    [Fact]
    public async Task Activate_WithStaleIfMatch_Returns412()
    {
        var id = await CreateResourceAsync();
        await UpdateVersionAsync(id, "Bumped", expectedVersion: 0);

        using var response = await _client.SendAsync(
            WithIfMatch(
                new HttpRequestMessage(HttpMethod.Post, $"/api/resources/{id}/activate"),
                version: 0)); // stale after the update

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Precondition failed", "/precondition-failed");
    }

    [Fact]
    public async Task Delete_WithStaleIfMatch_Returns412_AndKeepsTheResource()
    {
        var id = await CreateResourceAsync();
        await UpdateVersionAsync(id, "Bumped", expectedVersion: 0);

        using var response = await _client.SendAsync(
            WithIfMatch(
                new HttpRequestMessage(HttpMethod.Delete, $"/api/resources/{id}"),
                version: 0)); // stale after the update

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);

        using var getResponse = await _client.GetAsync($"/api/resources/{id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    /// <summary>
    /// Applies a name update under the given (fresh) version so subsequent
    /// tests can reason about a known, already-bumped ETag.
    /// </summary>
    private async Task UpdateVersionAsync(Guid id, string name, int expectedVersion)
    {
        using var response = await _client.SendAsync(
            WithIfMatch(
                new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
                {
                    Content = JsonContent.Create(
                        new { name, description = (string?)null, tags = (string[]?)null })
                },
                expectedVersion));

        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Asserts that a response is an RFC 7807 problem+json document with the
    /// expected title and a stable type URI under the API's error base.
    /// </summary>
    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        string expectedTitle,
        string expectedTypeSuffix)
    {
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
        Assert.EndsWith(
            expectedTypeSuffix,
            problem.GetProperty("type").GetString());
        Assert.False(
            string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
    }
}
