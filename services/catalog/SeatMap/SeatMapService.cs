using Microsoft.Extensions.Options;

namespace TetRail.Catalog.SeatMap;

public sealed class SeatMapService(
    ISeatMapRepository repository,
    ISeatMapCache cache,
    TimeProvider timeProvider,
    IOptions<SeatMapOptions> options,
    ILogger<SeatMapService> logger)
{
    private const string Notice = "Availability is a timestamped projection and does not guarantee that a hold will succeed.";
    private readonly SeatMapOptions settings = options.Value;

    public async Task<SeatMapResult> GetAsync(string? tripId, string? from, string? to, CancellationToken cancellationToken)
    {
        var errors = Validate(tripId, from, to, out var query);
        if (errors.Count > 0)
            return SeatMapResult.Invalid(errors);

        var cachePrefix = $"seat-map:v1:{query!.TripId:N}:{query.From}:{query.To}:";
        var dataVersion = await repository.GetDataVersionAsync(query.TripId, cancellationToken);
        if (dataVersion is not null)
        {
            try
            {
                var cached = await cache.GetAsync(cachePrefix + dataVersion, cancellationToken);
                if (cached is not null)
                    return SeatMapResult.Success(RefreshStaleness(cached, timeProvider.GetUtcNow()) with { CacheStatus = "HIT", AvailabilityNotice = Notice });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Seat map cache read failed; falling back to PostgreSQL.");
            }
        }

        var snapshot = await repository.GetAsync(query, timeProvider.GetUtcNow(), TimeSpan.FromSeconds(settings.StaleAfterSeconds), cancellationToken);
        if (snapshot is null)
            return SeatMapResult.Invalid(new Dictionary<string, string[]> { ["journey"] = ["Trip does not serve the requested ordered journey."] });

        var response = snapshot.Response with { CacheStatus = "MISS", AvailabilityNotice = Notice };
        try
        {
            await cache.SetAsync(cachePrefix + snapshot.DataVersion, response, TimeSpan.FromSeconds(settings.CacheTtlSeconds), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Seat map cache write failed; returning PostgreSQL result.");
            response = response with { CacheStatus = "FALLBACK" };
        }
        return SeatMapResult.Success(response);
    }

    private SeatMapResponse RefreshStaleness(SeatMapResponse response, DateTimeOffset now) => response with
    {
        IsStale = now - response.AvailabilityAsOf > TimeSpan.FromSeconds(settings.StaleAfterSeconds)
    };

    private static Dictionary<string, string[]> Validate(string? tripId, string? from, string? to, out SeatMapQuery? query)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Guid.TryParse(tripId, out var parsedTripId)) errors["tripId"] = ["Trip ID must be a valid UUID."];
        var normalizedFrom = from?.Trim().ToUpperInvariant();
        var normalizedTo = to?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedFrom) || normalizedFrom.Length > 10) errors["from"] = ["Origin station code is required and must not exceed 10 characters."];
        if (string.IsNullOrWhiteSpace(normalizedTo) || normalizedTo.Length > 10) errors["to"] = ["Destination station code is required and must not exceed 10 characters."];
        if (normalizedFrom == normalizedTo && !string.IsNullOrWhiteSpace(normalizedFrom)) errors["to"] = ["Destination station must differ from origin station."];
        query = errors.Count == 0 ? new SeatMapQuery(parsedTripId, normalizedFrom!, normalizedTo!) : null;
        return errors;
    }
}
