using System.Diagnostics;
using StackExchange.Redis;

namespace TetRail.Gateway;

public sealed record BookingPressure(DateTimeOffset SampledAt, int DatabasePercent, int Queued, long OldestMs,
    int ActiveHolds, long KafkaWriteMs, long KafkaErrors, long KafkaSkew = 0, int? LogicalActiveHolds = null);

public sealed class TrafficPressure(
    IHttpClientFactory clients, ConnectionMultiplexer redis, IConfiguration configuration,
    TimeProvider clock) : BackgroundService
{
    private int overloaded = 1;
    private long checkoutRequests;
    public void RecordCheckout() => Interlocked.Increment(ref checkoutRequests);
    public bool IsOverloaded => Volatile.Read(ref overloaded) != 0;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var client = clients.CreateClient("booking");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var checkout = Interlocked.Exchange(ref checkoutRequests, 0);
            var loaded = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            try
            {
                var sample = await client.GetFromJsonAsync<BookingPressure>("/internal/v1/traffic-pressure", timeout.Token);
                var start = Stopwatch.GetTimestamp();
                await redis.GetDatabase().PingAsync().WaitAsync(timeout.Token);
                var pingMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var memory = await redis.GetDatabase().ExecuteAsync("INFO", "memory").WaitAsync(timeout.Token);
                loaded = sample is null || IsBookingOverloaded(sample) ||
                    checkout >= configuration.GetValue("Traffic:CheckoutRequestsPerSample", 50) ||
                    RedisMemoryOverloaded(memory.ToString(), configuration.GetValue("Traffic:RedisMemoryPercent", 85)) ||
                    pingMs >= configuration.GetValue("Traffic:RedisLatencyMs", 100);
            }
            catch (Exception error) when (error is HttpRequestException or RedisException or OperationCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested)
                    return;
            }
            Volatile.Write(ref overloaded, loaded ? 1 : 0);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private bool IsBookingOverloaded(BookingPressure sample) =>
        clock.GetUtcNow() - sample.SampledAt > TimeSpan.FromSeconds(3) || sample.SampledAt == default ||
        sample.DatabasePercent >= configuration.GetValue("Traffic:DatabasePercent", 80) ||
        sample.Queued >= configuration.GetValue("Traffic:BookingQueueLimit", 500) ||
        sample.OldestMs >= configuration.GetValue("Traffic:BookingQueueAgeMs", 2000) ||
        sample.ActiveHolds >= configuration.GetValue("Traffic:ActiveHoldLimit", 10000) ||
        sample.KafkaSkew >= configuration.GetValue("Traffic:KafkaPartitionSkew", 200) ||
        sample.KafkaWriteMs >= configuration.GetValue("Traffic:KafkaWriteMs", 200) || sample.KafkaErrors > 0;

    internal static bool RedisMemoryOverloaded(string info, int threshold)
    {
        long used = -1, max = -1;
        foreach (var raw in info.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("used_memory:", StringComparison.Ordinal))
                used = long.TryParse(line.AsSpan(12), out var value) ? value : -1;
            if (line.StartsWith("maxmemory:", StringComparison.Ordinal))
                max = long.TryParse(line.AsSpan(10), out var value) ? value : -1;
        }
        return used < 0 || max < 0 || (max > 0 && (decimal)used * 100 >= (decimal)max * threshold);
    }
}
