namespace TetRail.Catalog.SeatMap;

public sealed class SeatMapOptions
{
    public const string SectionName = "SeatMap";
    public int CacheTtlSeconds { get; init; } = 15;
    public int StaleAfterSeconds { get; init; } = 60;
}
