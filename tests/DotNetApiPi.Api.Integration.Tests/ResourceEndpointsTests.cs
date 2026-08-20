using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNetApiPi.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// End-to-end tests for the resource endpoints over the full application
/// pipeline (middleware, DI, EF Core + SQLite).
/// </summary>
public sealed class ResourceEndpointsTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    /// <summary>
    /// Creates a dedicated in-process host and database per test.
    /// </summary>
    public Task InitializeAsync()
    {
        _factory = new ApiFactory();
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

    /// <summary>
    /// Creates a resource through the API and returns its JSON.
    /// </summary>
    private async Task<JsonElement> CreateResourceAsync(
        string name = "Integration Resource",
        string? description = "Created by a test",
        string[]? tags = null)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new
            {
                name,
                description,
                tags
            });

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }

    [Fact]
    public async Task Create_Returns201_AndStringStatus()
    {
        var json = await CreateResourceAsync();

        // The status is serialized as a stable string (not a numeric enum).
        Assert.Equal(
            ResourceStatus.Draft.ToString(),
            json.GetProperty("status").GetString());

        Assert.Equal("Integration Resource", json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("id").GetGuid() == Guid.Empty);
    }

    [Fact]
    public async Task Create_ResponseCarriesCorrelationIdHeader()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new { name = "Correlated", description = (string?)null, tags = (string[]?)null });

        response.EnsureSuccessStatusCode();

        var header = response.Headers
            .FirstOrDefault(header => header.Key == "X-Correlation-Id")
            .Value
            .FirstOrDefault();

        Assert.False(string.IsNullOrWhiteSpace(header));
    }

    [Fact]
    public async Task Create_Returns201_AndLocationHeader()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new { name = "Located", description = (string?)null, tags = (string[]?)null });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = json.GetProperty("id").GetGuid();

        // Under the in-process test server the Location header is an
        // absolute URI rooted at the test server's base address.
        Assert.Equal(
            $"http://localhost/api/resources/{id}",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_ReturnsCreatedResource()
    {
        var created = await CreateResourceAsync("Findable");
        var id = created.GetProperty("id").GetGuid();

        var response = await _client.GetAsync($"/api/resources/{id}");

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("Findable", json.GetProperty("name").GetString());
        Assert.Equal(ResourceStatus.Draft.ToString(), json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetAll_ReturnsCreatedResources()
    {
        await CreateResourceAsync("First");
        await CreateResourceAsync("Second");

        var response = await _client.GetAsync("/api/resources");

        response.EnsureSuccessStatusCode();
        var resources = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.NotNull(resources);
        Assert.Equal(2, resources!.Length);
    }

    [Fact]
    public async Task Update_RenamesResource()
    {
        var created = await CreateResourceAsync("Before Update");
        var id = created.GetProperty("id").GetGuid();

        // Mutating endpoints require If-Match carrying the current ETag
        // (a fresh resource is at version 0).
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/resources/{id}")
        {
            Content = JsonContent.Create(
                new { name = "After Update", description = "Updated", tags = (string[]?)null })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");

        var response = await _client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var json = (await response.Content.ReadFromJsonAsync<JsonElement>())!;

        Assert.Equal("After Update", json.GetProperty("name").GetString());
        Assert.Equal("Updated", json.GetProperty("description").GetString());
        // Two real state changes (rename + description) → two version bumps.
        Assert.Equal(2, json.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Activate_ThenArchive_FollowsLifecycle()
    {
        var created = await CreateResourceAsync();
        var id = created.GetProperty("id").GetGuid();

        var activateRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/activate");
        activateRequest.Headers.TryAddWithoutValidation("If-Match", "\"0\"");

        var activateResponse = await _client.SendAsync(activateRequest);

        activateResponse.EnsureSuccessStatusCode();
        var activateJson = (await activateResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(ResourceStatus.Active.ToString(), activateJson.GetProperty("status").GetString());

        // Activation bumped the version to 1; the archive must carry it.
        var archiveRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/archive");
        archiveRequest.Headers.TryAddWithoutValidation("If-Match", "\"1\"");

        var archiveResponse = await _client.SendAsync(archiveRequest);

        archiveResponse.EnsureSuccessStatusCode();
        var archiveJson = (await archiveResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Equal(ResourceStatus.Archived.ToString(), archiveJson.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Delete_Returns204_AndRemovesResource()
    {
        var created = await CreateResourceAsync();
        var id = created.GetProperty("id").GetGuid();

        var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/resources/{id}");
        deleteRequest.Headers.TryAddWithoutValidation("If-Match", "\"0\"");

        var deleteResponse = await _client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getResponse = await _client.GetAsync($"/api/resources/{id}");

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_WithBlankName_Returns400ProblemJson()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new { name = "   ", description = (string?)null, tags = (string[]?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Bad request");
    }

    [Fact]
    public async Task Create_WithNameOverDomainLimit_Returns400ProblemJson()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/resources",
            new { name = new string('a', 257), description = (string?)null, tags = (string[]?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Bad request");
    }

    [Fact]
    public async Task Get_ForUnknownId_Returns404ProblemJson()
    {
        var response = await _client.GetAsync($"/api/resources/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Not found");
    }

    [Fact]
    public async Task Activate_AlreadyActiveResource_Returns409ProblemJson()
    {
        var created = await CreateResourceAsync();
        var id = created.GetProperty("id").GetGuid();

        var firstActivate = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/activate");
        firstActivate.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await _client.SendAsync(firstActivate)).EnsureSuccessStatusCode();

        // A fresh If-Match (version 1) rules out a concurrency conflict: the
        // 409 must come from the state transition itself.
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/activate");
        request.Headers.TryAddWithoutValidation("If-Match", "\"1\"");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Conflict");
    }

    [Fact]
    public async Task Archive_DraftResource_Returns409ProblemJson()
    {
        var created = await CreateResourceAsync();
        var id = created.GetProperty("id").GetGuid();

        // Fresh If-Match on a draft: the 409 is the state transition
        // (drafts cannot be archived), not a concurrency conflict.
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/resources/{id}/archive");
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertProblemDetailsAsync(response, "Conflict");
    }

    /// <summary>
    /// Asserts that a response is an RFC 7807 problem+json document with the
    /// expected title, a stable type URI and a non-empty detail.
    /// </summary>
    private static async Task AssertProblemDetailsAsync(
        HttpResponseMessage response,
        string expectedTitle)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.Equal(expectedTitle, problem.GetProperty("title").GetString());
        Assert.False(
            string.IsNullOrEmpty(problem.GetProperty("type").GetString()));
        Assert.False(
            string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
    }
}
