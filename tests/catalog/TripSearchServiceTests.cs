using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TetRail.Catalog.TripSearch;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class TripSearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SearchAsync_rejects_same_origin_and_destination_without_querying_repository()
    {
        var repository = new StubRepository([]);
        var service = CreateService(repository, new StubCache());

        var result = await service.SearchAsync("SGN", "sgn", "2027-02-05", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("to", result.Errors);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task SearchAsync_normalizes_station_codes_and_returns_repository_data_on_cache_miss()
    {
        var item = CreateTrip();
        var repository = new StubRepository([item]);
        var cache = new StubCache();
        var service = CreateService(repository, cache);

        var result = await service.SearchAsync(" sgn ", "hno", "2027-02-05", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("SGN", repository.LastQuery?.From);
        Assert.Equal("HNO", repository.LastQuery?.To);
        Assert.Equal("MISS", result.Response?.CacheStatus);
        Assert.Single(result.Response!.Trips);
        Assert.Contains(":SGN:HNO:2027-02-05:", cache.LastKey);
    }

    [Fact]
    public async Task SearchAsync_returns_cached_result_without_querying_repository()
    {
        var cached = new TripSearchResponse([CreateTrip()], Now, "MISS", "catalog-v1");
        var repository = new StubRepository([]);
        var service = CreateService(repository, new StubCache { Value = cached });

        var result = await service.SearchAsync("SGN", "HNO", "2027-02-05", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("HIT", result.Response?.CacheStatus);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_repository_when_cache_is_unavailable()
    {
        var repository = new StubRepository([CreateTrip()]);
        var service = CreateService(repository, new StubCache { ThrowOnGet = true, ThrowOnSet = true });

        var result = await service.SearchAsync("SGN", "HNO", "2027-02-05", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("FALLBACK", result.Response?.CacheStatus);
        Assert.Equal(1, repository.CallCount);
    }

    private static TripSearchService CreateService(ITripSearchRepository repository, ITripSearchCache cache)
    {
        var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return new TripSearchService(
            repository,
            cache,
            new FixedTimeProvider(Now),
            Options.Create(new TripSearchOptions()),
            new TripSearchMetrics(services.GetRequiredService<IMeterFactory>()),
            NullLogger<TripSearchService>.Instance);
    }

    private static TripSearchItem CreateTrip() => new(
        Guid.Parse("40000000-0000-0000-0000-000000000001"),
        "SE2-2027-02-05",
        new StationSummary("SGN", "Saigon"),
        new StationSummary("HNO", "Hanoi"),
        new DateTimeOffset(2027, 2, 5, 0, 10, 0, TimeSpan.FromHours(7)),
        new DateTimeOffset(2027, 2, 6, 9, 0, 0, TimeSpan.FromHours(7)),
        1970,
        "SCHEDULED",
        [new FareOption("SOFT_SEAT", 1_250_000, "VND", 1, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero))],
        new AvailabilitySummary("AVAILABLE", 96, Now, false));

    private sealed class StubRepository(IReadOnlyList<TripSearchItem> value) : ITripSearchRepository
    {
        public int CallCount { get; private set; }
        public TripSearchQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<TripSearchItem>> SearchAsync(TripSearchQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken)
        {
            CallCount++;
            LastQuery = query;
            return Task.FromResult(value);
        }
    }

    private sealed class StubCache : ITripSearchCache
    {
        public TripSearchResponse? Value { get; init; }
        public bool ThrowOnGet { get; init; }
        public bool ThrowOnSet { get; init; }
        public string LastKey { get; private set; } = string.Empty;

        public Task<TripSearchResponse?> GetAsync(string key, CancellationToken cancellationToken)
        {
            LastKey = key;
            return ThrowOnGet ? Task.FromException<TripSearchResponse?>(new InvalidOperationException("Redis unavailable")) : Task.FromResult(Value);
        }

        public Task SetAsync(string key, TripSearchResponse response, TimeSpan ttl, CancellationToken cancellationToken)
        {
            LastKey = key;
            return ThrowOnSet ? Task.FromException(new InvalidOperationException("Redis unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
