// Local orchestration. Every fixed host port is in 5200-5249 (docker-compose uses 5250-5299), see docs/PLAN.md §6.
var builder = DistributedApplication.CreateBuilder(args);

// Images match the ones used by Testcontainers and docker-compose, so every environment runs the same versions.
var postgres = builder.AddPostgres("postgres", port: 5224)
    .WithImage("library/postgres", "17-alpine");
var catalogDb = postgres.AddDatabase("catalogdb");
var bookingDb = postgres.AddDatabase("bookingdb");
var paymentsDb = postgres.AddDatabase("paymentsdb");
var notificationsDb = postgres.AddDatabase("notificationsdb");

var rabbitMq = builder.AddRabbitMQ("rabbitmq", port: 5223)
    .WithManagementPlugin(port: 5222)
    .WithImage("library/rabbitmq", "4-management-alpine");

var redis = builder.AddRedis("redis", port: 5225)
    .WithImage("library/redis", "8-alpine");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit", "v1.27")
    .WithHttpEndpoint(port: 5221, targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");

// Catalog declares two Kestrel endpoints in appsettings.json: "http" (REST, HTTP/1.1) and "grpc" (cleartext HTTP/2).
var catalog = builder.AddProject<Projects.TicketHub_Catalog>("catalog")
    .WithEndpoint("http", endpoint => endpoint.Port = 5210)
    .WithEndpoint("grpc", endpoint => endpoint.Port = 5211)
    .WithReference(catalogDb).WaitFor(catalogDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(redis).WaitFor(redis)
    .WithHttpHealthCheck("/health/ready");

var booking = builder.AddProject<Projects.TicketHub_Booking>("booking")
    .WithReference(bookingDb).WaitFor(bookingDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithReference(catalog).WaitFor(catalog)
    .WithEnvironment("Booking__Holds__Duration", builder.Configuration["Booking:Holds:Duration"] ?? "00:10:00")
    .WithEnvironment("Booking__Expiry__PollInterval", builder.Configuration["Booking:Expiry:PollInterval"] ?? "00:00:05")
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.TicketHub_Payments>("payments")
    .WithReference(paymentsDb).WaitFor(paymentsDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("Payments__Simulator__DeclineRate", builder.Configuration["Payments:Simulator:DeclineRate"] ?? "0.10")
    .WithEnvironment("Payments__Simulator__TransientFailureRate", builder.Configuration["Payments:Simulator:TransientFailureRate"] ?? "0.10")
    .WithHttpHealthCheck("/health/ready");

var smtp = mailpit.GetEndpoint("smtp");
builder.AddProject<Projects.TicketHub_Notifications>("notifications")
    .WithReference(notificationsDb).WaitFor(notificationsDb)
    .WithReference(rabbitMq).WaitFor(rabbitMq)
    .WithEnvironment("Smtp__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Smtp__Port", smtp.Property(EndpointProperty.Port))
    .WaitFor(mailpit)
    .WithHttpHealthCheck("/health/ready");

builder.AddProject<Projects.TicketHub_Gateway>("gateway")
    .WithReference(catalog).WaitFor(catalog)
    .WithReference(booking).WaitFor(booking)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health/ready");

builder.Build().Run();
