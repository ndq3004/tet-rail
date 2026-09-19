namespace TetRail.Catalog.Migrations;

public interface ICatalogMigrationRunner
{
    Task<MigrationRunResult> RunAsync(CancellationToken cancellationToken);
}

public sealed record MigrationRunResult(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Skipped);
