using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DotNetApiPi.Api.Integration.Tests;

/// <summary>
/// Exercises the bounded list endpoint: <c>page</c>/<c>pageSize</c> query
/// parameters, the bare-array body plus <c>X-Total-Count</c> /
/// <c>X-Total-Pages</c> headers, stable ordering and the 400 contract for
/// out-of-range paging parameters.
/// </summary>
public sealed class PagingTests : IAsyncLifetime
{
    private const int TotalResources = 25;

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
    /// Creates <see cref="TotalResources"/> resources through the API.
    /// </summary>
    private async Task SeedResourcesAsync()
    {
        for (var i = 0; i < TotalResources; i++)
        {
            using var response = await _client.PostAsJsonAsync(
                "/api/resources",
                new { name = $"Paged Resource {i:D2}", description = (string?)null, tags = (string[]?)null });

            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task GetAll_DefaultsToFirstPage_WithMetadataHeaders()
    {
        await SeedResourcesAsync();

        using var response = await _client.GetAsync("/api/resources");

        response.EnsureSuccessStatusCode();
        var items = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Equal(20, items.Length); // the default page size

        AssertHeaders(response, TotalResources, (TotalResources + 19) / 20);
    }

    [Fact]
    public async Task GetAll_ReturnsTheRequestedPage_WithCorrectHeaders()
    {
        await SeedResourcesAsync();

        using var response = await _client.GetAsync("/api/resources?page=2&pageSize=10");

        response.EnsureSuccessStatusCode();
        var items = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;

        // The body remains a bare JSON array of resource DTOs.
        Assert.Equal(10, items.Length);
        AssertHeaders(response, 25, 3);
    }

    [Fact]
    public async Task GetAll_TilingThePages_YieldsEveryResourceExactlyOnce_InStableOrder()
    {
        await SeedResourcesAsync();

        var idsOnPages = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            using var response = await _client.GetAsync($"/api/resources?page={page}&pageSize=10");
            response.EnsureSuccessStatusCode();
            var items = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;
            idsOnPages.AddRange(items.Select(static item => item.GetProperty("id").GetGuid()));
        }

        // 25 unique resources across the 3 pages...
        Assert.Equal(TotalResources, idsOnPages.Distinct().Count());

        // ...and the pages are contiguous slices of the deterministic
        // identity ordering (no page shifts under the stable sort).
        Assert.Equal(idsOnPages, idsOnPages.OrderBy(static id => id).ToList());
    }

    [Fact]
    public async Task GetAll_LastPageMayBePartial()
    {
        await SeedResourcesAsync();

        using var response = await _client.GetAsync("/api/resources?page=3&pageSize=10");

        response.EnsureSuccessStatusCode();
        var items = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Equal(5, items.Length);
        AssertHeaders(response, 25, 3);
    }

    [Fact]
    public async Task GetAll_PastTheEnd_ReturnsAnEmptyArray_WithFullCount()
    {
        await SeedResourcesAsync();

        using var response = await _client.GetAsync("/api/resources?page=4&pageSize=10");

        response.EnsureSuccessStatusCode();
        var items = (await response.Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Empty(items);
        AssertHeaders(response, 25, 3);
    }

    [Theory]
    [InlineData("page=0&pageSize=10", "page must be >= 1")]
    [InlineData("page=1&pageSize=0", "pageSize between 1")]
    [InlineData("page=1&pageSize=101", "pageSize between 1")]
    [InlineData("page=-1&pageSize=10", "page must be >= 1")]
    public async Task GetAll_OutOfRangePagingParameters_Return400ProblemJson(
        string query,
        string expectedDetailFragment)
    {
        using var response = await _client.GetAsync($"/api/resources?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.Equal("Bad request", problem.GetProperty("title").GetString());
        Assert.Equal(
            "https://dotnet-api-pi.example/errors/bad-request",
            problem.GetProperty("type").GetString());
        Assert.Contains(expectedDetailFragment, problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Asserts the paging metadata headers on a 200 list response.
    /// </summary>
    private static void AssertHeaders(HttpResponseMessage response, int totalCount, int totalPages)
    {
        Assert.True(
            response.Headers.TryGetValues("X-Total-Count", out var countValues),
            "the X-Total-Count header is missing");
        Assert.Equal(totalCount.ToString(), countValues.Single());

        Assert.True(
            response.Headers.TryGetValues("X-Total-Pages", out var pageValues),
            "the X-Total-Pages header is missing");
        Assert.Equal(totalPages.ToString(), pageValues.Single());
    }
}
