using TicketHub.TestKit;

[assembly: AssemblyFixture(typeof(PostgresContainer))]
[assembly: AssemblyFixture(typeof(RabbitMqContainer))]
[assembly: AssemblyFixture(typeof(RedisContainer))]
