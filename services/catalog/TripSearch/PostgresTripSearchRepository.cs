using Npgsql;

namespace TetRail.Catalog.TripSearch;

public sealed class PostgresTripSearchRepository(NpgsqlDataSource dataSource) : ITripSearchRepository
{
    private const string SearchSql = """
        SELECT t.id, t.trip_number, t.status,
               origin.code, origin.name, destination.code, destination.name,
               departure.departure_at, arrival.arrival_at,
               f.seat_class, f.price, f.currency, f.version, f.effective_from,
               a.status, a.available_count, a.as_of
        FROM catalog.trips t
        JOIN catalog.trip_stops departure ON departure.trip_id = t.id
        JOIN catalog.route_stops departure_route_stop ON departure_route_stop.id = departure.route_stop_id
        JOIN catalog.stations origin ON origin.id = departure_route_stop.station_id
        JOIN catalog.trip_stops arrival ON arrival.trip_id = t.id
        JOIN catalog.route_stops arrival_route_stop ON arrival_route_stop.id = arrival.route_stop_id
        JOIN catalog.stations destination ON destination.id = arrival_route_stop.station_id
        JOIN catalog.fares f ON f.route_id = t.route_id
          AND f.from_stop_order = departure_route_stop.stop_order
          AND f.to_stop_order = arrival_route_stop.stop_order
          AND f.effective_from <= @now
          AND (f.effective_to IS NULL OR f.effective_to > @now)
        LEFT JOIN catalog.trip_availability_summary a ON a.trip_id = t.id AND a.seat_class = f.seat_class
        WHERE origin.code = @from
          AND destination.code = @to
          AND departure_route_stop.stop_order < arrival_route_stop.stop_order
          AND (departure.departure_at AT TIME ZONE 'Asia/Bangkok')::date = @date
          AND t.status = 'SCHEDULED'
        ORDER BY departure.departure_at, t.id, f.seat_class;
        """;

    public async Task<IReadOnlyList<TripSearchItem>> SearchAsync(TripSearchQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SearchSql);
        command.Parameters.AddWithValue("from", query.From);
        command.Parameters.AddWithValue("to", query.To);
        command.Parameters.AddWithValue("date", query.Date);
        command.Parameters.AddWithValue("now", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<Row>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7), reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetString(9), reader.GetDecimal(10), reader.GetString(11), reader.GetInt64(12),
                reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14) ? "UNKNOWN" : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? now : reader.GetFieldValue<DateTimeOffset>(16)));
        }

        return rows.GroupBy(row => row.TripId).Select(group =>
        {
            var first = group.First();
            var asOf = group.Min(row => row.AvailabilityAsOf);
            var statuses = group.Select(row => row.AvailabilityStatus).Distinct().ToArray();
            var availabilityStatus = statuses.Length == 1 ? statuses[0] : "MIXED";
            var availableCount = group.Any(row => row.AvailableCount is null) ? null : group.Sum(row => row.AvailableCount);
            return new TripSearchItem(
                first.TripId, first.TripNumber,
                new StationSummary(first.FromCode, first.FromName), new StationSummary(first.ToCode, first.ToName),
                first.DepartureAt, first.ArrivalAt,
                checked((int)(first.ArrivalAt - first.DepartureAt).TotalMinutes), first.ScheduleStatus,
                group.Select(row => new FareOption(row.SeatClass, row.Price, row.Currency, row.FareVersion, row.FareEffectiveAt)).ToArray(),
                new AvailabilitySummary(availabilityStatus, availableCount, asOf, now - asOf > staleAfter));
        }).OrderBy(item => item.DepartureAt).ToArray();
    }

    private sealed record Row(
        Guid TripId, string TripNumber, string ScheduleStatus,
        string FromCode, string FromName, string ToCode, string ToName,
        DateTimeOffset DepartureAt, DateTimeOffset ArrivalAt,
        string SeatClass, decimal Price, string Currency, long FareVersion, DateTimeOffset FareEffectiveAt,
        string AvailabilityStatus, int? AvailableCount, DateTimeOffset AvailabilityAsOf);
}
