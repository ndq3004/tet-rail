using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TetRail.Catalog.TripSearch;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class TripSearchEndpointTests : IClassFixture<TripSearchEndpointTests.CatalogFactory>
{
    private readonly HttpClient client;

    public TripSearchEndpointTests(CatalogFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task Search_returns_api_error_v1_and_correlation_id_for_invalid_input()
    {
        using var response = await client.GetAsync("/api/v1/trips?from=SGN&to=SGN&date=2027-02-05");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var values));
        Assert.True(Guid.TryParse(values.Single(), out _));
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("VALIDATION_FAILED", error?.Code);
        Assert.Contains("to", error!.Errors!);
    }

    [Fact]
    public async Task Search_returns_empty_result_when_query_is_valid()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/trips?from=SGN&to=HNO&date=2027-02-05");
        request.Headers.Add("X-Correlation-ID", "67e55044-10b1-426f-9247-bb680e5fe0c8");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("67e55044-10b1-426f-9247-bb680e5fe0c8", response.Headers.GetValues("X-Correlation-ID").Single());
        var result = await response.Content.ReadFromJsonAsync<TripSearchResponse>();
        Assert.Empty(result!.Trips);
    }

    [Fact]
    public async Task Search_returns_api_error_v1_when_availability_data_is_insufficient()
    {
        using var factory = new UnavailableCatalogFactory();
        using var unavailableClient = factory.CreateClient();

        using var response = await unavailableClient.GetAsync("/api/v1/trips?from=SGN&to=HNO&date=2027-02-05");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("CATALOG_UNAVAILABLE", error?.Code);
        Assert.NotEqual(Guid.Empty, error?.CorrelationId);
    }

    [Fact]
    public async Task Passenger_routes_require_authentication_and_return_api_error_v1()
    {
        using var response = await client.GetAsync("/api/v1/passengers");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("UNAUTHENTICATED", error?.Code);
        Assert.NotEqual(Guid.Empty, error?.CorrelationId);
    }

    [Fact]
    public async Task Testing_authentication_allows_customer_identity_but_denies_admin_route()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity/me");
        request.Headers.Add("X-Test-Subject", "customer-a");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/identity/me");
        adminRequest.Headers.Add("X-Test-Subject", "customer-a");
        using var adminResponse = await client.SendAsync(adminRequest);
        Assert.Equal(HttpStatusCode.Forbidden, adminResponse.StatusCode);
    }

    public sealed class CatalogFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITripSearchRepository>();
                services.RemoveAll<ITripSearchCache>();
                services.AddSingleton<ITripSearchRepository, EmptyRepository>();
                services.AddSingleton<ITripSearchCache, EmptyCache>();
            });
        }
    }

    public sealed class UnavailableCatalogFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITripSearchRepository>();
                services.AddSingleton<ITripSearchRepository, UnavailableRepository>();
            });
        }
    }

    private sealed class EmptyRepository : ITripSearchRepository
    {
        public Task<string> GetDataVersionAsync(CancellationToken cancellationToken) => Task.FromResult("catalog-1");
        public Task<IReadOnlyList<TripSearchItem>> SearchAsync(TripSearchQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TripSearchItem>>([]);
    }

    private sealed class UnavailableRepository : ITripSearchRepository
    {
        public Task<string> GetDataVersionAsync(CancellationToken cancellationToken) => Task.FromResult("catalog-1");
        public Task<IReadOnlyList<TripSearchItem>> SearchAsync(TripSearchQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyList<TripSearchItem>>(new TripSearchDataUnavailableException("Projection unavailable."));
    }

    private sealed class EmptyCache : ITripSearchCache
    {
        public Task<TripSearchResponse?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<TripSearchResponse?>(null);
        public Task SetAsync(string key, TripSearchResponse response, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
