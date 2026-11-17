using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TetRail.Gateway;
using Xunit;

public sealed class TrafficTests
{
    [RedisFact]
    public async Task ReplicasPreserveFifoAndCheckoutDuringShedding()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("TRAFFIC_TEST_REDIS")!);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Traffic:SigningKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Traffic:IpLimit"] = "8", ["Traffic:AdmissionIntervalMs"] = "1"
        }).Build();
        var clock = new TestClock();
        using var upstream = new PressureUpstream(clock);
        using var pressure = new TrafficPressure(upstream, redis, config, clock);
        await pressure.StartAsync(default);
        try
        {
            var first = new TrafficControl(redis, config, clock, pressure);
            var second = new TrafficControl(redis, config, clock, pressure);
            var a = Guid.NewGuid().ToString();
            var b = Guid.NewGuid().ToString();
            Assert.Equal(202, ((IStatusCodeHttpResult)await first.PollAsync(Request(a))).StatusCode);
            Assert.Equal(202, ((IStatusCodeHttpResult)await second.PollAsync(Request(b))).StatusCode);
            upstream.Queue = 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (pressure.IsOverloaded)
                await Task.Delay(50, deadline.Token);

            var premature = await second.PollAsync(Request(b));
            Assert.Equal(202, ((IStatusCodeHttpResult)premature).StatusCode);
            var grant = Token(await second.PollAsync(Request(a)));
            var wrong = await Send(first, b, "/api/holds", grant);
            Assert.Equal(429, wrong.Response.StatusCode);
            var replay = await Task.WhenAll(Send(first, a, "/api/holds", grant), Send(second, a, "/api/holds", grant));
            Assert.Single(replay, x => x.Response.StatusCode == 204);
            Assert.Single(replay, x => x.Response.StatusCode == 429);

            var expiring = Token(await first.PollAsync(Request(b)));
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(429, (await Send(second, b, "/api/holds", expiring)).Response.StatusCode);

            upstream.Queue = 500;
            while (!pressure.IsOverloaded)
                await Task.Delay(50, deadline.Token);
            Assert.Equal(429, (await Send(first, a, "/api/holds")).Response.StatusCode);
            var browse = Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Send(first, a, "/api/trips")));
            using var ordering = new HttpClient();
            var orderUrl = Environment.GetEnvironmentVariable("TRAFFIC_TEST_ORDER_URL");
            var orderOwner = Environment.GetEnvironmentVariable("TRAFFIC_TEST_ORDER_OWNER");
            var checkout = Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Send(second, orderOwner ?? a, "/api/orders", checkout: true, upstream: orderUrl is null ? null : async () =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, orderUrl);
                    request.Headers.Add("X-User-Id", orderOwner);
                    using var response = await ordering.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                })));
            Assert.All(await checkout, response => Assert.Equal(204, response.Response.StatusCode));
            var shed = (await browse).Where(response => response.Response.StatusCode == 429).ToArray();
            Assert.NotEmpty(shed);
            Assert.All(shed, response => Assert.NotEmpty(response.Response.Headers.RetryAfter.ToString()));
        }
        finally { await pressure.StopAsync(default); }
    }

    private static string Token(IResult result)
    {
        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var value = ((IValueHttpResult)result).Value!;
        return (string)value.GetType().GetProperty("admissionToken")!.GetValue(value)!;
    }

    private static DefaultHttpContext Request(string subject) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "integration"))
    };

    private static async Task<HttpContext> Send(TrafficControl control, string subject, string path, string? token = null, bool checkout = false, Func<Task>? upstream = null)
    {
        var context = Request(subject);
        context.Request.Path = path;
        context.Request.Method = token is not null || checkout || path == "/api/holds" ? "POST" : "GET";
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(path), 0, EndpointMetadataCollection.Empty, path));
        if (token is not null)
            context.Request.Headers["X-Admission-Token"] = token;
        context.Response.Body = new MemoryStream();
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.RequestServices = services;
        await control.InvokeAsync(context, async response =>
        {
            if (upstream is null) await Task.Delay(15);
            else await upstream();
            response.Response.StatusCode = 204;
        });
        return context;
    }

    private sealed class PressureUpstream(TestClock clock) : HttpMessageHandler, IHttpClientFactory
    {
        public volatile int Queue = 500;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("http://booking") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new BookingPressure(clock.GetUtcNow(), 1, Queue, 0, 0, 0, 0))
            });
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan span) => Interlocked.Add(ref ticks, span.Ticks);
    }
}

internal sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRAFFIC_TEST_REDIS")))
            Skip = "Set TRAFFIC_TEST_REDIS to an isolated Redis database.";
    }
}
