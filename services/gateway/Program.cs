using System.Net.Mime;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
var catalogBaseUrl = builder.Configuration["services:catalog:http:0"] is not null
    ? "https+http://catalog"
    : builder.Configuration["Catalog:BaseUrl"]
        ?? throw new InvalidOperationException("Catalog:BaseUrl is required.");
var timeoutSeconds = builder.Configuration.GetValue("Catalog:SearchTimeoutSeconds", 5);
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

app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "healthy" }));

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

app.Run();

public partial class Program;
