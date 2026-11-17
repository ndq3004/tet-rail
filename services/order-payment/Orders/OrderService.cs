using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace TetRail.OrderPayment.Orders;

public sealed class OrderService(NpgsqlDataSource dataSource, IHttpClientFactory clients, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OrderResponse?> CreateAsync(CreateOrderRequest request, Guid ownerId, string idempotencyKey, Guid correlationId, CancellationToken cancellationToken)
    {
        if (request.HoldId == Guid.Empty || request.Passengers is not { Count: > 0 } || request.Passengers.Count > 4 ||
            request.Fare is null || request.Fare.Price < 0 || string.IsNullOrWhiteSpace(request.Fare.Currency) || request.Fare.Currency.Length != 3 ||
            request.Passengers.Any(p => p.PassengerId == Guid.Empty || string.IsNullOrWhiteSpace(p.FullName) || p.FullName.Length > 200))
            throw new ArgumentException("Invalid order request.");

        var fingerprint = Fingerprint(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await FindByIdempotencyAsync(connection, transaction, ownerId, idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Fingerprint != fingerprint) throw new InvalidOperationException("Idempotency key was used with a different request.");
            await transaction.CommitAsync(cancellationToken);
            return existing.Value.Response;
        }

        var hold = await GetHoldAsync(request.HoldId, ownerId, correlationId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (hold is null || hold.State is not ("ACTIVE" or "HELD") || hold.ExpiresAt <= now || hold.Segments.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (request.Passengers.Count != hold.Segments.Count)
            throw new ArgumentException("A passenger is required for each held segment.");

        var total = checked(request.Fare.Price * hold.Segments.Count);
        var order = new OrderResponse(Guid.NewGuid(), "PENDING_PAYMENT", now.AddMinutes(15), total, request.Fare.Currency.ToUpperInvariant());
        await InsertOrderAsync(connection, transaction, order, request, ownerId, idempotencyKey, fingerprint, hold, correlationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return order;
    }

    public async Task<OrderResponse?> GetAsync(Guid id, Guid ownerId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id,state,payment_expires_at,total_amount,currency FROM ordering.orders WHERE id=@id AND owner_id=@owner;");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new OrderResponse(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetDecimal(3), reader.GetString(4))
            : null;
    }

    private async Task<BookingHold?> GetHoldAsync(Guid holdId, Guid ownerId, Guid correlationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/holds/{holdId:D}");
        request.Headers.TryAddWithoutValidation("X-User-Id", ownerId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", correlationId.ToString("D"));
        using var response = await clients.CreateClient("booking").SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BookingHold>(JsonOptions, cancellationToken);
    }

    private static async Task InsertOrderAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, OrderResponse order, CreateOrderRequest request, Guid ownerId, string key, string fingerprint, BookingHold hold, Guid correlationId, CancellationToken ct)
    {
        const string orderSql = "INSERT INTO ordering.orders(id,hold_id,owner_id,idempotency_key,request_fingerprint,state,total_amount,currency,payment_expires_at) VALUES(@id,@hold,@owner,@key,@fingerprint,@state,@total,@currency,@expires);";
        await using (var command = new NpgsqlCommand(orderSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", order.OrderId); command.Parameters.AddWithValue("hold", request.HoldId); command.Parameters.AddWithValue("owner", ownerId);
            command.Parameters.AddWithValue("key", key); command.Parameters.AddWithValue("fingerprint", fingerprint); command.Parameters.AddWithValue("state", order.State);
            command.Parameters.AddWithValue("total", order.TotalAmount); command.Parameters.AddWithValue("currency", order.Currency); command.Parameters.AddWithValue("expires", order.PaymentExpiresAt);
            await command.ExecuteNonQueryAsync(ct);
        }
        for (var index = 0; index < hold.Segments.Count; index++)
        {
            var segment = hold.Segments[index];
            await using var item = new NpgsqlCommand("INSERT INTO ordering.order_items(id,order_id,trip_id,seat_id,segment_index,passenger_snapshot,fare_snapshot) VALUES(@id,@order,@trip,@seat,@segment,@passenger::jsonb,@fare::jsonb);", connection, transaction);
            item.Parameters.AddWithValue("id", Guid.NewGuid()); item.Parameters.AddWithValue("order", order.OrderId); item.Parameters.AddWithValue("trip", Guid.Parse(segment.TripId)); item.Parameters.AddWithValue("seat", Guid.Parse(segment.SeatId)); item.Parameters.AddWithValue("segment", segment.SegmentIndex);
            item.Parameters.AddWithValue("passenger", JsonSerializer.Serialize(request.Passengers[index], JsonOptions)); item.Parameters.AddWithValue("fare", JsonSerializer.Serialize(request.Fare, JsonOptions));
            await item.ExecuteNonQueryAsync(ct);
        }
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { event_id = eventId, event_type = "OrderCreated.v1", version = 1, occurred_at = DateTimeOffset.UtcNow, correlation_id = correlationId, causation_id = request.HoldId, payload = new { order_id = order.OrderId, hold_id = request.HoldId, owner_id = ownerId, total_amount = order.TotalAmount, currency = order.Currency } }, JsonOptions);
        await using var outbox = new NpgsqlCommand("INSERT INTO ordering.outbox_events(id,event_type,aggregate_id,correlation_id,payload) VALUES(@id,'OrderCreated.v1',@order,@correlation,@payload::jsonb);", connection, transaction);
        outbox.Parameters.AddWithValue("id", eventId); outbox.Parameters.AddWithValue("order", order.OrderId); outbox.Parameters.AddWithValue("correlation", correlationId); outbox.Parameters.AddWithValue("payload", payload);
        await outbox.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string Fingerprint, OrderResponse Response)?> FindByIdempotencyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid ownerId, string key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT request_fingerprint,id,state,payment_expires_at,total_amount,currency FROM ordering.orders WHERE owner_id=@owner AND idempotency_key=@key FOR UPDATE;", connection, transaction);
        command.Parameters.AddWithValue("owner", ownerId); command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), new OrderResponse(reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetDecimal(4), reader.GetString(5))) : null;
    }

    private static string Fingerprint(CreateOrderRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonOptions))));
}
