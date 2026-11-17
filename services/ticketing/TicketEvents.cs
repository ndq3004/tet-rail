using System.Net;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace TetRail.Ticketing;

public sealed class TicketEvents(TicketStore tickets, NpgsqlDataSource database, IHttpClientFactory clients,
    IConfiguration configuration, ILogger<TicketEvents> logger) : BackgroundService
{
    private string Brokers => configuration["Kafka:BootstrapServers"] ?? "localhost:9092";

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(Task.Run(() => ConsumeAsync(ct), ct), PublishAsync(ct));

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = Brokers, GroupId = "ticketing-payment-v1",
                AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false
            }).Build();
            consumer.Subscribe("ordering.payments.v1");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var record = consumer.Consume(ct);
                    await ProcessAsync(record.Message.Value, ct);
                    consumer.Commit(record);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is KafkaException or NpgsqlException or HttpRequestException or OperationCanceledException)
            {
                logger.LogWarning("Ticket payment consumer will retry from the committed offset.");
                await Task.Delay(1000, ct);
            }
            finally { consumer.Close(); }
        }
    }

    public async Task ProcessAsync(string body, CancellationToken ct)
    {
        Guid eventId, correlation, orderId;
        try
        {
            using var document = JsonDocument.Parse(body);
            var envelope = document.RootElement;
            if (envelope.GetProperty("event_type").GetString() != "PaymentSucceeded.v1" ||
                envelope.GetProperty("version").GetInt32() != 1)
                return;
            eventId = envelope.GetProperty("event_id").GetGuid();
            correlation = envelope.GetProperty("correlation_id").GetGuid();
            orderId = envelope.GetProperty("payload").GetProperty("order_id").GetGuid();
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            logger.LogWarning("Ticketing ignored an invalid payment event.");
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/v1/paid-orders/{orderId:D}/items");
        request.Headers.Add("X-Correlation-Id", correlation.ToString("D"));
        using var response = await clients.CreateClient("ordering").SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Ticketing ignored a payment event without a matching paid order.");
            return;
        }
        response.EnsureSuccessStatusCode();
        PaidOrder? order;
        try { order = await response.Content.ReadFromJsonAsync<PaidOrder>(ct); }
        catch (JsonException error) { throw new HttpRequestException("Invalid paid order snapshot.", error); }
        if (order is null || order.OwnerId == Guid.Empty || order.Items is not { Length: > 0 })
            throw new HttpRequestException("Paid order snapshot is incomplete.");
        await tickets.IssueAsync(eventId, correlation, orderId, order, ct);
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = Brokers, Acks = Acks.All, EnableIdempotence = true, MessageTimeoutMs = 5000
        }).Build();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PublishOneAsync(producer, ct))
                    continue;
            }
            catch (Exception error) when (error is NpgsqlException or KafkaException)
            {
                logger.LogWarning("Ticket outbox publication will retry.");
            }
            await Task.Delay(500, ct);
        }
    }

    private async Task<bool> PublishOneAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        Guid id;
        Message<string, string> message;
        await using (var select = new NpgsqlCommand("""
            SELECT id,ticket_id,body::text FROM ticketing.outbox WHERE published_at IS NULL
            ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, tx))
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            id = reader.GetGuid(0);
            message = new() { Key = reader.GetGuid(1).ToString("D"), Value = reader.GetString(2) };
        }
        await producer.ProduceAsync("ticketing.events.v1", message, ct);
        await using var mark = new NpgsqlCommand("UPDATE ticketing.outbox SET published_at=now() WHERE id=@id", connection, tx);
        mark.Parameters.AddWithValue("id", id);
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }
}
