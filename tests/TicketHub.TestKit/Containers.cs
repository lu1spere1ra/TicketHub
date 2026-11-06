using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace TicketHub.TestKit;

/// <summary>
/// One PostgreSQL container per test assembly. Each test class asks for its own database, so tests stay isolated
/// without paying for a container per test.
/// </summary>
public sealed class PostgresContainer : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    // Several test hosts share this server in parallel, each with its own connection pool.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).WithCommand("-c", "max_connections=500").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database with a unique name and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

public sealed class RabbitMqContainer : IAsyncLifetime
{
    public const string Image = "rabbitmq:4-management-alpine";

    private readonly Testcontainers.RabbitMq.RabbitMqContainer _container =
        new RabbitMqBuilder(Image).WithUsername("guest").WithPassword("guest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

public sealed class RedisContainer : IAsyncLifetime
{
    public const string Image = "redis:8-alpine";

    private readonly Testcontainers.Redis.RedisContainer _container = new RedisBuilder(Image).Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    public Task StopAsync() => _container.StopAsync();

    public Task StartAsync() => _container.StartAsync();
}
