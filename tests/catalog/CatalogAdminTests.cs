using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TetRail.Catalog.Migrations;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class CatalogAdminTests
{
    [CatalogAdminDatabaseFact]
    public async Task StationWritesRequireAdminAndAuditOnlyCommittedChanges()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("CATALOG_ADMIN_TEST_DATABASE_URL")!);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.AddSingleton(database);
            });
        });
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<ICatalogMigrationRunner>().RunAsync(default);
        var id = Guid.NewGuid();
        var path = $"/api/v1/admin/catalog/stations/{id}";
        client.DefaultRequestHeaders.Add("X-Test-Subject", "test-operator");
        using var foreign = await client.PutAsJsonAsync(path, new { code = "US099", name = "Test station" });
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Role", "ADMIN");
        using var invalid = await client.PutAsJsonAsync(path, new { code = "lower", name = "Test station" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var created = await client.PutAsJsonAsync(path, new { code = "US099", name = "Test station" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var replay = await client.PutAsJsonAsync(path, new { code = "US099", name = "Test station" });
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var deleted = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var stationA = Guid.NewGuid();
        var stationB = Guid.NewGuid();
        var route = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var carriage = Guid.NewGuid();
        var seat = Guid.NewGuid();
        var stopA = Guid.NewGuid();
        var stopB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var resources = new (string Name, Guid Id, object Body)[]
        {
            ("stations", stationA, new { code = "US10A", name = "Origin" }),
            ("stations", stationB, new { code = "US10B", name = "Destination" }),
            ("routes", route, new { code = "US10", name = "Test route" }),
            ("route_stops", stopA, new { route_id = route, station_id = stationA, stop_order = 1 }),
            ("route_stops", stopB, new { route_id = route, station_id = stationB, stop_order = 2 }),
            ("trips", trip, new { route_id = route, trip_number = "US10", status = "SCHEDULED" }),
            ("carriages", carriage, new { carriage_number = "US10", seat_class = "SOFT_SEAT" }),
            ("seats", seat, new { carriage_id = carriage, seat_number = "1A", seat_type = "WINDOW" }),
            ("fares", Guid.NewGuid(), new { route_id = route, from_stop_order = 1, to_stop_order = 2,
                seat_class = "SOFT_SEAT", price = 100, currency = "VND", version = 1,
                effective_from = now, effective_to = (DateTimeOffset?)null })
        };
        await using var version = database.CreateCommand("SELECT version FROM catalog.trip_search_data_version");
        var previousVersion = (long)(await version.ExecuteScalarAsync())!;
        foreach (var resource in resources)
        {
            var collection = $"/api/v1/admin/catalog/{resource.Name}";
            using var write = await client.PutAsJsonAsync($"{collection}/{resource.Id}", resource.Body);
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
            using var list = await client.GetAsync(collection);
            Assert.Contains(resource.Id.ToString(), await list.Content.ReadAsStringAsync());
        }
        var stops = new[]
        {
            new { routeStopId = stopA, arrivalAt = now, departureAt = now },
            new { routeStopId = stopB, arrivalAt = now.AddHours(1), departureAt = now.AddHours(1) }
        };
        var schedule = $"/api/v1/admin/catalog/trips/{trip}/schedule";
        using var reversed = await client.PutAsJsonAsync(schedule, new { stops = stops.Reverse(), seatIds = new[] { seat } });
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        using var scheduled = await client.PutAsJsonAsync(schedule, new { stops, seatIds = new[] { seat } });
        Assert.Equal(HttpStatusCode.NoContent, scheduled.StatusCode);
        using var conflict = await client.DeleteAsync($"/api/v1/admin/catalog/seats/{seat}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.True((long)(await version.ExecuteScalarAsync())! > previousVersion);
        await using var scheduleAudit = database.CreateCommand("""
            SELECT count(*) FROM catalog.admin_audit WHERE resource='trip_schedule' AND resource_id=@trip
            """);
        scheduleAudit.Parameters.AddWithValue("trip", trip);
        Assert.Equal(1L, (long)(await scheduleAudit.ExecuteScalarAsync())!);
        await using var audit = database.CreateCommand("SELECT count(*) FROM catalog.admin_audit WHERE resource_id=@id");
        audit.Parameters.AddWithValue("id", id);
        Assert.Equal(2L, (long)(await audit.ExecuteScalarAsync())!);
    }
}

internal sealed class CatalogAdminDatabaseFactAttribute : FactAttribute
{
    public CatalogAdminDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CATALOG_ADMIN_TEST_DATABASE_URL")))
            Skip = "Set CATALOG_ADMIN_TEST_DATABASE_URL to a disposable PostgreSQL database.";
    }
}
