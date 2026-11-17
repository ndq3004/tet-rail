using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace TetRail.OrderPayment.Payments;

public sealed class PaymentWorker(
    SimulatorService simulator, SimulatorSignature signature, NpgsqlDataSource database,
    IConfiguration configuration, ILogger<PaymentWorker> logger) : BackgroundService
{
    public const string Topic = "ordering.payments.v1";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunDeliveryAsync(stoppingToken), PublishAsync(stoppingToken),
        Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken));

    private async Task RunDeliveryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (signature.IsConfigured && await simulator.DeliverDueAsync(ct))
                    continue;
            }
            catch (NpgsqlException)
            {
                logger.LogWarning("Simulator delivery will retry.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = Brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
            MessageTimeoutMs = 5000
        }).Build();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await PublishOneAsync(producer, ct))
                    continue;
            }
            catch (Exception exception) when (exception is NpgsqlException or KafkaException)
            {
                logger.LogWarning("Order outbox publication will retry.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private async Task<bool> PublishOneAsync(IProducer<string, string> producer, CancellationToken ct)
    {
        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var select = new NpgsqlCommand("""
            SELECT id,aggregate_id,payload::text FROM ordering.outbox_events
            WHERE published_at IS NULL ORDER BY occurred_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, connection, tx);
        Guid id;
        Message<string, string> message;
        await using (var reader = await select.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
                return false;
            id = reader.GetGuid(0);
            message = new() { Key = reader.GetGuid(1).ToString("D"), Value = reader.GetString(2) };
        }

        await producer.ProduceAsync(Topic, message, ct);
        await using var mark = new NpgsqlCommand("UPDATE ordering.outbox_events SET published_at=now() WHERE id=@id", connection, tx);
        mark.Parameters.AddWithValue("id", id);
        await mark.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = Brokers,
                GroupId = "ordering-payment-results-v1",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false
            }).Build();
            consumer.Subscribe(Topic);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = consumer.Consume(ct);
                    using var document = JsonDocument.Parse(message.Message.Value);
                    var envelope = document.RootElement;
                    var type = envelope.GetProperty("event_type").GetString();
                    if (type is "BookingConfirmed.v1" or "BookingConfirmationFailed.v1")
                    {
                        var payload = envelope.GetProperty("payload");
                        await simulator.CompleteAsync(payload.GetProperty("order_id").GetGuid(),
                            payload.GetProperty("provider_transaction_id").GetGuid(), type == "BookingConfirmed.v1", ct);
                    }
                    consumer.Commit(message);
                }
            }
            catch (Exception exception) when (exception is KafkaException or NpgsqlException)
            {
                logger.LogWarning("Payment result consumption will retry.");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            finally
            {
                consumer.Close();
            }
        }
    }

    private string Brokers => configuration["Kafka:BootstrapServers"] ?? "localhost:9092";
}
