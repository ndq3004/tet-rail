using System.Text.Json.Serialization;
using TetRail.Catalog.TripSearch;

namespace TetRail.Catalog.SeatMap;

public sealed record SeatMapQuery(Guid TripId, string From, string To);

public sealed record SeatMapSeat(
    [property: JsonPropertyName("seat_id")] Guid SeatId,
    [property: JsonPropertyName("seat_number")] string SeatNumber,
    [property: JsonPropertyName("carriage_id")] Guid CarriageId,
    [property: JsonPropertyName("carriage_number")] string CarriageNumber,
    [property: JsonPropertyName("seat_class")] string SeatClass,
    [property: JsonPropertyName("seat_type")] string SeatType,
    string Status);

public sealed record SeatMapResponse(
    [property: JsonPropertyName("trip_id")] Guid TripId,
    [property: JsonPropertyName("from_station")] StationSummary FromStation,
    [property: JsonPropertyName("to_station")] StationSummary ToStation,
    IReadOnlyList<SeatMapSeat> Seats,
    [property: JsonPropertyName("availability_as_of")] DateTimeOffset AvailabilityAsOf,
    [property: JsonPropertyName("projection_version")] long ProjectionVersion,
    [property: JsonPropertyName("is_stale")] bool IsStale,
    [property: JsonPropertyName("availability_notice")] string AvailabilityNotice,
    [property: JsonPropertyName("cache_status")] string CacheStatus);

public sealed record SeatMapSnapshot(SeatMapResponse Response, long DataVersion);

public sealed record SeatMapResult(bool IsValid, SeatMapResponse? Response, IReadOnlyDictionary<string, string[]> Errors)
{
    public static SeatMapResult Success(SeatMapResponse response) => new(true, response, EmptyErrors);
    public static SeatMapResult Invalid(IReadOnlyDictionary<string, string[]> errors) => new(false, null, errors);
    private static readonly IReadOnlyDictionary<string, string[]> EmptyErrors = new Dictionary<string, string[]>();
}
