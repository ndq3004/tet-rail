using System.Text.Json;
using StackExchange.Redis;

namespace TetRail.Catalog.TripSearch;

public sealed class RedisTripSearchCache(string connectionString) : ITripSearchCache, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() => ConnectionMultiplexer.ConnectAsync(connectionString));

    public async Task<TripSearchResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await (await connection.Value).GetDatabase().StringGetAsync(key);
        return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<TripSearchResponse>(value!, JsonOptions);
    }

    public async Task SetAsync(string key, TripSearchResponse response, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await (await connection.Value).GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(response, JsonOptions), ttl);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated)
            (await connection.Value).Dispose();
    }
}
