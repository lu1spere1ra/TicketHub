// Local orchestration. Every fixed host port is in 5200-5249 (docker-compose uses 5250-5299), see docs/PLAN.md §6.
var builder = DistributedApplication.CreateBuilder(args);

// Images match the ones used by Testcontainers and docker-compose, so every environment runs the same versions.
var postgres = builder.AddPostgres("postgres", port: 5224)
    .WithImage("library/postgres", "17-alpine");
var catalogDb = postgres.AddDatabase("catalogdb");
var bookingDb = postgres.AddDatabase("bookingdb");

var rabbitMq = builder.AddRabbitMQ("rabbitmq", port: 5223)
    .WithManagementPlugin(port: 5222)
    .WithImage("library/rabbitmq", "4-management-alpine");

// Catalog declares two Kestrel endpoints in appsettings.json: "http" (REST, HTTP/1.1) and "grpc" (cleartext HTTP/2).
var catalog = builder.AddProject<Projects.TicketHub_Catalog>("catalog")
    .WithEndpoint("http", endpoint => endpoint.Port = 5210)
    .WithEndpoint("grpc", endpoint => endpoint.Port = 5211)
    .WithReference(catalogDb).WaitFor(catalogDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithHttpHealthCheck("/health/ready");

var booking = builder.AddProject<Projects.TicketHub_Booking>("booking")
    .WithReference(bookingDb).WaitFor(bookingDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(catalog).WaitFor(catalog)
    .WithEnvironment("Booking__Holds__Duration", builder.Configuration["Booking:Holds:Duration"] ?? "00:10:00")
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.TicketHub_Gateway>("gateway")
    .WithReference(catalog).WaitFor(catalog)
    .WithReference(booking).WaitFor(booking)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
