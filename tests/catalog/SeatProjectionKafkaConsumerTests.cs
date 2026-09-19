using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TetRail.Catalog.SeatMap;
using Xunit;

namespace TetRail.Catalog.Tests;

public sealed class SeatProjectionKafkaConsumerTests
{
    [Fact]
    public async Task ConsumeOneAsync_applies_then_commits_offset()
    {
        var client = new FakeKafkaClient(ValidRecord());
        var applier = new FakeApplier(client.Operations);
        var consumer = CreateConsumer(client, applier);

        await consumer.ConsumeOneAsync(CancellationToken.None);

        Assert.Equal(["apply", "commit"], client.Operations);
    }

    [Fact]
    public async Task ConsumeOneAsync_sends_invalid_contract_to_dlq_before_commit()
    {
        var client = new FakeKafkaClient(new KafkaSeatProjectionRecord("booking.seat-state.v1", 2, 11, "key", "{}"u8.ToArray()));
        var consumer = CreateConsumer(client, new FakeApplier(client.Operations));

        await consumer.ConsumeOneAsync(CancellationToken.None);

        Assert.Equal(["dlq:contract-invalid", "commit"], client.Operations);
    }

    [Fact]
    public async Task ConsumeOneAsync_retries_then_dlqs_before_commit()
    {
        var client = new FakeKafkaClient(ValidRecord());
        var consumer = CreateConsumer(client, new FakeApplier(client.Operations, throwOnApply: true));

        await consumer.ConsumeOneAsync(CancellationToken.None);

        Assert.Equal(["apply", "apply", "dlq:apply-failed", "commit"], client.Operations);
    }

    private static SeatProjectionKafkaConsumer CreateConsumer(FakeKafkaClient client, ISeatProjectionEventApplier applier)
    {
        var options = Options.Create(new SeatProjectionKafkaOptions { MaxDeliveryAttempts = 2, RetryBaseDelayMilliseconds = 1 });
        var processor = new SeatProjectionEventProcessor(new BookingStateEventParser(), applier, client, options, NullLogger<SeatProjectionEventProcessor>.Instance);
        return new SeatProjectionKafkaConsumer(client, processor, NullLogger<SeatProjectionKafkaConsumer>.Instance);
    }

    private static KafkaSeatProjectionRecord ValidRecord() => new("booking.seat-state.v1", 2, 11, "trip:seat", """
        {"event_id":"90000000-0000-0000-0000-000000000001","event_type":"SeatHeld.v1","version":1,"occurred_at":"2026-09-19T12:00:00Z","correlation_id":"90000000-0000-0000-0000-000000000002","causation_id":"90000000-0000-0000-0000-000000000003","payload":{"trip_id":"40000000-0000-0000-0000-000000000001","seat_id":"70000000-0000-0000-0000-000000000001","segment_indices":[1,2]}}
        """u8.ToArray());

    private sealed class FakeKafkaClient(KafkaSeatProjectionRecord record) : ISeatProjectionKafkaClient
    {
        private KafkaSeatProjectionRecord? _record = record;
        public List<string> Operations { get; } = [];
        public KafkaSeatProjectionRecord? Consume(CancellationToken cancellationToken) { var result = _record; _record = null; return result; }
        public Task PublishDeadLetterAsync(KafkaSeatProjectionRecord record, string reason, CancellationToken cancellationToken) { Operations.Add($"dlq:{reason}"); return Task.CompletedTask; }
        public void Commit(KafkaSeatProjectionRecord record) => Operations.Add("commit");
        public void Dispose() { }
    }

    private sealed class FakeApplier(List<string> operations, bool throwOnApply = false) : ISeatProjectionEventApplier
    {
        public Task<ProjectionEventApplyResult> ApplyAsync(BookingStateEvent stateEvent, CancellationToken cancellationToken)
        {
            operations.Add("apply");
            if (throwOnApply) throw new InvalidOperationException("db unavailable");
            return Task.FromResult(ProjectionEventApplyResult.Applied);
        }
    }
}
