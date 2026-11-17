using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TetRail.Ticketing;
using Xunit;

namespace TetRail.Ticketing.Tests;

[Collection("Ticket database")]
public sealed class TicketTests
{
    [TicketDatabaseFact]
    public async Task ReplayOwnershipAndDeliveryFailures()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("TICKET_TEST_DATABASE_URL")!);
        using var stream = typeof(TicketStore).Assembly.GetManifestResourceStream("TetRail.Ticketing.001_tickets.sql")!;
        using var reader = new StreamReader(stream);
        await using var migrate = database.CreateCommand(await reader.ReadToEndAsync());
        await migrate.ExecuteNonQueryAsync();

        var store = new TicketStore(database);
        var owner = Guid.NewGuid();
        var order = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var paid = new PaidOrder(owner, [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1)]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.IssueAsync(i % 2 == 0 ? eventId : Guid.NewGuid(), Guid.NewGuid(), order, paid, default)));

        var ticket = Assert.Single(await store.ReadAsync(order, owner, true, default));
        Assert.Empty(await store.ReadAsync(ticket.TicketId, Guid.NewGuid(), false, default));
        Assert.True(await store.ValidateAsync(ticket.QrReference, default));
        Assert.False(await store.ValidateAsync(Guid.NewGuid(), default));

        var documents = new FakeDocuments();
        var delivery = new TicketDelivery(database, documents, NullLogger<TicketDelivery>.Instance);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(await delivery.DeliverOneAsync(default));
            await using var retry = database.CreateCommand("UPDATE ticketing.jobs SET available_at=now() WHERE ticket_id=@ticket");
            retry.Parameters.AddWithValue("ticket", ticket.TicketId);
            await retry.ExecuteNonQueryAsync();
        }
        Assert.False(await delivery.DeliverOneAsync(default));
        Assert.Equal(5, documents.Calls);
        Assert.False(Assert.Single(await store.ReadAsync(order, owner, true, default)).PdfReady);

        await using var requeue = database.CreateCommand("""
            UPDATE ticketing.jobs SET dead_at=NULL,attempts=0 WHERE ticket_id=@ticket AND kind='pdf'
            """);
        requeue.Parameters.AddWithValue("ticket", ticket.TicketId);
        await requeue.ExecuteNonQueryAsync();
        documents.Fail = false;
        Assert.True(await delivery.DeliverOneAsync(default));
        await using var failNotification = database.CreateCommand("""
            CREATE FUNCTION ticketing.test_notification_failure() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'simulated delivery failure'; END; $$;
            CREATE TRIGGER test_notification_failure BEFORE INSERT ON ticketing.notifications
                FOR EACH ROW EXECUTE FUNCTION ticketing.test_notification_failure();
            """);
        await failNotification.ExecuteNonQueryAsync();
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Assert.True(await delivery.DeliverOneAsync(default));
                await using var retry = database.CreateCommand("UPDATE ticketing.jobs SET available_at=now() WHERE ticket_id=@ticket");
                retry.Parameters.AddWithValue("ticket", ticket.TicketId);
                await retry.ExecuteNonQueryAsync();
            }
            Assert.False(await delivery.DeliverOneAsync(default));
        }
        finally
        {
            await using var restore = database.CreateCommand("""
                DROP TRIGGER test_notification_failure ON ticketing.notifications;
                DROP FUNCTION ticketing.test_notification_failure();
                """);
            await restore.ExecuteNonQueryAsync();
        }
        await using var requeueNotification = database.CreateCommand("""
            UPDATE ticketing.jobs SET dead_at=NULL,attempts=0 WHERE ticket_id=@ticket AND kind='notification'
            """);
        requeueNotification.Parameters.AddWithValue("ticket", ticket.TicketId);
        await requeueNotification.ExecuteNonQueryAsync();
        Assert.True(await delivery.DeliverOneAsync(default));
        Assert.False(await delivery.DeliverOneAsync(default));
        Assert.True(Assert.Single(await store.ReadAsync(order, owner, true, default)).PdfReady);

        await using var effects = database.CreateCommand("""
            SELECT (SELECT count(*) FROM ticketing.outbox WHERE ticket_id=@ticket)
                + (SELECT count(*) FROM ticketing.notifications WHERE ticket_id=@ticket)
            """);
        effects.Parameters.AddWithValue("ticket", ticket.TicketId);
        Assert.Equal(2L, (long)(await effects.ExecuteScalarAsync())!);
    }

    private sealed class FakeDocuments : ITicketDocuments
    {
        public int Calls;
        public bool Fail = true;
        public Task UploadAsync(Ticket ticket, CancellationToken ct)
        {
            Calls++;
            return Fail ? Task.FromException(new IOException("test storage unavailable")) : Task.CompletedTask;
        }
        public Task<string> DownloadUrlAsync(Guid ticketId) => throw new NotSupportedException();
    }
}

internal sealed class TicketDatabaseFactAttribute : FactAttribute
{
    public TicketDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TICKET_TEST_DATABASE_URL")))
            Skip = "Set TICKET_TEST_DATABASE_URL to an empty PostgreSQL test database.";
    }
}
