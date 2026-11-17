using System.Text.Json;
using Confluent.Kafka;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TetRail.Ticketing;
using Xunit;

namespace TetRail.Ticketing.Tests;

[Collection("Ticket database")]
public sealed class ReportingTests
{
    [TicketDatabaseFact]
    public async Task ReportReplaysEventsWithoutDuplicatingSalesOrReleasingSoldSegments()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TICKET_TEST_DATABASE_URL")!);
        using var stream = typeof(ReportingWorker).Assembly.GetManifestResourceStream("TetRail.Ticketing.001_tickets.sql")!;
        using var reader = new StreamReader(stream);
        await using var migrate = database.CreateCommand(await reader.ReadToEndAsync());
        await migrate.ExecuteNonQueryAsync();
        var worker = new ReportingWorker(database, new ConfigurationBuilder().Build(), NullLogger<ReportingWorker>.Instance);
        var order = Guid.NewGuid();
        var trip = Guid.NewGuid();
        var seat = new { trip_id = trip, seat_id = Guid.NewGuid(), segment_indices = new[] { 1 } };
        var paid = Record("ordering.payments.v1", "PaymentSucceeded.v1", 2, new { order_id = order });
        await worker.ApplyAsync(Record("ordering.payments.v1", "OrderCreated.v1", 1, new { order_id = order, total_amount = 120, currency = "VND" }), default);
        await worker.ApplyAsync(paid, default);
        await worker.ApplyAsync(paid, default);
        await worker.ApplyAsync(Record("booking.seat-state.v1", "HoldConfirmed.v1", 2, seat), default);
        await worker.ApplyAsync(Record("booking.seat-state.v1", "HoldExpired.v1", 3, seat), default);

        var result = Assert.IsAssignableFrom<IContentTypeHttpResult>(await Reports.ReadAsync(database, default));
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        await ((IResult)result).ExecuteAsync(context);
        using var report = JsonDocument.Parse(body.ToArray());
        var root = report.RootElement;
        Assert.Equal(120, root.GetProperty("sales")[0].GetProperty("revenue").GetInt32());
        Assert.Equal(1, root.GetProperty("conversion").GetProperty("paid").GetInt32());
        Assert.Equal("SOLD", root.GetProperty("occupancy")[0].GetProperty("status").GetString());
    }

    [ReportingKafkaFact]
    public async Task LiveConsumerRecordsDeadLetterAndPartitionLag()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("REPORTING_TEST_DATABASE_URL")!);
        using var stream = typeof(ReportingWorker).Assembly.GetManifestResourceStream("TetRail.Ticketing.001_tickets.sql")!;
        using var reader = new StreamReader(stream);
        await using var migrate = database.CreateCommand(await reader.ReadToEndAsync());
        await migrate.ExecuteNonQueryAsync();
        var brokers = Environment.GetEnvironmentVariable("REPORTING_TEST_KAFKA")!;
        var topic = "reporting-test-" + Guid.NewGuid().ToString("N") + ".dlq";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = brokers }).Build();
        await admin.CreateTopicsAsync([new Confluent.Kafka.Admin.TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kafka:BootstrapServers"] = brokers,
                ["Reporting:SampleIntervalSeconds"] = "2",
                ["Reporting:Topics:0"] = topic,
                ["Reporting:ConsumerGroup"] = "reporting-test-" + Guid.NewGuid().ToString("N")
            }).Build();
            using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = brokers }).Build();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var ct = timeout.Token;
            var message = await producer.ProduceAsync(topic,
                new Message<string, string> { Key = Guid.NewGuid().ToString(), Value = "{}" }, ct);
            var eventId = new Guid(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(message.TopicPartitionOffset.ToString())).AsSpan(0, 16));
    
            using var worker = new ReportingWorker(database, config, NullLogger<ReportingWorker>.Instance);
            await worker.StartAsync(ct);
            try
            {
                await using var check = database.CreateCommand("""
                    SELECT EXISTS (SELECT 1 FROM reporting.events WHERE id=@id AND kind='ProjectionDeadLetter.v1')
                        AND EXISTS (SELECT 1 FROM reporting.streams WHERE topic=@topic AND partition_id=@partition AND lag>=0)
                    """);
                check.Parameters.AddWithValue("id", eventId);
                check.Parameters.AddWithValue("topic", message.Topic);
                check.Parameters.AddWithValue("partition", message.Partition.Value);
                while (!(bool)(await check.ExecuteScalarAsync(ct))!)
                    await Task.Delay(250, ct);
            }
            finally { await worker.StopAsync(default); }
        }
        finally { await admin.DeleteTopicsAsync([topic]); }
    }

    private static ConsumeResult<string, string> Record(string topic, string kind, long offset, object payload) => new()
    {
        Topic = topic, Offset = new Offset(offset), Partition = new Partition(0),
        Message = new() { Value = JsonSerializer.Serialize(new
        {
            event_id = Guid.NewGuid(), event_type = kind, occurred_at = DateTimeOffset.UtcNow, payload
        }) }
    };
}

internal sealed class ReportingKafkaFactAttribute : FactAttribute
{
    public ReportingKafkaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPORTING_TEST_DATABASE_URL")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("REPORTING_TEST_KAFKA")))
            Skip = "Set REPORTING_TEST_DATABASE_URL and REPORTING_TEST_KAFKA for live reporting verification.";
    }
}