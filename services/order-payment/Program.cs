using System.Text.Json;
using TetRail.OrderPayment.Payments;
using Npgsql;
using TetRail.OrderPayment.Orders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddOpenApi();
var databaseUrl = builder.Configuration.GetConnectionString("OrderPostgres")
    ?? builder.Configuration["DATABASE_URL"]
    ?? "Host=localhost;Port=5432;Database=tetrail;Username=tetrail;Password=tetrail-local-only"
    ?? throw new InvalidOperationException("ConnectionStrings:OrderPostgres is required.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(databaseUrl));
builder.Services.AddSingleton<OrderMigrations>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("booking", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Booking:BaseUrl"] ?? "http://localhost:8080");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton(new SimulatorSignature(builder.Configuration["Payments:SimulatorSecret"] ?? ""));
builder.Services.AddSingleton<SimulatorService>();
builder.Services.AddHostedService<PaymentWorker>();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "TetRail Order & Payment v1"));
}
if (app.Environment.IsDevelopment())
    await app.Services.GetRequiredService<OrderMigrations>().RunAsync(CancellationToken.None);

app.MapGet("/health", () => Results.Ok(new
{
    service = "order-payment",
    status = "healthy"
}));
app.MapPost("/api/orders", async (CreateOrderRequest request, HttpContext context, OrderService orders, CancellationToken ct) =>
{
    if (!Guid.TryParse(context.Request.Headers["X-User-Id"], out var owner) || !Guid.TryParse(context.Request.Headers["X-Correlation-Id"], out var correlation))
        return Results.NotFound();
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 256) return Results.BadRequest();
    try
    {
        var order = await orders.CreateAsync(request, owner, key, correlation, ct);
        return order is null ? Results.NotFound() : Results.Created($"/api/orders/{order.OrderId:D}", order);
    }
    catch (ArgumentException exception)
    {
        app.Logger.LogInformation("Order request validation failed. CorrelationId={CorrelationId} Reason={Reason}", correlation, exception.Message);
        return Results.BadRequest(new { code = "VALIDATION_FAILED", detail = exception.Message });
    }
    catch (InvalidOperationException) { return Results.Conflict(); }
    catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
app.MapGet("/api/orders/{id:guid}", async (Guid id, HttpContext context, OrderService orders, CancellationToken ct) =>
{
    if (!Guid.TryParse(context.Request.Headers["X-User-Id"], out var owner)) return Results.NotFound();
    return await orders.GetAsync(id, owner, ct) is { } order ? Results.Ok(order) : Results.NotFound();
});

app.MapPost("/api/payments/{id:guid}/simulate", async (Guid id, SimulatePaymentRequest request,
    HttpContext context, SimulatorSignature signature, SimulatorService simulator, CancellationToken ct) =>
{
    if (!signature.IsConfigured)
        return Results.StatusCode(503);
    if (!Guid.TryParse(context.Request.Headers["X-User-Id"], out var owner)
        || !Guid.TryParse(context.Request.Headers["X-Correlation-Id"], out var correlation))
        return Results.NotFound();
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
        return Results.BadRequest();
    try
    {
        var attempt = await simulator.ScheduleAsync(id, owner, key, request.Scenario, correlation, ct);
        return attempt is null ? Results.NotFound() : Results.Accepted(value: attempt);
    }
    catch (ArgumentException) { return Results.BadRequest(); }
    catch (InvalidOperationException) { return Results.Conflict(); }
    catch (NpgsqlException) { return Results.StatusCode(503); }
});
app.MapPost("/api/payment-webhooks/simulator", async (HttpContext context, SimulatorSignature signature,
    SimulatorService simulator, CancellationToken ct) =>
{
    if (!signature.IsConfigured)
        return Results.StatusCode(503);
    var supplied = context.Request.Headers["X-Simulator-Signature"].ToString();
    if (supplied.Length != 64)
        return Results.Unauthorized();

    var buffer = new byte[4097];
    var length = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken: ct);
    if (length == buffer.Length)
        return Results.StatusCode(413);

    try
    {
        var callback = signature.ReadCallback(buffer[..length], supplied);
        return callback is null ? Results.Unauthorized() : Results.StatusCode(await simulator.AcceptAsync(callback, ct));
    }
    catch (JsonException) { return Results.BadRequest(); }
    catch (NpgsqlException) { return Results.StatusCode(503); }
});

app.MapGet("/internal/v1/paid-orders/{id:guid}/items", async (Guid id, NpgsqlDataSource database, CancellationToken ct) =>
{
    await using var command = database.CreateCommand("""
        SELECT o.owner_id,i.id,i.trip_id,i.seat_id,i.segment_index
        FROM ordering.orders o JOIN ordering.order_items i ON i.order_id=o.id
        WHERE o.id=@order AND o.state='PAID' ORDER BY i.id
        """);
    command.Parameters.AddWithValue("order", id);
    await using var reader = await command.ExecuteReaderAsync(ct);
    var items = new List<object>();
    Guid owner = default;
    while (await reader.ReadAsync(ct))
    {
        owner = reader.GetGuid(0);
        items.Add(new { itemId = reader.GetGuid(1), tripId = reader.GetGuid(2),
            seatId = reader.GetGuid(3), segmentIndex = reader.GetInt32(4) });
    }
    return items.Count == 0 ? Results.NotFound() : Results.Ok(new { ownerId = owner, items });
});

app.Run();

public partial class Program;
