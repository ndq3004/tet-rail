using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TetRail.Catalog.TripSearch;

public sealed class TripSearchService(
    ITripSearchRepository repository,
    ITripSearchCache cache,
    TimeProvider timeProvider,
    IOptions<TripSearchOptions> options,
    TripSearchMetrics metrics,
    ILogger<TripSearchService> logger)
{
    private readonly TripSearchOptions settings = options.Value;

    public async Task<TripSearchResult> SearchAsync(string? from, string? to, string? date, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var errors = Validate(from, to, date, out var query);
        if (errors.Count > 0)
        {
            metrics.RecordRequest("validation_error", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return TripSearchResult.Invalid(errors);
        }

        var dataVersion = await repository.GetDataVersionAsync(cancellationToken);
        var cacheKey = $"trip-search:v1:{query!.From}:{query.To}:{query.Date:yyyy-MM-dd}:{dataVersion}";
        try
        {
            var cached = await cache.GetAsync(cacheKey, cancellationToken);
            if (cached is not null)
            {
                cached = RefreshStaleness(cached, timeProvider.GetUtcNow());
                metrics.RecordCache("hit");
                if (cached.Trips.Any(trip => trip.Availability.IsStale)) metrics.RecordCache("stale");
                metrics.RecordRequest("success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return TripSearchResult.Success(cached with { CacheStatus = "HIT" });
            }
            metrics.RecordCache("miss");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.RecordCache("fallback");
            logger.LogWarning(exception, "Trip search cache read failed; falling back to PostgreSQL.");
        }

        var now = timeProvider.GetUtcNow();
        IReadOnlyList<TripSearchItem> trips;
        try
        {
            trips = await repository.SearchAsync(query, now, TimeSpan.FromSeconds(settings.AvailabilityStaleAfterSeconds), cancellationToken);
        }
        catch
        {
            metrics.RecordRequest("error", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        var response = new TripSearchResponse(trips, now, "MISS", dataVersion);

        try
        {
            await cache.SetAsync(cacheKey, response, TimeSpan.FromSeconds(settings.CacheTtlSeconds), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            metrics.RecordCache("fallback");
            logger.LogWarning(exception, "Trip search cache write failed; returning PostgreSQL result.");
            response = response with { CacheStatus = "FALLBACK" };
        }

        metrics.RecordRequest("success", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return TripSearchResult.Success(response);
    }

    private TripSearchResponse RefreshStaleness(TripSearchResponse response, DateTimeOffset now) => response with
    {
        Trips = response.Trips.Select(trip => trip with
        {
            Availability = trip.Availability with { IsStale = now - trip.Availability.AsOf > TimeSpan.FromSeconds(settings.AvailabilityStaleAfterSeconds) }
        }).ToArray()
    };

    private static Dictionary<string, string[]> Validate(string? from, string? to, string? date, out TripSearchQuery? query)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedFrom = from?.Trim().ToUpperInvariant();
        var normalizedTo = to?.Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(normalizedFrom) || normalizedFrom.Length > 10)
            errors["from"] = ["Origin station code is required and must not exceed 10 characters."];
        if (string.IsNullOrWhiteSpace(normalizedTo) || normalizedTo.Length > 10)
            errors["to"] = ["Destination station code is required and must not exceed 10 characters."];
        if (normalizedFrom == normalizedTo && !string.IsNullOrWhiteSpace(normalizedFrom))
            errors["to"] = ["Destination station must differ from origin station."];
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var parsedDate))
            errors["date"] = ["Date must use yyyy-MM-dd format."];

        query = errors.Count == 0 ? new TripSearchQuery(normalizedFrom!, normalizedTo!, parsedDate) : null;
        return errors;
    }
}
