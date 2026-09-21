using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TetRail.Catalog.Migrations;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class CatalogMigrationEndpointTests
{
    [Fact]
    public async Task Run_returns_applied_and_skipped_migrations_in_development()
    {
        await using var factory = new CatalogFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/internal/migrations/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<MigrationRunResult>();
        Assert.Equal(["001_trip_search.sql"], result!.Applied);
        Assert.Equal(["002_development_seed.sql"], result.Skipped);
    }

    [Fact]
    public async Task Run_is_not_registered_outside_development()
    {
        await using var factory = new CatalogFactory("Production");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/internal/migrations/run", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class CatalogFactory(string environment = "Testing") : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICatalogMigrationRunner>();
                services.AddSingleton<ICatalogMigrationRunner>(new FakeMigrationRunner());
            });
        }
    }

    private sealed class FakeMigrationRunner : ICatalogMigrationRunner
    {
        public Task<MigrationRunResult> RunAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new MigrationRunResult(["001_trip_search.sql"], ["002_development_seed.sql"]));
    }
}
