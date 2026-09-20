using Npgsql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Claims;
using TetRail.Catalog;
using TetRail.Catalog.Identity;
using TetRail.Catalog.Migrations;
using TetRail.Catalog.TripSearch;
using TetRail.Catalog.SeatMap;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.Configure<TripSearchOptions>(builder.Configuration.GetSection(TripSearchOptions.SectionName));
builder.Services.Configure<CognitoOptions>(builder.Configuration.GetSection(CognitoOptions.SectionName));
var cognito = builder.Configuration.GetSection(CognitoOptions.SectionName).Get<CognitoOptions>() ?? new CognitoOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.Authority = cognito.Authority;
    options.Audience = cognito.Audience;
    options.TokenValidationParameters.ValidIssuer = cognito.Authority;
    options.TokenValidationParameters.ValidAudience = cognito.Audience;
    options.TokenValidationParameters.RoleClaimType = cognito.GroupClaimType;
});
builder.Services.AddAuthorization();
builder.Services.AddDataProtection().SetApplicationName("TetRail.Catalog");
builder.Services.Configure<SeatMapOptions>(builder.Configuration.GetSection(SeatMapOptions.SectionName));
builder.Services.Configure<SeatProjectionKafkaOptions>(builder.Configuration.GetSection(SeatProjectionKafkaOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMetrics();
builder.Services.AddSingleton<TripSearchMetrics>();

var postgresConnection = builder.Configuration.GetConnectionString("CatalogPostgres")
    ?? throw new InvalidOperationException("ConnectionStrings:CatalogPostgres is required.");
var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.");

builder.Services.AddSingleton(NpgsqlDataSource.Create(postgresConnection));
builder.Services.AddSingleton<ICatalogMigrationRunner, CatalogMigrationRunner>();
builder.Services.AddSingleton<ITripSearchRepository, PostgresTripSearchRepository>();
builder.Services.AddSingleton<ITripSearchCache>(_ => new RedisTripSearchCache(redisConnection));
builder.Services.AddScoped<TripSearchService>();
builder.Services.AddSingleton<ISeatMapRepository, PostgresSeatMapRepository>();
builder.Services.AddSingleton<ISeatMapCache>(_ => new RedisSeatMapCache(redisConnection));
builder.Services.AddSingleton<ISeatProjectionEventApplier, PostgresSeatProjectionEventApplier>();
builder.Services.AddSingleton<BookingStateEventParser>();
builder.Services.AddSingleton<SeatProjectionEventProcessor>();
builder.Services.AddSingleton<ISeatProjectionKafkaClient, ConfluentSeatProjectionKafkaClient>();
builder.Services.AddSingleton<IPassengerRepository, PostgresPassengerRepository>();
builder.Services.AddHostedService<SeatProjectionKafkaConsumer>();
builder.Services.AddScoped<SeatMapService>();
builder.Services.AddOpenApi();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "TetRail Catalog v1"));
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { service = "catalog", status = "healthy" }));

app.MapGet("/api/v1/identity/me", [Authorize] (ClaimsPrincipal user) => Results.Ok(new CurrentIdentity(user.FindFirstValue("sub")!, user.FindAll(cognito.GroupClaimType).SelectMany(c => c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray())));
app.MapGet("/api/v1/passengers", [Authorize] async (ClaimsPrincipal user, IPassengerRepository repository, CancellationToken ct) => Results.Ok(await repository.ListAsync(user.FindFirstValue("sub")!, ct)));
app.MapGet("/api/v1/passengers/{id:guid}", [Authorize] async (Guid id, ClaimsPrincipal user, IPassengerRepository repository, CancellationToken ct) => (await repository.GetAsync(user.FindFirstValue("sub")!, id, ct)) is { } passenger ? Results.Ok(passenger) : Results.NotFound());
app.MapPost("/api/v1/passengers", [Authorize] async (PassengerInput input, ClaimsPrincipal user, IPassengerRepository repository, IDataProtectionProvider protection, HttpContext context, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.FullName) || input.FullName.Length > 200 || input.DateOfBirth is null || input.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow) || (input.NationalId?.Length > 32)) return Results.BadRequest();
    var normalized = string.IsNullOrWhiteSpace(input.NationalId) ? null : new string(input.NationalId.Where(char.IsLetterOrDigit).ToArray());
    if (normalized is { Length: < 6 }) return Results.BadRequest();
    var record = await repository.CreateAsync(user.FindFirstValue("sub")!, input with { FullName = input.FullName.Trim() }, normalized is null ? null : protection.CreateProtector("national-id.v1").Protect(normalized), normalized is null ? null : normalized[^4..], (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!, ct);
    return Results.Created($"/api/v1/passengers/{record.PassengerId}", record);
});

if (app.Environment.IsDevelopment())
{
    app.MapPost("/internal/migrations/run", async (
        ICatalogMigrationRunner runner, HttpContext context, CancellationToken cancellationToken) =>
    {
        var correlationId = (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!;
        try
        {
            return Results.Ok(await runner.RunAsync(cancellationToken));
        }
        catch (NpgsqlException exception)
        {
            app.Logger.LogError(exception, "Catalog migration operation failed. CorrelationId={CorrelationId}", correlationId);
            return Results.Json(new ApiError(
                "https://tetrail.local/problems/catalog-unavailable",
                "Catalog migration operation is temporarily unavailable",
                StatusCodes.Status503ServiceUnavailable,
                "CATALOG_UNAVAILABLE",
                correlationId), statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    });
}

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

app.MapGet("/api/v1/trips/{tripId}/seats", async (
    string tripId, string? from, string? to,
    SeatMapService service, HttpContext context, CancellationToken cancellationToken) =>
{
    var correlationId = (Guid)context.Items[CorrelationIdMiddleware.HeaderName]!;
    try
    {
        var result = await service.GetAsync(tripId, from, to, cancellationToken);
        if (result.IsValid) return Results.Ok(result.Response);
        return Results.Json(new ApiError("https://tetrail.local/problems/validation-failed", "Request validation failed", 400, "VALIDATION_FAILED", correlationId, Errors: result.Errors), statusCode: 400);
    }
    catch (NpgsqlException exception)
    {
        app.Logger.LogError(exception, "Seat map PostgreSQL operation failed. CorrelationId={CorrelationId}", correlationId);
        return Results.Json(new ApiError("https://tetrail.local/problems/catalog-unavailable", "Seat map is temporarily unavailable", 503, "CATALOG_UNAVAILABLE", correlationId), statusCode: 503);
    }
});

app.Run();

public partial class Program;
