namespace TetRail.Catalog.TripSearch;

public sealed class TripSearchOptions
{
    public const string SectionName = "TripSearch";
    public int CacheTtlSeconds { get; init; } = 60;
    public int AvailabilityStaleAfterSeconds { get; init; } = 120;
}
