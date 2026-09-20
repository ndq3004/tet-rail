var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.TetRail_Catalog>("catalog")
    .WithHttpEndpoint(name: "http-catalog")
    .WithUrlForEndpoint("http-catalog", endpoint => new() { Url = "/swagger", DisplayText = "Swagger UI" })
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_Gateway>("gateway")
    //.WithHttpsEndpoint(port: 51757, name: "https-gateway")
    .WithHttpEndpoint(port: 52757, name: "http-gateway")
    .WithUrlForEndpoint("https-gateway", endpoint => new() { Url = "/swagger", DisplayText = "Swagger UI" })
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_OrderPayment>("order-payment")
    .WithHttpEndpoint(name: "http-order-payment")
    .WithUrlForEndpoint("http-order-payment", endpoint => new() { Url = "/swagger", DisplayText = "Swagger UI" })
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_Ticketing>("ticketing")
    .WithHttpEndpoint(name: "http-ticketing")
    .WithUrlForEndpoint("http-ticketing", endpoint => new() { Url = "/swagger", DisplayText = "Swagger UI" })
    .WithHttpHealthCheck("/health");

builder.AddExecutable("booking-engine", "go", "../../services/booking-engine")
    .WithArgs("run", "./cmd/api")
    .WithEnvironment("HTTP_ADDRESS", ":8080")
    .WithEnvironment("DATABASE_URL", "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail?search_path=booking")
    .WithEnvironment("KAFKA_BROKERS", "localhost:9092")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http", isProxied: false)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
