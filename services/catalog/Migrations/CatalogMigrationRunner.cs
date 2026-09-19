using System.Reflection;
using Npgsql;

namespace TetRail.Catalog.Migrations;

public sealed class CatalogMigrationRunner(NpgsqlDataSource dataSource, ILogger<CatalogMigrationRunner> logger) : ICatalogMigrationRunner
{
    private const long AdvisoryLockId = 7_254_017;
    private const string ResourcePrefix = "TetRail.Catalog.Migrations.";

    public async Task<MigrationRunResult> RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureHistoryTableAsync(connection, cancellationToken);

        var applied = new List<string>();
        var skipped = new List<string>();
        foreach (var resourceName in GetMigrationResourceNames())
        {
            var migrationName = resourceName[ResourcePrefix.Length..];
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await AcquireLockAsync(connection, transaction, cancellationToken);

            if (await IsAppliedAsync(connection, transaction, migrationName, cancellationToken))
            {
                skipped.Add(migrationName);
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            var sql = ReadEmbeddedMigration(resourceName);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await RecordAppliedAsync(connection, transaction, migrationName, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            applied.Add(migrationName);
            logger.LogInformation("Applied Catalog migration {MigrationName}", migrationName);
        }

        return new MigrationRunResult(applied, skipped);
    }

    private static async Task EnsureHistoryTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS catalog;
            CREATE TABLE IF NOT EXISTS catalog.schema_migrations (
                name varchar(255) PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lockId);", connection, transaction);
        command.Parameters.AddWithValue("lockId", AdvisoryLockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string migrationName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM catalog.schema_migrations WHERE name = @name);", connection, transaction);
        command.Parameters.AddWithValue("name", migrationName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task RecordAppliedAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string migrationName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("INSERT INTO catalog.schema_migrations (name) VALUES (@name);", connection, transaction);
        command.Parameters.AddWithValue("name", migrationName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IReadOnlyList<string> GetMigrationResourceNames() =>
        typeof(CatalogMigrationRunner).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string ReadEmbeddedMigration(string resourceName)
    {
        var assembly = typeof(CatalogMigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Migration resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
