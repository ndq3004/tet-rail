using Npgsql;

namespace TetRail.OrderPayment.Orders;

public sealed class OrderMigrations(NpgsqlDataSource dataSource)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('ordering:migrations',0));", connection, transaction))
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        await using (var history = new NpgsqlCommand("CREATE SCHEMA IF NOT EXISTS ordering; CREATE TABLE IF NOT EXISTS ordering.schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());", connection, transaction))
            await history.ExecuteNonQueryAsync(cancellationToken);
        var assembly = typeof(OrderMigrations).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)).Order())
        {
            var migration = name[(name.LastIndexOf(".Migrations.", StringComparison.Ordinal) + 12)..];
            await using var alreadyApplied = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM ordering.schema_migrations WHERE name=@name);", connection, transaction);
            alreadyApplied.Parameters.AddWithValue("name", migration);
            if ((bool)(await alreadyApplied.ExecuteScalarAsync(cancellationToken))!) continue;
            await using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing migration {name}.");
            using var reader = new StreamReader(stream);
            await using (var apply = new NpgsqlCommand(await reader.ReadToEndAsync(cancellationToken), connection, transaction))
                await apply.ExecuteNonQueryAsync(cancellationToken);
            await using var record = new NpgsqlCommand("INSERT INTO ordering.schema_migrations(name) VALUES(@name);", connection, transaction);
            record.Parameters.AddWithValue("name", migration);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
