using Npgsql;

namespace TetRail.Catalog.SeatMap;

public sealed class PostgresSeatProjectionEventApplier(NpgsqlDataSource dataSource) : ISeatProjectionEventApplier
{
    public async Task<ProjectionEventApplyResult> ApplyAsync(BookingStateEvent stateEvent, CancellationToken cancellationToken)
    {
        if (!IsSupported(stateEvent.EventType) || stateEvent.SourcePosition < 0 || stateEvent.SegmentIndices.Count == 0 || stateEvent.SegmentIndices.Any(segment => segment < 1))
            throw new ArgumentException("Invalid booking-state projection event.", nameof(stateEvent));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string inboxSql = """
            INSERT INTO catalog.seat_projection_inbox(event_id, source_position, event_type, applied_at)
            VALUES (@eventId, @sourcePosition, @eventType, @occurredAt) ON CONFLICT DO NOTHING;
            """;
        await using var inbox = new NpgsqlCommand(inboxSql, connection, transaction);
        inbox.Parameters.AddWithValue("eventId", stateEvent.EventId);
        inbox.Parameters.AddWithValue("sourcePosition", stateEvent.SourcePosition);
        inbox.Parameters.AddWithValue("eventType", stateEvent.EventType);
        inbox.Parameters.AddWithValue("occurredAt", stateEvent.OccurredAt);
        if (await inbox.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ProjectionEventApplyResult.Duplicate;
        }

        var desiredStatus = stateEvent.EventType == BookingStateEventTypes.SeatHeld ? "HELD"
            : stateEvent.EventType == BookingStateEventTypes.HoldConfirmed ? "SOLD" : "AVAILABLE";
        var appliedAny = false;
        foreach (var segmentIndex in stateEvent.SegmentIndices.Distinct())
        {
            const string cursorSql = """
                INSERT INTO catalog.seat_segment_projection_cursor(trip_id, seat_id, segment_index, status, source_position, updated_at)
                VALUES (@tripId, @seatId, @segmentIndex, @status, @sourcePosition, @occurredAt)
                ON CONFLICT (trip_id, seat_id, segment_index) DO UPDATE SET
                  status = EXCLUDED.status, source_position = EXCLUDED.source_position, updated_at = EXCLUDED.updated_at
                WHERE EXCLUDED.source_position > catalog.seat_segment_projection_cursor.source_position
                  AND (catalog.seat_segment_projection_cursor.status <> 'SOLD' OR EXCLUDED.status = 'SOLD')
                RETURNING status;
                """;
            await using var cursor = new NpgsqlCommand(cursorSql, connection, transaction);
            cursor.Parameters.AddWithValue("tripId", stateEvent.TripId);
            cursor.Parameters.AddWithValue("seatId", stateEvent.SeatId);
            cursor.Parameters.AddWithValue("segmentIndex", segmentIndex);
            cursor.Parameters.AddWithValue("status", desiredStatus);
            cursor.Parameters.AddWithValue("sourcePosition", stateEvent.SourcePosition);
            cursor.Parameters.AddWithValue("occurredAt", stateEvent.OccurredAt);
            var resultingStatus = await cursor.ExecuteScalarAsync(cancellationToken) as string;
            if (resultingStatus is null) continue;
            appliedAny = true;
            await ApplyProjectionRowAsync(connection, transaction, stateEvent, segmentIndex, resultingStatus, cancellationToken);
        }
        if (appliedAny)
        {
            const string metadataSql = """
                INSERT INTO catalog.seat_projection_metadata(trip_id, data_version, updated_at) VALUES (@tripId, 1, @occurredAt)
                ON CONFLICT (trip_id) DO UPDATE SET
                  data_version = catalog.seat_projection_metadata.data_version + 1,
                  updated_at = EXCLUDED.updated_at;
                """;
            await using var metadata = new NpgsqlCommand(metadataSql, connection, transaction);
            metadata.Parameters.AddWithValue("tripId", stateEvent.TripId);
            metadata.Parameters.AddWithValue("occurredAt", stateEvent.OccurredAt);
            await metadata.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return appliedAny ? ProjectionEventApplyResult.Applied : ProjectionEventApplyResult.Superseded;
    }

    private static async Task ApplyProjectionRowAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, BookingStateEvent stateEvent, int segmentIndex, string status, CancellationToken cancellationToken)
    {
        if (status == "AVAILABLE")
        {
            await using var delete = new NpgsqlCommand("DELETE FROM catalog.seat_segment_projection WHERE trip_id = @tripId AND seat_id = @seatId AND segment_index = @segmentIndex;", connection, transaction);
            delete.Parameters.AddWithValue("tripId", stateEvent.TripId); delete.Parameters.AddWithValue("seatId", stateEvent.SeatId); delete.Parameters.AddWithValue("segmentIndex", segmentIndex);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }
        const string upsertSql = """
            INSERT INTO catalog.seat_segment_projection(trip_id, seat_id, segment_index, status, source_event_id, source_position, projection_version, updated_at)
            VALUES (@tripId, @seatId, @segmentIndex, @status, @eventId, @sourcePosition, @sourcePosition + 1, @occurredAt)
            ON CONFLICT (trip_id, seat_id, segment_index) DO UPDATE SET status = EXCLUDED.status, source_event_id = EXCLUDED.source_event_id, source_position = EXCLUDED.source_position, projection_version = EXCLUDED.projection_version, updated_at = EXCLUDED.updated_at;
            """;
        await using var upsert = new NpgsqlCommand(upsertSql, connection, transaction);
        upsert.Parameters.AddWithValue("tripId", stateEvent.TripId); upsert.Parameters.AddWithValue("seatId", stateEvent.SeatId); upsert.Parameters.AddWithValue("segmentIndex", segmentIndex);
        upsert.Parameters.AddWithValue("status", status); upsert.Parameters.AddWithValue("eventId", stateEvent.EventId); upsert.Parameters.AddWithValue("sourcePosition", stateEvent.SourcePosition); upsert.Parameters.AddWithValue("occurredAt", stateEvent.OccurredAt);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsSupported(string eventType) => eventType is BookingStateEventTypes.SeatHeld or BookingStateEventTypes.HoldExpired or BookingStateEventTypes.HoldConfirmed;
}
