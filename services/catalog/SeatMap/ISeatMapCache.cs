namespace TetRail.Catalog.SeatMap;

public interface ISeatMapCache
{
    Task<SeatMapResponse?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, SeatMapResponse response, TimeSpan ttl, CancellationToken cancellationToken);
}
