using System.Text.Json;
using StackExchange.Redis;

namespace TetRail.Catalog.SeatMap;

public sealed class RedisSeatMapCache(string connectionString) : ISeatMapCache, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() => ConnectionMultiplexer.ConnectAsync(connectionString));

    public async Task<SeatMapResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var database = (await connection.Value.WaitAsync(cancellationToken)).GetDatabase();
        var value = await database.StringGetAsync(key);
        return value.HasValue ? JsonSerializer.Deserialize<SeatMapResponse>(value!) : null;
    }

    public async Task SetAsync(string key, SeatMapResponse response, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var database = (await connection.Value.WaitAsync(cancellationToken)).GetDatabase();
        await database.StringSetAsync(key, JsonSerializer.Serialize(response), ttl);
    }

    public async ValueTask DisposeAsync()
    {
        if (connection.IsValueCreated)
            (await connection.Value).Dispose();
    }
}
