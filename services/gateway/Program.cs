using StackExchange.Redis;
using TetRail.Gateway;
using System.Net.Mime;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var cognitoAuthority = builder.Configuration["Cognito:Authority"];
var cognitoAudience = builder.Configuration["Cognito:Audience"];
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = cognitoAuthority;
    options.Audience = cognitoAudience;
    options.RequireHttpsMetadata = !(builder.Environment.IsDevelopment()
        && Uri.TryCreate(cognitoAuthority, UriKind.Absolute, out var issuer)
        && issuer.IsLoopback && issuer.Scheme == Uri.UriSchemeHttp);
    options.Events = new JwtBearerEvents
    {
        OnChallenge = context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            var id = context.HttpContext.Items.TryGetValue("X-Correlation-ID", out var value) && value is Guid correlationId
                ? correlationId : Guid.NewGuid();
            return context.HttpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tetrail.local/problems/unauthenticated",
                title = "Authentication is required",
                status = 401,
                code = "UNAUTHENTICATED",
                correlation_id = id
            });
        }
    };
});
builder.Services.AddAuthorization();
var trafficEnabled = builder.Configuration.GetValue("Traffic:Enabled", builder.Environment.IsProduction());
if (trafficEnabled)
{
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(_ =>
    {
        var options = ConfigurationOptions.Parse(builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379");
        options.AbortOnConnectFail = false;
        options.AsyncTimeout = 500;
        return ConnectionMultiplexer.Connect(options);
    });
    builder.Services.AddSingleton<TrafficPressure>();
    builder.Services.AddHostedService(services => services.GetRequiredService<TrafficPressure>());
    builder.Services.AddSingleton<TrafficControl>();
}
var catalogBaseUrl = builder.Configuration["services:catalog:http:0"] is not null
    ? "http://catalog"
    : builder.Configuration["Catalog:BaseUrl"]
        ?? throw new InvalidOperationException("Catalog:BaseUrl is required.");
var timeoutSeconds = builder.Configuration.GetValue("Catalog:SearchTimeoutSeconds", 1222);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient("catalog", client =>
{
    client.BaseAddress = new Uri(catalogBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
}).AddServiceDiscovery();
builder.Services.AddHttpClient("booking", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Booking:BaseUrl"] ?? "http://localhost:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddServiceDiscovery();
builder.Services.AddHttpClient("ordering", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ordering:BaseUrl"] ?? "http://localhost:8081");
    client.Timeout = TimeSpan.FromSeconds(10);
}).AddServiceDiscovery();
builder.Services.AddHttpClient("ticketing", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ticketing:BaseUrl"] ?? "http://localhost:8082");
    client.Timeout = TimeSpan.FromSeconds(10);
}).RemoveAllLoggers();
builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "TetRail Gateway v1"));
}

app.Use(async (context, next) =>
{
    const string headerName = "X-Correlation-ID";
    var correlationId = context.Request.Headers.TryGetValue(headerName, out var supplied) && Guid.TryParse(supplied, out var parsed)
        ? parsed
        : Guid.NewGuid();
    context.Items[headerName] = correlationId;
    context.Request.Headers[headerName] = correlationId.ToString();
    context.Response.Headers[headerName] = correlationId.ToString();
    await next();
});
app.UseAuthentication();
if (trafficEnabled)
{
    var traffic = app.Services.GetRequiredService<TrafficControl>();
    app.Use(traffic.InvokeAsync);
    app.MapPost("/api/v1/waiting-room/admission", [Authorize] (HttpContext context, TrafficControl control) => control.PollAsync(context));
}
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "healthy" }));

app.MapPost("/api/holds", [Authorize] (HttpContext context, IHttpClientFactory clients, CancellationToken ct) =>
    ForwardBooking(context, clients, "/api/holds", ct));
