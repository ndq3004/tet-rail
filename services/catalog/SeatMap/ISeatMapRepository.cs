namespace TetRail.Catalog.SeatMap;

public interface ISeatMapRepository
{
    Task<long?> GetDataVersionAsync(Guid tripId, CancellationToken cancellationToken);
    Task<SeatMapSnapshot?> GetAsync(SeatMapQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken);
}
