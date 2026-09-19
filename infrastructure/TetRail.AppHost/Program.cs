var builder = DistributedApplication.CreateBuilder(args);

var catalog = builder.AddProject<Projects.TetRail_Catalog>("catalog")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_Gateway>("gateway")
    .WithHttpEndpoint(name: "http")
    .WithReference(catalog)
    .WaitFor(catalog)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_OrderPayment>("order-payment")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.TetRail_Ticketing>("ticketing")
    .WithHttpEndpoint(name: "http")
    .WithHttpHealthCheck("/health");

builder.AddExecutable("booking-engine", "go", "../../services/booking-engine")
    .WithArgs("run", "./cmd/api")
    .WithEnvironment("HTTP_ADDRESS", ":8080")
    .WithHttpEndpoint(port: 8080, targetPort: 8080, name: "http", isProxied: false)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
