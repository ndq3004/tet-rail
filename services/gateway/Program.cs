var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "gateway",
    status = "healthy"
}));

app.Run();

public partial class Program;

