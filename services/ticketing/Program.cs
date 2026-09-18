var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "ticketing",
    status = "healthy"
}));

app.Run();

public partial class Program;

