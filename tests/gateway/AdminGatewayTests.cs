using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class AdminGatewayTests
{
    [Fact]
    public async Task GatewayForwardsCatalogWritesAndAuthorizesReportsBeforeReading()
    {
        var upstream = new AdminUpstream();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureServices(services =>
            {
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Test", _ => { });
                foreach (var client in new[] { "catalog", "ticketing", "booking" })
                    services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(() => upstream);
            });
        });
        using var client = factory.CreateClient();
        using var anonymous = await client.GetAsync("/api/v1/admin/reports/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer customer");
        using var denied = await client.GetAsync("/api/v1/admin/reports/overview");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(0, upstream.ReportReads);

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.Add("Authorization", "Bearer admin");
        using var write = await client.PutAsJsonAsync($"/api/v1/admin/catalog/stations/{Guid.NewGuid()}", new { code = "HNO", name = "Hanoi" });
        Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        Assert.Equal("application/json", upstream.WriteMediaType);
        using var report = await client.GetAsync("/api/v1/admin/reports/overview");
        report.EnsureSuccessStatusCode();
        var body = await report.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(2, body.GetProperty("booking").GetProperty("logicalActiveHolds").GetInt32());
        Assert.Equal(1, upstream.ReportReads);
    }

    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.ContainsKey("Authorization")
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class AdminUpstream : HttpMessageHandler
    {
        public int ReportReads;
        public string? WriteMediaType;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api/v1/admin/", StringComparison.Ordinal) && request.Headers.Authorization?.Parameter != "admin")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (path.StartsWith("/api/v1/admin/catalog/stations/", StringComparison.Ordinal))
                WriteMediaType = request.Content?.Headers.ContentType?.MediaType;
            if (path == "/internal/v1/reports/overview")
                ReportReads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path == "/internal/v1/traffic-pressure"
                    ? JsonContent.Create(new { logicalActiveHolds = 2 }) : JsonContent.Create(new { })
            });
        }
    }
}
