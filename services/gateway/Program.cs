using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
var cognitoAuthority = builder.Configuration["Cognito:Authority"];
var cognitoAudience = builder.Configuration["Cognito:Audience"];
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options => { options.Authority = cognitoAuthority; options.Audience = cognitoAudience; options.Events = new JwtBearerEvents { OnChallenge = context => { context.HandleResponse(); var id = context.HttpContext.Items.TryGetValue("X-Correlation-ID", out var value) && value is Guid correlationId ? correlationId : Guid.NewGuid(); return context.HttpContext.Response.WriteAsJsonAsync(new { type = "https://tetrail.local/problems/unauthenticated", title = "Authentication is required", status = 401, code = "UNAUTHENTICATED", correlation_id = id }); } }; });
builder.Services.AddAuthorization();
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
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "healthy" }));

app.MapMethods("/api/{**path}", ["GET", "POST", "PATCH", "DELETE"], [Authorize] async (HttpContext context, string path, IHttpClientFactory clientFactory, CancellationToken cancellationToken) =>
{
    if (!path.StartsWith("identity/me", StringComparison.Ordinal) && !path.StartsWith("admin/identity/me", StringComparison.Ordinal) && !path.StartsWith("passengers", StringComparison.Ordinal)) return Results.NotFound();
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), $"/api/v1/{path}{context.Request.QueryString}");
    request.Headers.TryAddWithoutValidation("X-Correlation-ID", context.Items["X-Correlation-ID"]!.ToString());
    if (context.Request.Headers.Authorization.Count > 0) request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToString());
    if (context.Request.ContentLength > 0) request.Content = new StreamContent(context.Request.Body);
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
