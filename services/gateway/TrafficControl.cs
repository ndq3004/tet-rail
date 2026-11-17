using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using StackExchange.Redis;

namespace TetRail.Gateway;

public sealed class TrafficControl(ConnectionMultiplexer redis, IConfiguration config, TimeProvider clock, TrafficPressure downstream)
{
    private readonly IDatabase db = redis.GetDatabase();
    private readonly byte[] secret = DecodeSecret(config["Traffic:SigningKey"]);
    private int active;
    private long pressure;

    private const string RateScript = """
        for i,key in ipairs(KEYS) do
            if tonumber(redis.call('GET',key) or 0) >= tonumber(ARGV[i]) then
                return math.max(1,math.ceil(redis.call('PTTL',key)/1000))
            end
        end
        for _,key in ipairs(KEYS) do
            if redis.call('INCR',key)==1 then redis.call('PEXPIRE',key,10000) end
        end
        return 0
        """;

    private const string QueueScript = """
        local owner, now, limit, grant, delay = ARGV[1],tonumber(ARGV[2]),tonumber(ARGV[3]),ARGV[4],tonumber(ARGV[5])
        local expired = redis.call('ZRANGEBYSCORE',KEYS[2],'-inf',now,'LIMIT',0,128)
        for _,member in ipairs(expired) do
            redis.call('ZREM',KEYS[1],member)
            redis.call('ZREM',KEYS[2],member)
        end
        if redis.call('EXISTS',KEYS[4]) == 1 then return -2 end
        if not redis.call('ZSCORE',KEYS[1],owner) then
            if redis.call('ZCARD',KEYS[1]) >= limit then return -1 end
            local seq = redis.call('INCR',KEYS[3])
            redis.call('ZADD',KEYS[1],seq,owner)
        end
        redis.call('ZADD',KEYS[2],now+60000,owner)
        for i=1,3 do redis.call('PEXPIRE',KEYS[i],120000) end
        local rank = redis.call('ZRANK',KEYS[1],owner)
        if rank==0 and delay>0 and redis.call('SET',KEYS[5],'1','PX',delay,'NX') then
            redis.call('SET',KEYS[4],grant,'PX',30000)
            redis.call('ZREM',KEYS[1],owner)
            redis.call('ZREM',KEYS[2],owner)
            return 0
        end
        return rank+1
        """;

    private const string LiveQueueScript = """
        return redis.call('ZCOUNT',KEYS[1],'('..ARGV[1],'+inf')
        """;

    private const string ConsumeScript = """
        if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('DEL',KEYS[1]) end
        return 0
        """;

    private const string Prefix = "tetrail:traffic:{gateway}:";
    private static readonly RedisKey Queue = Prefix + "queue";

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var endpoint = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
        var hold = endpoint == "/api/holds" && HttpMethods.IsPost(context.Request.Method);
        var checkout = !hold && (path.StartsWithSegments("/api/holds") ||
            path.StartsWithSegments("/api/booking-requests") || path.StartsWithSegments("/api/orders") ||
            path.StartsWithSegments("/api/payments") || path.StartsWithSegments("/api/payment-webhooks") ||
            path.StartsWithSegments("/api/tickets") && !path.StartsWithSegments("/api/tickets/validate"));
        var bucket = checkout ? "checkout:" : "general:";
        var subject = Subject(context);
        var ip = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
        try
        {
            var keys = new List<RedisKey> { Prefix + bucket + "ip:" + Hash(ip), Prefix + bucket + "endpoint:" + Hash(context.Request.Method + endpoint) };
            var limits = new List<RedisValue> { Limit(checkout ? "CheckoutIpLimit" : "IpLimit", 120), Limit(checkout ? "CheckoutEndpointLimit" : "EndpointLimit", 1000) };
            if (subject is not null)
            {
                keys.Add(Prefix + bucket + "user:" + Hash(subject));
                limits.Add(Limit(checkout ? "CheckoutUserLimit" : "UserLimit", 60));
            }
            var wait = (int)await db.ScriptEvaluateAsync(RateScript, keys.ToArray(), limits.ToArray());
            if (wait > 0)
            {
                await RejectAsync(context, wait, "RATE_LIMITED");
                return;
            }

            if (hold && subject is not null)
            {
                var token = context.Request.Headers["X-Admission-Token"].ToString();
                if (token.Length > 0)
                {
                    if (UnderPressure())
                    {
                        await RejectAsync(context, 1, "CAPACITY_LIMITED");
                        return;
                    }
                    if (!await ConsumeAsync(token, subject))
                    {
                        await RejectAsync(context, 1, "ADMISSION_INVALID");
                        return;
                    }
                }
                else if (UnderPressure() || await LiveQueueDepthAsync() > 0)
                {
                    await RejectAsync(context, 1, "WAITING_ROOM_REQUIRED");
                    return;
                }
            }
        }
        catch (RedisException)
        {
            if (!checkout || !await TryCheckoutAsync(context, next))
                await RejectAsync(context, 1, "TRAFFIC_CONTROL_UNAVAILABLE");
            return;
        }

