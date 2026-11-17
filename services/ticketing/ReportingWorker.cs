using System.Text.Json;
using Confluent.Kafka;
using Npgsql;

namespace TetRail.Ticketing;

public sealed class ReportingWorker(NpgsqlDataSource database, IConfiguration configuration, ILogger<ReportingWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken ct) => Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
            {
                BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
                GroupId = configuration["Reporting:ConsumerGroup"] ?? "tetrail-reporting-v1", EnableAutoCommit = false, AutoOffsetReset = AutoOffsetReset.Earliest
            }).Build();
            consumer.Subscribe(configuration.GetSection("Reporting:Topics").Get<string[]>() ??
                ["ordering.payments.v1", "booking.seat-state.v1", "ticketing.events.v1", "catalog.seat-projection.v1.dlq"]);
            var nextSample = DateTimeOffset.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var record = consumer.Consume(TimeSpan.FromMilliseconds(250));
                    if (record is not null)
                    {
                        await ApplyAsync(record, ct);
                        consumer.Commit(record);
                    }
                    if (DateTimeOffset.UtcNow >= nextSample && consumer.Assignment.Count > 0)
                    {
                        await SampleAsync(consumer, ct);
                        nextSample = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, configuration.GetValue("Reporting:SampleIntervalSeconds", 30)));
                    }
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is KafkaException or NpgsqlException)
            {
                logger.LogWarning("Reporting worker will retry from its committed offsets.");
                await Task.Delay(1000, ct);
            }
            finally { consumer.Close(); }
        }
    }, ct);

    public async Task ApplyAsync(ConsumeResult<string, string> record, CancellationToken ct)
    {
        var deadLetter = record.Topic.EndsWith(".dlq", StringComparison.Ordinal);
        Guid id;
        Guid trip = default, seat = default;
        int[] segments = [];
        string reportBody;
        string kind;
        DateTimeOffset occurred;
        JsonElement payload;
        try
        {
            using var doc = JsonDocument.Parse(deadLetter ? "{}" : record.Message.Value);
            var root = doc.RootElement;
            id = deadLetter
                ? new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(record.TopicPartitionOffset.ToString())).AsSpan(0, 16))
                : root.GetProperty("event_id").GetGuid();
            kind = deadLetter ? "ProjectionDeadLetter.v1" : root.GetProperty("event_type").GetString()!;
            occurred = deadLetter ? DateTimeOffset.UtcNow : root.GetProperty("occurred_at").GetDateTimeOffset();
            payload = deadLetter ? root.Clone() : root.GetProperty("payload").Clone();
            reportBody = kind switch
            {
                "OrderCreated.v1" => JsonSerializer.Serialize(new
                {
                    order_id = payload.GetProperty("order_id").GetGuid(),
                    total_amount = payload.GetProperty("total_amount").GetDecimal(),
                    currency = payload.GetProperty("currency").GetString()
                }),
                "PaymentSucceeded.v1" => JsonSerializer.Serialize(new { order_id = payload.GetProperty("order_id").GetGuid() }),
                _ => "{}"
            };
            if (record.Topic == "booking.seat-state.v1")
            {
                trip = payload.GetProperty("trip_id").GetGuid();
                seat = payload.GetProperty("seat_id").GetGuid();
                segments = payload.GetProperty("segment_indices").EnumerateArray().Select(x => x.GetInt32()).Distinct().ToArray();
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            logger.LogWarning("Reporting skipped a malformed domain event.");
            return;
        }

        await using var connection = await database.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO reporting.events(id,kind,occurred_at,body)
            VALUES(@id,@kind,@occurred,CAST(@body AS jsonb)) ON CONFLICT DO NOTHING
            """, connection, tx);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("occurred", occurred.ToUniversalTime());
        insert.Parameters.AddWithValue("body", reportBody);
        if (await insert.ExecuteNonQueryAsync(ct) == 0)
            return;

        if (record.Topic == "booking.seat-state.v1" && kind is "SeatHeld.v1" or "HoldConfirmed.v1" or "HoldExpired.v1" or "HoldCancelled.v1")
        {
            var status = kind == "SeatHeld.v1" ? "HELD" : kind == "HoldConfirmed.v1" ? "SOLD" : "AVAILABLE";
            await using var occupancy = new NpgsqlCommand("""
                INSERT INTO reporting.occupancy(trip_id,seat_id,segment,status,source_offset)
                SELECT @trip,@seat,unnest(@segments),@status,@offset
                ON CONFLICT(trip_id,seat_id,segment) DO UPDATE SET status=excluded.status,source_offset=excluded.source_offset
                WHERE reporting.occupancy.source_offset < excluded.source_offset
                  AND (reporting.occupancy.status <> 'SOLD' OR excluded.status='SOLD')
                """, connection, tx);
            occupancy.Parameters.AddWithValue("trip", trip);
            occupancy.Parameters.AddWithValue("seat", seat);
            occupancy.Parameters.AddWithValue("segments", segments);
            occupancy.Parameters.AddWithValue("status", status);
            occupancy.Parameters.AddWithValue("offset", record.Offset.Value);
            await occupancy.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task SampleAsync(IConsumer<string, string> consumer, CancellationToken ct)
    {
        foreach (var position in consumer.Committed(consumer.Assignment, TimeSpan.FromSeconds(2)))
        {
            WatermarkOffsets watermarks;
            try { watermarks = consumer.QueryWatermarkOffsets(position.TopicPartition, TimeSpan.FromMilliseconds(200)); }
            catch (KafkaException) { continue; }
            if (watermarks.High == Offset.Unset || watermarks.Low == Offset.Unset)
                continue;
            await using var sample = database.CreateCommand("""
                INSERT INTO reporting.streams(topic,partition_id,lag) VALUES(@topic,@partition,@lag)
                ON CONFLICT(topic,partition_id) DO UPDATE SET lag=excluded.lag,sampled_at=now()
                """);
            sample.Parameters.AddWithValue("topic", position.Topic);
            sample.Parameters.AddWithValue("partition", position.Partition.Value);
            sample.Parameters.AddWithValue("lag", Math.Max(0, watermarks.High.Value - Math.Max(watermarks.Low.Value, position.Offset.Value)));
            await sample.ExecuteNonQueryAsync(ct);
        }
    }
}
