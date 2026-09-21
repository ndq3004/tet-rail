using System.Text.Json.Serialization;

namespace TetRail.Catalog.TripSearch;

public sealed record TripSearchQuery(string From, string To, DateOnly Date);

public sealed record FareOption(
    [property: JsonPropertyName("seat_class")] string SeatClass,
    decimal Price,
    string Currency,
    [property: JsonPropertyName("fare_version")] long FareVersion,
    [property: JsonPropertyName("effective_at")] DateTimeOffset EffectiveAt);

public sealed record AvailabilitySummary(
    string Status,
    [property: JsonPropertyName("available_count")] int? AvailableCount,
    [property: JsonPropertyName("availability_as_of")] DateTimeOffset AsOf,
    [property: JsonPropertyName("is_stale")] bool IsStale);

public sealed record TripSearchItem(
    [property: JsonPropertyName("trip_id")] Guid TripId,
    [property: JsonPropertyName("trip_number")] string TripNumber,
    [property: JsonPropertyName("from_station")] StationSummary FromStation,
    [property: JsonPropertyName("to_station")] StationSummary ToStation,
    [property: JsonPropertyName("departure_at")] DateTimeOffset DepartureAt,
    [property: JsonPropertyName("arrival_at")] DateTimeOffset ArrivalAt,
    [property: JsonPropertyName("duration_minutes")] int DurationMinutes,
    [property: JsonPropertyName("schedule_status")] string ScheduleStatus,
    IReadOnlyList<FareOption> Fares,
    AvailabilitySummary Availability);

public sealed record StationSummary(string Code, string Name);

public sealed record TripSearchResponse(
    IReadOnlyList<TripSearchItem> Trips,
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("cache_status")] string CacheStatus,
    [property: JsonPropertyName("data_version")] string DataVersion);

public sealed record ApiError(
    string Type,
    string Title,
    int Status,
    string Code,
    [property: JsonPropertyName("correlation_id")] Guid CorrelationId,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record TripSearchResult(bool IsValid, TripSearchResponse? Response, IReadOnlyDictionary<string, string[]> Errors)
{
    public static TripSearchResult Success(TripSearchResponse response) => new(true, response, EmptyErrors);
    public static TripSearchResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new(false, null, errors);
    private static readonly IReadOnlyDictionary<string, string[]> EmptyErrors = new Dictionary<string, string[]>();
}

public sealed class TripSearchDataUnavailableException(string message) : Exception(message);