app.MapGet("/api/holds/{id:guid}", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardBooking(context, clients, $"/api/holds/{id:D}", ct));
app.MapGet("/api/booking-requests/{id:guid}", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardBooking(context, clients, $"/api/booking-requests/{id:D}", ct));
app.MapPost("/api/orders", [Authorize] (HttpContext context, IHttpClientFactory clients, CancellationToken ct) =>
    ForwardOrdering(context, clients, "/api/orders", ct));
app.MapGet("/api/orders/{id:guid}", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardOrdering(context, clients, $"/api/orders/{id:D}", ct));

app.MapPost("/api/payments/{id:guid}/simulate", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardOrdering(context, clients, $"/api/payments/{id:D}/simulate", ct));
app.MapPost("/api/payment-webhooks/simulator", (HttpContext context, IHttpClientFactory clients, CancellationToken ct) =>
    ForwardOrdering(context, clients, "/api/payment-webhooks/simulator", ct, webhook: true));

app.MapGet("/api/orders/{id:guid}/tickets", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardOrdering(context, clients, $"/api/orders/{id:D}/tickets", ct, service: "ticketing"));
app.MapGet("/api/tickets/{id:guid}", [Authorize] (HttpContext context, IHttpClientFactory clients, Guid id, CancellationToken ct) =>
    ForwardOrdering(context, clients, $"/api/tickets/{id:D}", ct, service: "ticketing"));
app.MapPost("/api/tickets/validate", (HttpContext context, IHttpClientFactory clients, CancellationToken ct) =>
    ForwardOrdering(context, clients, "/api/tickets/validate", ct, webhook: true, service: "ticketing"));

app.MapGet("/api/v1/admin/reports/overview", [Authorize] async (HttpContext context, IHttpClientFactory clients,
    IServiceProvider services, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    try
    {
        using var auth = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/identity/me");
        auth.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
        using var authorized = await clients.CreateClient("catalog").SendAsync(auth, ct);
        if (!authorized.IsSuccessStatusCode)
            return Results.StatusCode((int)authorized.StatusCode);
        using var report = await clients.CreateClient("ticketing").GetAsync("/internal/v1/reports/overview", ct);
        report.EnsureSuccessStatusCode();
        var data = await report.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        object? traffic = null;
        if (services.GetService<TrafficControl>() is { } control)
        {
            try { traffic = await control.SnapshotAsync(); }
            catch (RedisException) { }
        }
        BookingPressure? booking = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var client = clients.CreateClient("booking");
            booking = await client.GetFromJsonAsync<BookingPressure>("/internal/v1/traffic-pressure", timeout.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException) { }
        return Results.Ok(new { data, traffic, booking });
    }
    catch (HttpRequestException) { return Results.StatusCode(503); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(503); }
});

