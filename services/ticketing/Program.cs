using Amazon;
using Amazon.S3;
using Npgsql;
using TetRail.Ticketing;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddOpenApi();
builder.Services.AddSingleton(NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("TicketPostgres")
    ?? builder.Configuration["DATABASE_URL"]
    ?? "Host=localhost;Port=5432;Database=tetrail;Username=tetrail;Password=tetrail-local-only"));
builder.Services.AddSingleton<IAmazonS3>(_ =>
{
    var endpoint = builder.Configuration["TicketStorage:Endpoint"];
    if (string.IsNullOrEmpty(endpoint))
        return new AmazonS3Client(RegionEndpoint.GetBySystemName(builder.Configuration["TicketStorage:Region"] ?? "us-east-1"));

    return new AmazonS3Client(
        builder.Configuration["TicketStorage:AccessKey"] ?? throw new InvalidOperationException("TicketStorage:AccessKey is required."),
        builder.Configuration["TicketStorage:SecretKey"] ?? throw new InvalidOperationException("TicketStorage:SecretKey is required."),
        new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
});
builder.Services.AddHttpClient("ordering", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Ordering:BaseUrl"] ?? "http://localhost:8081");
    client.Timeout = TimeSpan.FromSeconds(10);
}).RemoveAllLoggers();
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddSingleton<ITicketDocuments, TicketDocuments>();
builder.Services.AddHostedService<TicketEvents>();
builder.Services.AddHostedService<TicketDelivery>();
builder.Services.AddHostedService<ReportingWorker>();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "TetRail Ticketing v1"));
    var dataSource = app.Services.GetRequiredService<NpgsqlDataSource>();
    await using var connection = await dataSource.OpenConnectionAsync();
    await using var transaction = await connection.BeginTransactionAsync();
    await using var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('ticketing:migrations',0))", connection, transaction);
    await migrationLock.ExecuteNonQueryAsync();
    using var stream = typeof(TicketStore).Assembly.GetManifestResourceStream("TetRail.Ticketing.001_tickets.sql")!;
    using var reader = new StreamReader(stream);
    await using var migration = new NpgsqlCommand(await reader.ReadToEndAsync(), connection, transaction);
    await migration.ExecuteNonQueryAsync();
    await transaction.CommitAsync();
}

app.MapGet("/health", () => Results.Ok(new { service = "ticketing", status = "healthy" }));
app.MapGet("/api/orders/{id:guid}/tickets", async (Guid id, HttpContext context, TicketStore tickets, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!Guid.TryParse(context.Request.Headers["X-User-Id"], out var owner))
        return Results.NotFound();
    return Results.Ok(await tickets.ReadAsync(id, owner, true, ct));
});
app.MapGet("/api/tickets/{id:guid}", async (Guid id, HttpContext context, TicketStore tickets, ITicketDocuments documents, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!Guid.TryParse(context.Request.Headers["X-User-Id"], out var owner))
        return Results.NotFound();
    var ticket = (await tickets.ReadAsync(id, owner, false, ct)).SingleOrDefault();
    if (ticket is null)
        return Results.NotFound();
    var pdfUrl = ticket.PdfReady ? await documents.DownloadUrlAsync(ticket.TicketId) : null;
    return Results.Ok(new { ticket, pdfUrl });
});
app.MapPost("/api/tickets/validate", async (ValidateTicket request, HttpContext context, TicketStore tickets, CancellationToken ct) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { valid = await tickets.ValidateAsync(request.Reference, ct) });
});
app.MapGet("/internal/v1/reports/overview", Reports.ReadAsync);
app.Run();

public sealed record ValidateTicket(Guid Reference);
public partial class Program;
