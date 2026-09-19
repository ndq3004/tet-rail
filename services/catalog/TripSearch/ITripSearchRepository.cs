namespace TetRail.Catalog.TripSearch;

public interface ITripSearchRepository
{
    Task<IReadOnlyList<TripSearchItem>> SearchAsync(TripSearchQuery query, DateTimeOffset now, TimeSpan staleAfter, CancellationToken cancellationToken);
}

public interface ITripSearchCache
{
    Task<TripSearchResponse?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, TripSearchResponse response, TimeSpan ttl, CancellationToken cancellationToken);
}
