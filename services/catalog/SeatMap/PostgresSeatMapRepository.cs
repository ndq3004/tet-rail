using Npgsql;
using TetRail.Catalog.TripSearch;

namespace TetRail.Catalog.SeatMap;

public sealed class PostgresSeatMapRepository(NpgsqlDataSource dataSource) : ISeatMapRepository
{
    public async Task<long?> GetDataVersionAsync(Guid tripId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT data_version FROM catalog.seat_projection_metadata WHERE trip_id = @tripId;");
        command.Parameters.AddWithValue("tripId", tripId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : (long)result;
    }

    public async Task<SeatMapSnapshot?> GetAsync(SeatMapQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string stopsSql = """
            SELECT s.code, s.name, rs.stop_order
            FROM catalog.trips t
            JOIN catalog.route_stops rs ON rs.route_id = t.route_id
            JOIN catalog.stations s ON s.id = rs.station_id
            WHERE t.id = @tripId AND t.status = 'SCHEDULED' AND s.code IN (@from, @to)
            ORDER BY rs.stop_order;
            """;
        await using var stopsCommand = new NpgsqlCommand(stopsSql, connection);
        stopsCommand.Parameters.AddWithValue("tripId", query.TripId);
        stopsCommand.Parameters.AddWithValue("from", query.From);
        stopsCommand.Parameters.AddWithValue("to", query.To);
        var stops = new List<(StationSummary Station, int Order)>();
        await using (var reader = await stopsCommand.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) stops.Add((new StationSummary(reader.GetString(0), reader.GetString(1)), reader.GetInt32(2)));
        if (stops.Count != 2 || stops[0].Station.Code != query.From || stops[1].Station.Code != query.To || stops[0].Order >= stops[1].Order)
            return null;

        const string seatsSql = """
            SELECT s.id, s.seat_number, c.id, c.carriage_number, c.seat_class, s.seat_type,
              CASE WHEN bool_or(p.status = 'SOLD') THEN 'SOLD'
                   WHEN bool_or(p.status = 'HELD') THEN 'HELD' ELSE 'AVAILABLE' END AS status,
              max(p.updated_at), coalesce(max(p.projection_version), 0), coalesce(max(m.data_version), 1), max(m.updated_at)
            FROM catalog.trip_seats ts
            JOIN catalog.seats s ON s.id = ts.seat_id
            JOIN catalog.carriages c ON c.id = s.carriage_id
            LEFT JOIN catalog.seat_segment_projection p ON p.trip_id = ts.trip_id AND p.seat_id = ts.seat_id
              AND p.segment_index >= @fromOrder AND p.segment_index < @toOrder
            LEFT JOIN catalog.seat_projection_metadata m ON m.trip_id = ts.trip_id
            WHERE ts.trip_id = @tripId
            GROUP BY s.id, s.seat_number, c.id, c.carriage_number, c.seat_class, s.seat_type
            ORDER BY c.carriage_number, s.seat_number;
            """;
        await using var seatsCommand = new NpgsqlCommand(seatsSql, connection);
        seatsCommand.Parameters.AddWithValue("tripId", query.TripId);
        seatsCommand.Parameters.AddWithValue("fromOrder", stops[0].Order);
        seatsCommand.Parameters.AddWithValue("toOrder", stops[1].Order);
        var seats = new List<SeatMapSeat>();
        DateTimeOffset? asOf = null;
        DateTimeOffset? metadataAsOf = null;
        long version = 0;
        long dataVersion = 1;
        await using (var reader = await seatsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                seats.Add(new SeatMapSeat(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
                if (!reader.IsDBNull(7)) asOf = !asOf.HasValue || reader.GetFieldValue<DateTimeOffset>(7) > asOf ? reader.GetFieldValue<DateTimeOffset>(7) : asOf;
                version = Math.Max(version, reader.GetInt64(8));
                dataVersion = Math.Max(dataVersion, reader.GetInt64(9));
                if (!reader.IsDBNull(10)) metadataAsOf = reader.GetFieldValue<DateTimeOffset>(10);
            }
        }
        if (seats.Count == 0) return null;
        var projectionAsOf = metadataAsOf ?? asOf ?? now;
        var response = new SeatMapResponse(query.TripId, stops[0].Station, stops[1].Station, seats, projectionAsOf, version, now - projectionAsOf > staleAfter, string.Empty, "MISS");
        return new SeatMapSnapshot(response, dataVersion);
    }
}
