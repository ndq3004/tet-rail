using System.Security.Claims;
using System.Text.Json;
using Npgsql;

namespace TetRail.Catalog;

public static class CatalogAdmin
{
    private static readonly Dictionary<string, string[]> Columns = new(StringComparer.Ordinal)
    {
        ["stations"] = ["code", "name"],
        ["routes"] = ["code", "name"],
        ["trips"] = ["route_id", "trip_number", "status"],
        ["carriages"] = ["carriage_number", "seat_class"],
        ["seats"] = ["carriage_id", "seat_number", "seat_type"],
        ["fares"] = ["route_id", "from_stop_order", "to_stop_order", "seat_class", "price", "currency", "version", "effective_from", "effective_to"],
        ["route_stops"] = ["route_id", "station_id", "stop_order"]
    };

    public static void MapCatalogAdmin(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/catalog/{resource}").RequireAuthorization("Admin");
        group.MapGet("", ListAsync);
        group.MapPut("/{id:guid}", WriteAsync);
        group.MapDelete("/{id:guid}", WriteAsync);
        app.MapPut("/api/v1/admin/catalog/trips/{tripId:guid}/schedule", UpdateScheduleAsync)
            .RequireAuthorization("Admin");
    }

    private sealed record ScheduledStop(Guid RouteStopId, DateTimeOffset ArrivalAt, DateTimeOffset DepartureAt);
    private sealed record TripSchedule(ScheduledStop[] Stops, Guid[] SeatIds);

    private static async Task<IResult> UpdateScheduleAsync(Guid tripId, TripSchedule schedule, HttpContext context,
        NpgsqlDataSource database, CancellationToken ct)
    {
        if (schedule.Stops is not { Length: >= 2 and <= 100 } || schedule.SeatIds is not { Length: > 0 and <= 2500 } ||
            schedule.Stops.Select(s => s.RouteStopId).Distinct().Count() != schedule.Stops.Length ||
            schedule.SeatIds.Distinct().Count() != schedule.SeatIds.Length ||
            schedule.Stops.Any(s => s.DepartureAt < s.ArrivalAt) ||
            context.User.FindFirstValue("sub") is not { } actor)
            return Results.BadRequest();

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var trip = new NpgsqlCommand("""
            SELECT route_id FROM catalog.trips WHERE id=@id FOR UPDATE
            """, connection, transaction);
        trip.Parameters.AddWithValue("id", tripId);
        if (await trip.ExecuteScalarAsync(ct) is not Guid route)
            return Results.NotFound();

        await using var verify = new NpgsqlCommand("""
            SELECT id FROM catalog.route_stops WHERE route_id=@route ORDER BY stop_order FOR SHARE
            """, connection, transaction);
        verify.Parameters.AddWithValue("route", route);
        var expected = new List<Guid>();
        await using (var rows = await verify.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
                expected.Add(rows.GetGuid(0));
        if (!expected.SequenceEqual(schedule.Stops.Select(s => s.RouteStopId)) ||
            schedule.Stops.Zip(schedule.Stops.Skip(1)).Any(pair => pair.First.DepartureAt >= pair.Second.ArrivalAt))
            return Results.BadRequest();

        await using var change = new NpgsqlCommand("""
            WITH snapshot AS MATERIALIZED (
                SELECT jsonb_build_object(
                    'stops', (SELECT jsonb_agg(to_jsonb(s) ORDER BY route_stop_id) FROM catalog.trip_stops s WHERE trip_id=@id),
                    'seatIds', (SELECT jsonb_agg(seat_id ORDER BY seat_id) FROM catalog.trip_seats WHERE trip_id=@id)
                ) AS before_state
            )
            INSERT INTO catalog.admin_audit(actor,correlation_id,resource,resource_id,operation,before_state,after_state)
            SELECT @actor,@correlation,'trip_schedule',@id,'update',before_state,CAST(@after AS jsonb) FROM snapshot;

            DELETE FROM catalog.trip_stops WHERE trip_id=@id;
            INSERT INTO catalog.trip_stops(trip_id,route_stop_id,arrival_at,departure_at)
                SELECT @id,stop,arrival,departure FROM unnest(@stops,@arrivals,@departures) AS schedule(stop,arrival,departure);

            INSERT INTO catalog.trip_seats(trip_id,seat_id) SELECT @id,unnest(@seats) ON CONFLICT DO NOTHING;
            INSERT INTO catalog.seat_projection_metadata(trip_id) VALUES(@id) ON CONFLICT DO NOTHING;
            """, connection, transaction);
        change.Parameters.AddWithValue("id", tripId);
        change.Parameters.AddWithValue("actor", actor);
        change.Parameters.AddWithValue("correlation", (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!);
        change.Parameters.AddWithValue("after", JsonSerializer.Serialize(schedule));
        change.Parameters.AddWithValue("stops", expected.ToArray());
        change.Parameters.AddWithValue("arrivals", schedule.Stops.Select(s => s.ArrivalAt.UtcDateTime).ToArray());
        change.Parameters.AddWithValue("departures", schedule.Stops.Select(s => s.DepartureAt.UtcDateTime).ToArray());
        change.Parameters.AddWithValue("seats", schedule.SeatIds);
        try
        {
            await change.ExecuteNonQueryAsync(ct);
            await transaction.CommitAsync(ct);
            return Results.NoContent();
        }
        catch (PostgresException error) when (error.SqlState == "23503") { return Results.Conflict(); }
    }

    private static async Task<IResult> ListAsync(string resource, int? offset, NpgsqlDataSource database, CancellationToken ct)
    {
        if (!Columns.ContainsKey(resource))
            return Results.NotFound();
        if (offset is < 0)
            return Results.BadRequest();
        await using var query = database.CreateCommand($"""
            SELECT coalesce(jsonb_agg(to_jsonb(rows) ORDER BY rows.id),'[]'::jsonb)::text
            FROM (SELECT * FROM catalog.{resource} ORDER BY id LIMIT 100 OFFSET @offset) rows
            """);
        query.Parameters.AddWithValue("offset", offset ?? 0);
        return Results.Content((string?)await query.ExecuteScalarAsync(ct), "application/json");
    }

    private static async Task<IResult> WriteAsync(string resource, Guid id, HttpContext context, NpgsqlDataSource database, CancellationToken ct)
    {
        if (!Columns.TryGetValue(resource, out var fields))
            return Results.NotFound();
        if (id == Guid.Empty || context.User.FindFirstValue("sub") is not { } actor)
            return Results.BadRequest();

        var delete = HttpMethods.IsDelete(context.Request.Method);
        var body = "";
        if (!delete)
        {
            try
            {
                var input = await context.Request.ReadFromJsonAsync<JsonElement>(ct);
                if (input.ValueKind != JsonValueKind.Object)
                    return Results.BadRequest();
                var names = input.EnumerateObject().Select(property => property.Name).ToArray();
                if (names.Length != fields.Length || !fields.All(names.Contains) ||
                    input.EnumerateObject().Any(property => property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                        || property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString())))
                    return Results.BadRequest();
                body = input.GetRawText();
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                return Results.BadRequest();
            }
        }

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var rowLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key,0))", connection, tx);
        rowLock.Parameters.AddWithValue("key", $"catalog:admin:{resource}:{id:D}");
        await rowLock.ExecuteNonQueryAsync(ct);

