namespace TetRail.Catalog.SeatMap;

public interface ISeatMapRepository
{
    Task<SeatMapSnapshot?> GetAsync(SeatMapQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken);
}
