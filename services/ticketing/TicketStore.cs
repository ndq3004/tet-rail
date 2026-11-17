using System.Text.Json;
using Npgsql;

namespace TetRail.Ticketing;

public sealed record PaidOrder(Guid OwnerId, PaidItem[] Items);
public sealed record PaidItem(Guid ItemId, Guid TripId, Guid SeatId, int SegmentIndex);
public sealed record Ticket(Guid TicketId, Guid OrderId, Guid ItemId, Guid TripId, Guid SeatId,
    int SegmentIndex, Guid QrReference, DateTimeOffset IssuedAt, bool PdfReady);

public sealed class TicketStore(NpgsqlDataSource database)
{
    public async Task IssueAsync(Guid eventId, Guid correlation, Guid orderId, PaidOrder order, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var inbox = new NpgsqlCommand("INSERT INTO ticketing.inbox(id) VALUES(@id) ON CONFLICT DO NOTHING", connection, tx);
        inbox.Parameters.AddWithValue("id", eventId);
        if (await inbox.ExecuteNonQueryAsync(ct) == 0)
            return;

        foreach (var item in order.Items.OrderBy(item => item.ItemId))
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO ticketing.tickets(order_id,item_id,owner_id,trip_id,seat_id,segment_index)
                VALUES(@order,@item,@owner,@trip,@seat,@segment)
                ON CONFLICT(item_id) DO NOTHING RETURNING id
                """, connection, tx);
            insert.Parameters.AddWithValue("order", orderId);
            insert.Parameters.AddWithValue("item", item.ItemId);
            insert.Parameters.AddWithValue("owner", order.OwnerId);
            insert.Parameters.AddWithValue("trip", item.TripId);
            insert.Parameters.AddWithValue("seat", item.SeatId);
            insert.Parameters.AddWithValue("segment", item.SegmentIndex);
            if (await insert.ExecuteScalarAsync(ct) is not Guid ticket)
                continue;

            var issued = Guid.NewGuid();
            var body = JsonSerializer.Serialize(new
            {
                event_id = issued, event_type = "TicketIssued.v1", version = 1, occurred_at = DateTimeOffset.UtcNow,
                correlation_id = correlation, causation_id = eventId,
                payload = new { ticket_id = ticket, order_id = orderId, order_item_id = item.ItemId }
            });
            await using var schedule = new NpgsqlCommand("""
                INSERT INTO ticketing.jobs(ticket_id,kind) VALUES(@ticket,'pdf'),(@ticket,'notification');
                INSERT INTO ticketing.outbox(id,ticket_id,body) VALUES(@issued,@ticket,@body::jsonb);
                """, connection, tx);
            schedule.Parameters.AddWithValue("ticket", ticket);
            schedule.Parameters.AddWithValue("issued", issued);
            schedule.Parameters.AddWithValue("body", body);
            await schedule.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<List<Ticket>> ReadAsync(Guid id, Guid owner, bool byOrder, CancellationToken ct)
    {
        var column = byOrder ? "order_id" : "id";
        await using var command = database.CreateCommand($"""
            SELECT id,order_id,item_id,trip_id,seat_id,segment_index,qr_reference,issued_at,pdf_ready
            FROM ticketing.tickets WHERE {column}=@id AND owner_id=@owner ORDER BY item_id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var tickets = new List<Ticket>();
        while (await reader.ReadAsync(ct))
            tickets.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4),
                reader.GetInt32(5), reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetBoolean(8)));
        return tickets;
    }

    public async Task<bool> ValidateAsync(Guid reference, CancellationToken ct)
    {
        await using var command = database.CreateCommand("SELECT EXISTS(SELECT 1 FROM ticketing.tickets WHERE qr_reference=@reference)");
        command.Parameters.AddWithValue("reference", reference);
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }
}