        await using var snapshot = new NpgsqlCommand($"SELECT to_jsonb(r)::text FROM catalog.{resource} r WHERE id=@id FOR UPDATE", connection, tx);
        snapshot.Parameters.AddWithValue("id", id);
        var before = (string?)await snapshot.ExecuteScalarAsync(ct);
        if (delete && before is null)
            return Results.NotFound();
        var columns = string.Join(",", fields);
        var assignments = string.Join(",", fields.Select(field => $"{field}=excluded.{field}"));
        var sql = delete ? $"DELETE FROM catalog.{resource} WHERE id=@id" : $"""
            INSERT INTO catalog.{resource} AS target(id,{columns})
            SELECT @id,{columns} FROM jsonb_populate_record(NULL::catalog.{resource},CAST(@body AS jsonb))
            ON CONFLICT(id) DO UPDATE SET {assignments} RETURNING to_jsonb(target)::text
            """;
        try
        {
            await using var write = new NpgsqlCommand(sql, connection, tx);
            write.Parameters.AddWithValue("id", id);
            if (!delete)
                write.Parameters.AddWithValue("body", body);
            string? after = null;
            if (delete)
                await write.ExecuteNonQueryAsync(ct);
            else
                after = (string?)await write.ExecuteScalarAsync(ct);

            if (before != after)
            {
                await using var audit = new NpgsqlCommand("""
                    INSERT INTO catalog.admin_audit(actor,correlation_id,resource,resource_id,operation,before_state,after_state)
                    VALUES(@actor,@correlation,@resource,@id,@operation,CAST(@before AS jsonb),CAST(@after AS jsonb))
                    """, connection, tx);
                audit.Parameters.AddWithValue("actor", actor);
                audit.Parameters.AddWithValue("correlation", (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!);
                audit.Parameters.AddWithValue("resource", resource);
                audit.Parameters.AddWithValue("id", id);
                audit.Parameters.AddWithValue("operation", delete ? "delete" : before is null ? "create" : "update");
                audit.Parameters.AddWithValue("before", NpgsqlTypes.NpgsqlDbType.Text, (object?)before ?? DBNull.Value);
                audit.Parameters.AddWithValue("after", NpgsqlTypes.NpgsqlDbType.Text, (object?)after ?? DBNull.Value);
                await audit.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return delete ? Results.NoContent() : Results.Content(after, "application/json");
        }
        catch (PostgresException error) when (error.SqlState is "23503" or "23505")
        {
            return Results.Conflict();
        }
        catch (PostgresException error) when (error.SqlState.StartsWith("22", StringComparison.Ordinal) || error.SqlState is "23502" or "23514")
        {
            return Results.BadRequest();
        }
    }
}
