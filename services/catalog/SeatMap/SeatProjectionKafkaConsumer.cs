using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace TetRail.Catalog.SeatMap;

public sealed class SeatProjectionKafkaOptions
{
    public const string SectionName = "SeatProjectionKafka";
    public string BootstrapServers { get; init; } = "localhost:9092";
    public string Topic { get; init; } = "booking.seat-state.v1";
    public string DeadLetterTopic { get; init; } = "catalog.seat-projection.v1.dlq";
    public string ConsumerGroup { get; init; } = "catalog-seat-projection-v1";
    public int MaxDeliveryAttempts { get; init; } = 3;
    public int RetryBaseDelayMilliseconds { get; init; } = 100;
}

public sealed record KafkaSeatProjectionRecord(string Topic, int Partition, long Offset, string? Key, byte[] Value);

public interface ISeatProjectionKafkaClient : IDisposable
{
    KafkaSeatProjectionRecord? Consume(CancellationToken cancellationToken);
    Task PublishDeadLetterAsync(KafkaSeatProjectionRecord record, string reason, CancellationToken cancellationToken);
    void Commit(KafkaSeatProjectionRecord record);
}

public sealed class ConfluentSeatProjectionKafkaClient : ISeatProjectionKafkaClient
{
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly IProducer<string, byte[]> _producer;
    private readonly SeatProjectionKafkaOptions _options;

    public ConfluentSeatProjectionKafkaClient(IOptions<SeatProjectionKafkaOptions> options)
    {
        _options = options.Value;
        _consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers, GroupId = _options.ConsumerGroup,
            EnableAutoCommit = false, EnableAutoOffsetStore = false, AutoOffsetReset = AutoOffsetReset.Earliest
        }).Build();
        _consumer.Subscribe(_options.Topic);
        _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = _options.BootstrapServers, EnableIdempotence = true }).Build();
    }

    public KafkaSeatProjectionRecord? Consume(CancellationToken cancellationToken)
    {
        try
        {
            var result = _consumer.Consume(cancellationToken);
            return new KafkaSeatProjectionRecord(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value ?? Array.Empty<byte>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
    }

    public async Task PublishDeadLetterAsync(KafkaSeatProjectionRecord record, string reason, CancellationToken cancellationToken)
    {
        var headers = new Headers
        {
            { "source-topic", Encoding.UTF8.GetBytes(record.Topic) },
            { "source-partition", Encoding.UTF8.GetBytes(record.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "source-offset", Encoding.UTF8.GetBytes(record.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "failure-reason", Encoding.UTF8.GetBytes(reason) }
        };
        await _producer.ProduceAsync(_options.DeadLetterTopic, new Message<string, byte[]> { Key = record.Key ?? string.Empty, Value = record.Value, Headers = headers }, cancellationToken);
    }

    public void Commit(KafkaSeatProjectionRecord record) => _consumer.Commit(new[] { new TopicPartitionOffset(record.Topic, new Partition(record.Partition), new Offset(record.Offset + 1)) });
    public void Dispose() { _consumer.Close(); _consumer.Dispose(); _producer.Flush(TimeSpan.FromSeconds(5)); _producer.Dispose(); }
}

public sealed class SeatProjectionKafkaConsumer(
    ISeatProjectionKafkaClient client,
    SeatProjectionEventProcessor processor,
    ILogger<SeatProjectionKafkaConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Seat-projection Kafka consumer failed before offset commit.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task ConsumeOneAsync(CancellationToken cancellationToken)
    {
        var record = client.Consume(cancellationToken);
        if (record is null) return;
        await processor.ProcessAsync(record, cancellationToken);
        client.Commit(record);
    }
}

public sealed class SeatProjectionEventProcessor(
    BookingStateEventParser parser,
    ISeatProjectionEventApplier applier,
    ISeatProjectionKafkaClient client,
    IOptions<SeatProjectionKafkaOptions> options,
    ILogger<SeatProjectionEventProcessor> logger)
{
    public async Task ProcessAsync(KafkaSeatProjectionRecord record, CancellationToken cancellationToken)
    {
        BookingStateEvent stateEvent;
        try { stateEvent = parser.Parse(record); }
        catch (BookingStateEventContractException)
        {
            await client.PublishDeadLetterAsync(record, "contract-invalid", cancellationToken);
            logger.LogWarning("Sent invalid seat projection event to DLQ. Topic={Topic} Partition={Partition} Offset={Offset}", record.Topic, record.Partition, record.Offset);
            return;
        }

        var settings = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            try { await applier.ApplyAsync(stateEvent, cancellationToken); return; }
            catch (Exception exception) when (attempt < settings.MaxDeliveryAttempts && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Retrying seat projection event. Attempt={Attempt} Topic={Topic} Partition={Partition} Offset={Offset}", attempt, record.Topic, record.Partition, record.Offset);
                await Task.Delay(TimeSpan.FromMilliseconds(settings.RetryBaseDelayMilliseconds * Math.Pow(2, attempt - 1)), cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await client.PublishDeadLetterAsync(record, "apply-failed", cancellationToken);
                logger.LogError(exception, "Sent exhausted seat projection event to DLQ. Topic={Topic} Partition={Partition} Offset={Offset}", record.Topic, record.Partition, record.Offset);
                return;
            }
        }
    }
}

public sealed class BookingStateEventParser
{
    public BookingStateEvent Parse(KafkaSeatProjectionRecord record)
    {
        try
        {
            using var document = JsonDocument.Parse(record.Value);
            var root = document.RootElement;
            RequireExactProperties(root, "event_id", "event_type", "version", "occurred_at", "correlation_id", "causation_id", "payload");
            var eventType = root.GetProperty("event_type").GetString();
            if (eventType is not (BookingStateEventTypes.SeatHeld or BookingStateEventTypes.HoldExpired or BookingStateEventTypes.HoldConfirmed) || root.GetProperty("version").GetInt32() != 1)
                throw new BookingStateEventContractException();
            var payload = root.GetProperty("payload");
            RequireExactProperties(payload, "trip_id", "seat_id", "segment_indices");
            var segments = payload.GetProperty("segment_indices").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (segments.Length == 0 || segments.Any(x => x < 1) || segments.Distinct().Count() != segments.Length)
                throw new BookingStateEventContractException();
            return new BookingStateEvent(Guid.Parse(root.GetProperty("event_id").GetString()!), eventType, record.Offset,
                DateTimeOffset.Parse(root.GetProperty("occurred_at").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                Guid.Parse(root.GetProperty("correlation_id").GetString()!), Guid.Parse(payload.GetProperty("trip_id").GetString()!),
                Guid.Parse(payload.GetProperty("seat_id").GetString()!), segments);
        }
        catch (BookingStateEventContractException) { throw; }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new BookingStateEventContractException(); }
    }

    private static void RequireExactProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(names.Order()) == false)
            throw new BookingStateEventContractException();
    }
}

public sealed class BookingStateEventContractException : Exception { }
