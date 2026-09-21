using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TetRail.Catalog.SeatMap;
using TetRail.Catalog.TripSearch;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class SeatMapServiceTests
{
    private static readonly Guid TripId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetAsync_rejects_invalid_journey_without_reading_repository()
    {
        var repository = new StubRepository(null);
        var result = await CreateService(repository, new StubCache()).GetAsync("not-a-guid", "SGN", "SGN", CancellationToken.None);
        Assert.False(result.IsValid);
        Assert.Equal(0, repository.CallCount);
        Assert.Contains("tripId", result.Errors);
        Assert.Contains("to", result.Errors);
    }

    [Fact]
    public async Task GetAsync_returns_segment_aware_projection_and_warms_cache()
    {
        var repository = new StubRepository(new SeatMapSnapshot(CreateResponse(), 7));
        var cache = new StubCache();
        var result = await CreateService(repository, cache).GetAsync(TripId.ToString(), "sgn", "hno", CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("MISS", result.Response!.CacheStatus);
        Assert.Contains(result.Response.Seats, seat => seat.Status == "SOLD");
        Assert.Contains("does not guarantee", result.Response.AvailabilityNotice);
        Assert.Equal(1, repository.CallCount);
        Assert.Contains(cache.SetKeys, key => key.EndsWith(":7"));
    }

    [Fact]
    public async Task GetAsync_uses_cached_projection_without_reading_repository()
    {
        var cached = CreateResponse() with { CacheStatus = "MISS" };
        var repository = new StubRepository(null);
        var result = await CreateService(repository, new StubCache { Value = cached }).GetAsync(TripId.ToString(), "SGN", "HNO", CancellationToken.None);
        Assert.True(result.IsValid);
        Assert.Equal("HIT", result.Response!.CacheStatus);
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task GetAsync_recalculates_staleness_for_a_versioned_cached_projection()
    {
        var cached = CreateResponse() with { AvailabilityAsOf = Now.AddMinutes(-3), IsStale = false };
        var result = await CreateService(new StubRepository(null), new StubCache { Value = cached }).GetAsync(TripId.ToString(), "SGN", "HNO", CancellationToken.None);

        Assert.True(result.Response!.IsStale);
    }

    private static SeatMapService CreateService(ISeatMapRepository repository, ISeatMapCache cache) => new(repository, cache, new FixedTimeProvider(Now), Options.Create(new SeatMapOptions()), NullLogger<SeatMapService>.Instance);

    private static SeatMapResponse CreateResponse() => new(TripId, new StationSummary("SGN", "Saigon"), new StationSummary("HNO", "Hanoi"), [new SeatMapSeat(Guid.NewGuid(), "1A", Guid.NewGuid(), "1", "SOFT_SEAT", "WINDOW", "SOLD"), new SeatMapSeat(Guid.NewGuid(), "1B", Guid.NewGuid(), "1", "SOFT_SEAT", "AISLE", "HELD")], Now, 3, false, string.Empty, "MISS");

    private sealed class StubRepository(SeatMapSnapshot? value) : ISeatMapRepository
    {
        public int CallCount { get; private set; }
        public Task<long?> GetDataVersionAsync(Guid tripId, CancellationToken cancellationToken) => Task.FromResult<long?>(value?.DataVersion ?? 7);
        public Task<SeatMapSnapshot?> GetAsync(SeatMapQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken) { CallCount++; return Task.FromResult(value); }
    }
    private sealed class StubCache : ISeatMapCache
    {
        public SeatMapResponse? Value { get; init; }
        public string LastKey { get; private set; } = string.Empty;
        public List<string> SetKeys { get; } = [];
        public Task<SeatMapResponse?> GetAsync(string key, CancellationToken cancellationToken) { LastKey = key; return Task.FromResult(Value); }
        public Task SetAsync(string key, SeatMapResponse response, TimeSpan ttl, CancellationToken cancellationToken) { LastKey = key; SetKeys.Add(key); return Task.CompletedTask; }
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