static async Task<IResult> ForwardBooking(HttpContext context, IHttpClientFactory clients, string path, CancellationToken ct)
{
    var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
    if (!Guid.TryParse(subject, out var owner))
    {
        return Results.Forbid();
    }

    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path);
    request.Headers.TryAddWithoutValidation("X-User-Id", owner.ToString("D"));
    request.Headers.TryAddWithoutValidation("X-Correlation-ID", context.Items["X-Correlation-ID"]!.ToString());
    if (HttpMethods.IsPost(context.Request.Method))
    {
        request.Headers.TryAddWithoutValidation("Idempotency-Key", context.Request.Headers["Idempotency-Key"].ToArray());
        request.Content = new StreamContent(context.Request.Body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
    }

    try
    {
        using var response = await clients.CreateClient("booking").SendAsync(request, ct);
        return Results.Content(await response.Content.ReadAsStringAsync(ct),
            response.Content.Headers.ContentType?.ToString(), statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}

static async Task<IResult> ForwardOrdering(HttpContext context, IHttpClientFactory clients, string path, CancellationToken ct, bool webhook = false, string service = "ordering")
{
    var subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
    if (!Guid.TryParse(subject, out var owner) && !webhook) return Results.Forbid();
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path);
    if (service == "ticketing") context.Response.Headers.CacheControl = "no-store";
    if (webhook && service == "ordering")
        request.Headers.TryAddWithoutValidation("X-Simulator-Signature", context.Request.Headers["X-Simulator-Signature"].ToArray());
    else if (!webhook)
        request.Headers.TryAddWithoutValidation("X-User-Id", owner.ToString("D"));
    request.Headers.TryAddWithoutValidation("X-Correlation-Id", context.Items["X-Correlation-ID"]!.ToString());
    if (HttpMethods.IsPost(context.Request.Method))
    {
        request.Headers.TryAddWithoutValidation("Idempotency-Key", context.Request.Headers["Idempotency-Key"].ToArray());
        request.Content = new StreamContent(context.Request.Body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
    }
    try
    {
        using var response = await clients.CreateClient(service).SendAsync(request, ct);
        return Results.Content(await response.Content.ReadAsStringAsync(ct), response.Content.Headers.ContentType?.ToString(), statusCode: (int)response.StatusCode);
    }
    catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
}

app.MapMethods("/api/{**path}", ["GET", "POST", "PATCH", "PUT", "DELETE"], [Authorize] async (HttpContext context, string path, IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
{
    if (!path.StartsWith("identity/me", StringComparison.Ordinal) && !path.StartsWith("admin/identity/me", StringComparison.Ordinal) && !path.StartsWith("admin/catalog/", StringComparison.Ordinal) && !path.StartsWith("v1/admin/catalog/", StringComparison.Ordinal) && !path.StartsWith("passengers", StringComparison.Ordinal)) return Results.NotFound();
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"/api/{(path.StartsWith("v1/", StringComparison.Ordinal) ? path : "v1/" + path)}{context.Request.QueryString}");
    request.Headers.TryAddWithoutValidation("X-Correlation-ID", context.Items["X-Correlation-ID"]!.ToString());
    if (context.Request.Headers.Authorization.Count > 0) request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
    if (context.Request.ContentType is { } contentType)
    {
        request.Content = new StreamContent(context.Request.Body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }
    using var response = await clientFactory.CreateClient("catalog").SendAsync(request, cancellationToken);
    return Results.Content(await response.Content.ReadAsStringAsync(cancellationToken), response.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json, Encoding.UTF8, (int)response.StatusCode);
});

app.MapGet("/api/trips", async (HttpContext context, IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
{
    var correlationId = (Guid)context.Items["X-Correlation-ID"]!;
    var target = "/api/v1/trips" + context.Request.QueryString;
    using var request = new HttpRequestMessage(HttpMethod.Get, target);
    request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId.ToString());

    try
    {
        using var response = await clientFactory.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json;
        return Results.Content(payload, contentType, Encoding.UTF8, (int)response.StatusCode);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning(exception, "Catalog trip search proxy failed. CorrelationId={CorrelationId}", correlationId);
        return Results.Json(new
        {
            type = "https://tetrail.local/problems/catalog-unavailable",
            title = "Trip search is temporarily unavailable",
            status = StatusCodes.Status503ServiceUnavailable,
            code = "CATALOG_UNAVAILABLE",
            correlation_id = correlationId
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/api/trips/{tripId}/seats", async (string tripId, HttpContext context, IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
{
    var correlationId = (Guid)context.Items["X-Correlation-ID"]!;
    using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/trips/{tripId}/seats{context.Request.QueryString}");
    request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId.ToString());
    try
    {
        using var response = await clientFactory.CreateClient("catalog").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return Results.Content(payload, response.Content.Headers.ContentType?.ToString() ?? MediaTypeNames.Application.Json, Encoding.UTF8, (int)response.StatusCode);
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        app.Logger.LogWarning(exception, "Catalog seat map proxy failed. CorrelationId={CorrelationId}", correlationId);
        return Results.Json(new { type = "https://tetrail.local/problems/catalog-unavailable", title = "Seat map is temporarily unavailable", status = 503, code = "CATALOG_UNAVAILABLE", correlation_id = correlationId }, statusCode: 503);
    }
});

app.Run();

public partial class Program;
