using Npgsql;

namespace TetRail.Ticketing;

public sealed class TicketDelivery(NpgsqlDataSource database, ITicketDocuments documents, ILogger<TicketDelivery> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await DeliverOneAsync(ct))
                    continue;
            }
            catch (NpgsqlException)
            {
                logger.LogWarning("Ticket delivery database operation will retry.");
            }
            await Task.Delay(500, ct);
        }
    }

    public async Task<bool> DeliverOneAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Ticket ticket;
        string kind;
        await using (var claim = new NpgsqlCommand("""
            SELECT t.id,t.order_id,t.item_id,t.trip_id,t.seat_id,t.segment_index,t.qr_reference,t.issued_at,t.pdf_ready,j.kind
            FROM ticketing.jobs j JOIN ticketing.tickets t ON t.id=j.ticket_id
            WHERE j.completed_at IS NULL AND j.dead_at IS NULL AND j.available_at<=now()
                AND (j.kind='pdf' OR t.pdf_ready)
            ORDER BY j.available_at,j.ticket_id LIMIT 1 FOR UPDATE OF j SKIP LOCKED
            """, connection, tx))
        await using (var reader = await claim.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            ticket = new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                reader.GetInt32(5), reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8));
            kind = reader.GetString(9);
        }

        if (kind == "pdf")
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await documents.UploadAsync(ticket, timeout.Token);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
            {
                await RecordFailureAsync(connection, tx, ticket.TicketId, kind, ct);
                logger.LogWarning("Ticket PDF delivery failed and was recorded for retry or dead-letter review.");
                return true;
            }
        }

        if (kind == "notification")
        {
            await tx.SaveAsync("notification", ct);
            try
            {
                await using var send = new NpgsqlCommand("""
                    INSERT INTO ticketing.notifications(ticket_id) VALUES(@ticket) ON CONFLICT DO NOTHING
                    """, connection, tx);
                send.Parameters.AddWithValue("ticket", ticket.TicketId);
                await send.ExecuteNonQueryAsync(ct);
            }
            catch (PostgresException error) when (error.SqlState.StartsWith("23", StringComparison.Ordinal) ||
                error.SqlState == "P0001")
            {
                await tx.RollbackAsync("notification", ct);
                await RecordFailureAsync(connection, tx, ticket.TicketId, kind, ct);
                logger.LogWarning("Simulated ticket delivery failed and was recorded for retry or dead-letter review.");
                return true;
            }
        }

        var effect = kind == "pdf" ? "UPDATE ticketing.tickets SET pdf_ready=true WHERE id=@ticket;" : "";
        await using var complete = new NpgsqlCommand(effect + """
            UPDATE ticketing.jobs SET completed_at=now() WHERE ticket_id=@ticket AND kind=@kind;
            """, connection, tx);
        complete.Parameters.AddWithValue("ticket", ticket.TicketId);
        complete.Parameters.AddWithValue("kind", kind);
        await complete.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task RecordFailureAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid ticket, string kind, CancellationToken ct)
    {
        await using var failure = new NpgsqlCommand("""
            UPDATE ticketing.jobs SET attempts=attempts+1,
                available_at=now() + (interval '1 second' * power(2,attempts)),
                dead_at=CASE WHEN attempts+1>=5 THEN now() END
            WHERE ticket_id=@ticket AND kind=@kind
            """, connection, tx);
        failure.Parameters.AddWithValue("ticket", ticket);
        failure.Parameters.AddWithValue("kind", kind);
        await failure.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
