using System.Text.Json;
using Npgsql;

namespace TetRail.OrderPayment.Payments;

public sealed class SimulatorService(NpgsqlDataSource dataSource, TimeProvider clock, SimulatorSignature signature)
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public async Task<SimulatorAttempt?> ScheduleAsync(Guid order, Guid owner, string key, string scenario, Guid correlation, CancellationToken ct)
    {
        if (scenario is not ("success" or "failure" or "timeout" or "delayed_success" or "duplicate_callback"))
            throw new ArgumentException("Unknown simulator scenario.");

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT state, payment_expires_at FROM ordering.orders
            WHERE id=@order AND owner_id=@owner FOR UPDATE
            """, connection, tx);
        find.Parameters.AddWithValue("order", order);
        find.Parameters.AddWithValue("owner", owner);
        string state;
        DateTimeOffset deadline;
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return null;
            state = reader.GetString(0);
            deadline = reader.GetFieldValue<DateTimeOffset>(1);
        }

        await using var existing = new NpgsqlCommand("""
            SELECT transaction_id, due_at, scenario, idempotency_key
            FROM ordering.simulator_attempts WHERE order_id=@order
            """, connection, tx);
        existing.Parameters.AddWithValue("order", order);
        await using (var reader = await existing.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (reader.GetString(2) != scenario || reader.GetString(3) != key)
                    throw new InvalidOperationException("This order already has a simulator attempt.");
                return new SimulatorAttempt(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), scenario);
            }
        }

        var now = clock.GetUtcNow();
        if (state != "PENDING_PAYMENT" || deadline <= now)
            throw new InvalidOperationException("The order is no longer payable.");

        var due = scenario switch
        {
            "timeout" => deadline,
            "delayed_success" => deadline.AddSeconds(1),
            _ => now
        };
        var attempt = new SimulatorAttempt(Guid.NewGuid(), due, scenario);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO ordering.simulator_attempts
            (order_id, transaction_id, idempotency_key, scenario, due_at, correlation_id, deliveries_remaining)
            VALUES (@order,@transaction,@key,@scenario,@due,@correlation,@deliveries)
            RETURNING due_at
            """, connection, tx);
        insert.Parameters.AddWithValue("order", order);
        insert.Parameters.AddWithValue("transaction", attempt.TransactionId);
        insert.Parameters.AddWithValue("key", key);
        insert.Parameters.AddWithValue("scenario", scenario);
        insert.Parameters.AddWithValue("due", due);
        insert.Parameters.AddWithValue("correlation", correlation);
        insert.Parameters.AddWithValue("deliveries", (short)(scenario == "duplicate_callback" ? 2 : 1));
        var storedDue = (DateTime)(await insert.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return attempt with { DueAt = new DateTimeOffset(storedDue) };
    }

    public async Task<int> AcceptAsync(SimulatorCallback callback, CancellationToken ct)
    {
        if (callback.OrderId == Guid.Empty || callback.TransactionId == Guid.Empty || callback.Outcome is not ("success" or "failure"))
            return 400;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var attempt = await ReadAttemptAsync(connection, tx, "a.transaction_id=@transaction AND a.order_id=@order",
            [new("transaction", callback.TransactionId), new("order", callback.OrderId)], ct);
        if (attempt is null)
            return 404;
        var status = await ApplyAsync(connection, tx, attempt, callback.Outcome, ct);
        await tx.CommitAsync(ct);
        return status;
    }

    public async Task CompleteAsync(Guid order, Guid providerTransaction, bool confirmed, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var attempt = await ReadAttemptAsync(connection, tx, "a.transaction_id=@transaction AND a.order_id=@order",
            [new("transaction", providerTransaction), new("order", order)], ct);
        if (attempt is not { State: "PENDING_PAYMENT", Outcome: "success" })
            return;

        await RecordAsync(connection, tx, attempt, confirmed ? "PAID" : "PAYMENT_REVIEW",
            confirmed ? "PaymentSucceeded.v1" : "PaymentReviewRequired.v1", clock.GetUtcNow(), ct);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> DeliverDueAsync(CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var attempt = await ReadAttemptAsync(connection, tx, "a.deliveries_remaining > 0 AND a.due_at <= @now",
            [new("now", clock.GetUtcNow())], ct, dueOnly: true);
        if (attempt is null)
            return false;

        if (attempt.Scenario == "timeout")
        {
            if (attempt.State == "PENDING_PAYMENT" && attempt.Outcome is null)
                await RecordAsync(connection, tx, attempt, "EXPIRED", "OrderExpired.v1", clock.GetUtcNow(), ct);
        }
        else
        {
            var callback = new SimulatorCallback(attempt.Transaction, attempt.Order, attempt.Scenario == "failure" ? "failure" : "success");
            var body = JsonSerializer.SerializeToUtf8Bytes(callback, WebJson);
            var delivered = signature.ReadCallback(body, signature.Sign(body));
            await ApplyAsync(connection, tx, attempt, delivered!.Outcome, ct);
        }

        await using var update = new NpgsqlCommand("""
            UPDATE ordering.simulator_attempts SET deliveries_remaining=deliveries_remaining-1 WHERE order_id=@order
            """, connection, tx);
        update.Parameters.AddWithValue("order", attempt.Order);
        await update.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<int> ApplyAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Attempt attempt, string outcome, CancellationToken ct)
    {
        if (attempt.Outcome is not null)
            return attempt.Outcome == outcome ? 204 : 409;

        var now = clock.GetUtcNow();
        if (attempt.Scenario == "delayed_success" && now < attempt.DueAt)
            return 409;

        await using var update = new NpgsqlCommand("""
            UPDATE ordering.simulator_attempts
            SET callback_outcome=@outcome, callback_received_at=@now WHERE order_id=@order
            """, connection, tx);
        update.Parameters.AddWithValue("outcome", outcome);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("order", attempt.Order);
        await update.ExecuteNonQueryAsync(ct);

        if (outcome == "success")
        {
            if (now >= attempt.Deadline || attempt.State != "PENDING_PAYMENT")
                await RecordAsync(connection, tx, attempt, "PAYMENT_REVIEW", "LatePaymentReceived.v1", now, ct);
            else
                await RecordAsync(connection, tx, attempt, "PENDING_PAYMENT", "PaymentAuthorized.v1", now, ct);
        }
        else if (attempt.State == "PENDING_PAYMENT")
        {
            var expired = now >= attempt.Deadline;
            await RecordAsync(connection, tx, attempt, expired ? "EXPIRED" : "PAYMENT_FAILED",
                expired ? "OrderExpired.v1" : "PaymentFailed.v1", now, ct);
        }
        return 204;
    }

    private static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Attempt attempt, string state,
        string eventType, DateTimeOffset now, CancellationToken ct)
    {
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            event_id = eventId, event_type = eventType, version = 1, occurred_at = now,
            correlation_id = attempt.Correlation, causation_id = attempt.Transaction,
            payload = new { order_id = attempt.Order, hold_id = attempt.Hold, owner_id = attempt.Owner,
                provider = "simulator", provider_transaction_id = attempt.Transaction, payment_expires_at = attempt.Deadline,
                compensation_required = state == "PAYMENT_REVIEW" }
        });
        await using var command = new NpgsqlCommand("""
            UPDATE ordering.orders SET state=@state WHERE id=@order;
            UPDATE ordering.simulator_attempts SET compensation_required=true
            WHERE order_id=@order AND @state='PAYMENT_REVIEW';
            INSERT INTO ordering.outbox_events(id,event_type,aggregate_id,correlation_id,payload)
            VALUES(@id,@event,@order,@correlation,@payload::jsonb);
            """, connection, tx);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("order", attempt.Order);
        command.Parameters.AddWithValue("id", eventId);
        command.Parameters.AddWithValue("event", eventType);
        command.Parameters.AddWithValue("correlation", attempt.Correlation);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Attempt?> ReadAttemptAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
        string predicate, NpgsqlParameter[] parameters, CancellationToken ct, bool dueOnly = false)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT a.order_id,a.transaction_id,a.scenario,a.correlation_id,a.callback_outcome,
                   o.state,o.payment_expires_at,o.hold_id,o.owner_id,a.due_at
            FROM ordering.simulator_attempts a JOIN ordering.orders o ON o.id=a.order_id
            WHERE {predicate}
            ORDER BY a.due_at,a.order_id LIMIT 1 FOR UPDATE OF o
            """, connection, tx);
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var attempt = new Attempt(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetGuid(7), reader.GetGuid(8), reader.GetFieldValue<DateTimeOffset>(9));
        await reader.CloseAsync();

        // Refresh after acquiring the order lock. A competing callback can have updated the
        // attempt while this SELECT waited on the order row.
        await using var refresh = new NpgsqlCommand("""
            SELECT callback_outcome, deliveries_remaining FROM ordering.simulator_attempts WHERE order_id=@order
            """, connection, tx);
        refresh.Parameters.AddWithValue("order", attempt.Order);
        await using var current = await refresh.ExecuteReaderAsync(ct);
        await current.ReadAsync(ct);
        if (dueOnly && current.GetInt16(1) == 0)
            return null;
        return attempt with { Outcome = current.IsDBNull(0) ? null : current.GetString(0) };
    }

    private sealed record Attempt(Guid Order, Guid Transaction, string Scenario, Guid Correlation, string? Outcome,
        string State, DateTimeOffset Deadline, Guid Hold, Guid Owner, DateTimeOffset DueAt);
}
