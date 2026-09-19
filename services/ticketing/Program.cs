var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "TetRail Ticketing v1"));
}

app.MapGet("/health", () => Results.Ok(new
{
    service = "ticketing",
    status = "healthy"
}));

app.Run();

public partial class Program;
