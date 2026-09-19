using Npgsql;
using TetRail.Catalog;
using TetRail.Catalog.TripSearch;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.Configure<TripSearchOptions>(builder.Configuration.GetSection(TripSearchOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMetrics();
builder.Services.AddSingleton<TripSearchMetrics>();

var postgresConnection = builder.Configuration.GetConnectionString("CatalogPostgres")
    ?? throw new InvalidOperationException("ConnectionStrings:CatalogPostgres is required.");
var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(postgresConnection));
builder.Services.AddSingleton<ITripSearchRepository, PostgresTripSearchRepository>();
builder.Services.AddSingleton<ITripSearchCache>(_ => new RedisTripSearchCache(redisConnection));
builder.Services.AddScoped<TripSearchService>();

var app = builder.Build();
app.UseMiddleware<CorrelationIdMiddleware>();

app.MapGet("/health", () => Results.Ok(new { service = "catalog", status = "healthy" }));

app.MapGet("/api/v1/trips", async (
    string? from, string? to, string? date,
    TripSearchService service, HttpContext context, CancellationToken cancellationToken) =>
{
    var correlationId = (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!;
    try
    {
        var result = await service.SearchAsync(from, to, date, cancellationToken);
        if (result.IsValid)
            return Results.Ok(result.Response);

        return Results.Json(new ApiError(
            "https://tetrail.local/problems/validation-failed",
            "Request validation failed",
            StatusCodes.Status400BadRequest,
            "VALIDATION_FAILED",
            correlationId,
            Errors: result.Errors), statusCode: StatusCodes.Status400BadRequest);
    }
    catch (NpgsqlException exception)
    {
        app.Logger.LogError(exception, "Trip search PostgreSQL operation failed. CorrelationId={CorrelationId}", correlationId);
        return Results.Json(new ApiError(
            "https://tetrail.local/problems/catalog-unavailable",
            "Trip search is temporarily unavailable",
            StatusCodes.Status503ServiceUnavailable,
            "CATALOG_UNAVAILABLE",
            correlationId), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public partial class Program;
