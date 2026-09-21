using System.Text.Json;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class TripSearchContractTests
{
    [Fact]
    public void OpenApi_contract_contains_versioned_trip_search_and_projection_metadata()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "http", "trip-search.v1.openapi.json")));

        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/v1/trips", out _));
        var response = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("TripSearchResponse");
        var required = response.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("generated_at", required);
        Assert.Contains("cache_status", required);
        Assert.Contains("data_version", required);

        var availability = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Availability");
        var availabilityRequired = availability.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("availability_as_of", availabilityRequired);
        Assert.Contains("is_stale", availabilityRequired);
    }

    [Fact]
    public void Example_is_valid_json_and_uses_contract_cache_status()
    {
        var root = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "contracts", "http", "trip-search.v1.example.json")));
        Assert.Equal("MISS", document.RootElement.GetProperty("cache_status").GetString());
        Assert.NotEmpty(document.RootElement.GetProperty("trips").EnumerateArray());
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TetRail.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
