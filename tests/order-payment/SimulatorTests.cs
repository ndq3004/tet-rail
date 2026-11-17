using Npgsql;
using TetRail.OrderPayment.Orders;
using TetRail.OrderPayment.Payments;
using Xunit;

namespace TetRail.OrderPayment.Tests;

public class SimulatorTests
{
    [Fact]
    public void SignatureCoversTheExactBytes()
    {
        var signer = new SimulatorSignature("test-only");
        var body = "{}"u8.ToArray();
        var signature = signer.Sign(body);
        Assert.True(signer.Verify(body, signature));
        Assert.False(signer.Verify("{ }"u8.ToArray(), signature));
        Assert.False(signer.Verify(body, new string('x', 64)));
        Assert.False(new SimulatorSignature("").Verify(body, signature));
    }

    [PostgresTheory]
    [InlineData("success", "PAID", "PaymentSucceeded.v1")]
    [InlineData("duplicate_callback", "PAID", "PaymentSucceeded.v1")]
    [InlineData("failure", "PAYMENT_FAILED", "PaymentFailed.v1")]
    [InlineData("timeout", "EXPIRED", "OrderExpired.v1")]
    [InlineData("delayed_success", "PAYMENT_REVIEW", "LatePaymentReceived.v1")]
    [InlineData("success", "PAYMENT_REVIEW", "PaymentReviewRequired.v1")]
    public async Task ScenariosSurviveRestartAndPersistOneOutcome(string scenario, string expectedState, string expectedEvent)
    {
        var databaseUrl = Environment.GetEnvironmentVariable("ORDER_TEST_DATABASE_URL");
        await using var database = NpgsqlDataSource.Create(databaseUrl!);
        await new OrderMigrations(database).RunAsync(default);
        var clock = new TestClock();
        var signature = new SimulatorSignature("test-only");
        var service = new SimulatorService(database, clock, signature);
        var order = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        await using var seed = database.CreateCommand("""
            INSERT INTO ordering.orders(id,hold_id,owner_id,idempotency_key,request_fingerprint,state,total_amount,currency,payment_expires_at)
            VALUES (@order,@hold,@owner,'test','test','PENDING_PAYMENT',100,'VND',@deadline)
            """);
        seed.Parameters.AddWithValue("order", order);
        seed.Parameters.AddWithValue("hold", Guid.NewGuid());
        seed.Parameters.AddWithValue("owner", owner);
        seed.Parameters.AddWithValue("deadline", clock.Now.AddMinutes(5));
        await seed.ExecuteNonQueryAsync();

        try
        {
            Assert.Null(await service.ScheduleAsync(order, Guid.NewGuid(), "pay", scenario, correlation, default));
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                service.ScheduleAsync(order, owner, "pay", scenario, correlation, default)));
            var attempt = Assert.IsType<SimulatorAttempt>(attempts[0]);
            Assert.All(attempts, value => Assert.Equal(attempt, value));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ScheduleAsync(order, owner, "different", scenario, correlation, default));

            clock.Now = attempt.DueAt;
            var restarted = new SimulatorService(database, clock, signature);
            if (scenario is "success" or "duplicate_callback")
            {
                var callback = new SimulatorCallback(attempt.TransactionId, order, "success");
                var replies = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => restarted.AcceptAsync(callback, default)));
                Assert.All(replies, status => Assert.Equal(204, status));
                Assert.Equal(409, await restarted.AcceptAsync(callback with { Outcome = "failure" }, default));
                Assert.Equal("PENDING_PAYMENT", await StateAsync(database, order));
                Assert.Equal(1L, await EventCountAsync(database, order, "PaymentAuthorized.v1"));
                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                    restarted.CompleteAsync(order, attempt.TransactionId, expectedState == "PAID", default)));
            }

            Assert.True(await restarted.DeliverDueAsync(default));
            if (scenario == "duplicate_callback")
                Assert.True(await restarted.DeliverDueAsync(default));
            Assert.Equal(expectedState, await StateAsync(database, order));
            Assert.Equal(1L, await EventCountAsync(database, order, expectedEvent));
            Assert.Equal(scenario is "success" or "duplicate_callback" ? 1L : 0L,
                await EventCountAsync(database, order, "PaymentAuthorized.v1"));
            await using var review = database.CreateCommand("SELECT compensation_required FROM ordering.simulator_attempts WHERE order_id=@order");
            review.Parameters.AddWithValue("order", order);
            Assert.Equal(expectedState == "PAYMENT_REVIEW", (bool)(await review.ExecuteScalarAsync())!);
        }
        finally
        {
            await using var cleanup = database.CreateCommand("""
                DELETE FROM ordering.outbox_events WHERE aggregate_id=@order;
                DELETE FROM ordering.simulator_attempts WHERE order_id=@order;
                DELETE FROM ordering.orders WHERE id=@order;
                """);
            cleanup.Parameters.AddWithValue("order", order);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string?> StateAsync(NpgsqlDataSource database, Guid order)
    {
        await using var command = database.CreateCommand("SELECT state FROM ordering.orders WHERE id=@order");
        command.Parameters.AddWithValue("order", order);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<long> EventCountAsync(NpgsqlDataSource database, Guid order, string type)
    {
        await using var command = database.CreateCommand("SELECT count(*) FROM ordering.outbox_events WHERE aggregate_id=@order AND event_type=@type");
        command.Parameters.AddWithValue("order", order);
        command.Parameters.AddWithValue("type", type);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

internal sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ORDER_TEST_DATABASE_URL")))
            Skip = "Set ORDER_TEST_DATABASE_URL to run PostgreSQL integration tests.";
    }
}