        if (checkout)
        {
            if (!await TryCheckoutAsync(context, next))
                await RejectAsync(context, 1, "CHECKOUT_CAPACITY_LIMITED");
            return;
        }
        var inFlight = Interlocked.Increment(ref active);
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (inFlight > Limit("ConcurrentLimit", 64))
            {
                MarkPressure();
                await RejectAsync(context, 1, "CAPACITY_LIMITED");
                return;
            }
            await next(context);
        }
        finally
        {
            if (context.Response.StatusCode >= 500 || Stopwatch.GetElapsedTime(started).TotalMilliseconds > Limit("LatencyMs", 200))
                MarkPressure();
            Interlocked.Decrement(ref active);
        }
    }

    private readonly System.Threading.RateLimiting.ConcurrencyLimiter checkoutCapacity = new(new()
    {
        PermitLimit = 16, QueueLimit = 0
    });

    private async Task<bool> TryCheckoutAsync(HttpContext context, RequestDelegate next)
    {
        using var permit = checkoutCapacity.AttemptAcquire();
        if (!permit.IsAcquired)
            return false;
        downstream.RecordCheckout();
        await next(context);
        return true;
    }

    public async Task<IResult> PollAsync(HttpContext context)
    {
        if (Subject(context) is not { } subject)
            return Results.Forbid();
        context.Response.Headers.CacheControl = "no-store";
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var userKey = Hash(subject);
        var grant = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var payload = $"{now + 30000}.{grant}";
        var token = payload + "." + WebEncoders.Base64UrlEncode(Sign(subject, payload));
        try
        {
            var pace = UnderPressure() ? 0 : Limit("AdmissionIntervalMs", 100);
            var result = (long)await db.ScriptEvaluateAsync(QueueScript,
                [Queue, Prefix + "expiry", Prefix + "sequence", GrantKey(userKey), Prefix + "admission-pace"],
                [userKey, now, Limit("QueueLimit", 10000), token, pace]);
            var admissionToken = result switch
            {
                0 => token,
                -2 => (string?)await db.StringGetAsync(GrantKey(userKey)),
                _ => null
            };
            if (admissionToken is not null)
                return Results.Ok(new { admissionToken, expiresIn = 30 });
            context.Response.Headers.RetryAfter = "1";
            return result == -1 ? Results.StatusCode(429) : Results.Json(new { position = Math.Max(1, result) }, statusCode: 202);
        }
        catch (RedisException)
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(503);
        }
    }

    private async Task<bool> ConsumeAsync(string token, string subject)
    {
        if (token.Length > 160)
            return false;
        var fields = token.Split('.');
        if (fields.Length != 3 || !long.TryParse(fields[0], out var expiration) ||
            expiration <= clock.GetUtcNow().ToUnixTimeMilliseconds())
            return false;
        var expected = WebEncoders.Base64UrlEncode(Sign(subject, fields[0] + "." + fields[1]));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.UTF8.GetBytes(fields[2])))
            return false;

        return (int)await db.ScriptEvaluateAsync(ConsumeScript, [GrantKey(Hash(subject))], [token]) == 1;
    }

    private async Task<long> LiveQueueDepthAsync() => (long)await db.ScriptEvaluateAsync(
        LiveQueueScript, [Prefix + "expiry"], [clock.GetUtcNow().ToUnixTimeMilliseconds()]);

    public async Task<object> SnapshotAsync() => new
    {
        active = Volatile.Read(ref active),
        capacity = Limit("ConcurrentLimit", 64),
        underPressure = UnderPressure(),
        queueDepth = await LiveQueueDepthAsync()
    };

    private bool UnderPressure() => downstream.IsOverloaded || Volatile.Read(ref active) >= Limit("ConcurrentLimit", 64) ||
        clock.GetUtcNow().ToUnixTimeMilliseconds() < Interlocked.Read(ref pressure);

    private void MarkPressure() => Interlocked.Exchange(ref pressure, clock.GetUtcNow().AddSeconds(2).ToUnixTimeMilliseconds());
    private int Limit(string name, int fallback) => Math.Max(1, config.GetValue($"Traffic:{name}", fallback));
    private byte[] Sign(string subject, string payload) =>
        HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"v1\nPOST /api/holds\n{subject}\n{payload}"));
    private static string? Subject(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static RedisKey GrantKey(string user) => Prefix + "grant:" + user;

    private static byte[] DecodeSecret(string? value)
    {
        var bytes = Convert.FromBase64String(value ?? "");
        return bytes.Length >= 32 ? bytes : throw new InvalidOperationException("Traffic:SigningKey must contain at least 32 base64-encoded bytes.");
    }

    private static Task RejectAsync(HttpContext context, int retry, string code)
    {
        context.Response.StatusCode = 429;
        context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return context.Response.WriteAsJsonAsync(new { code, retryAfterSeconds = retry }, context.RequestAborted);
    }
}
