using TicketHub.Booking.Domain;

namespace TicketHub.Booking.Tests.Domain;

public class ArchitectureTests
{
    [Fact]
    public void Domain_types_do_not_depend_on_infrastructure()
    {
        // The domain lives in a folder rather than its own project, so this test keeps its boundary honest.
        var forbidden = new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "RabbitMQ", "Grpc", "Npgsql" };
        var domainTypes = typeof(Reservation).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(Reservation).Namespace)
            .ToList();

        var offending = domainTypes
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Select(f => f.FieldType)
                .Concat(t.GetProperties().Select(p => p.PropertyType))
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Select(dependency => (Type: t, Dependency: dependency)))
            .Where(x => forbidden.Any(prefix => x.Dependency.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true))
            .Select(x => $"{x.Type.Name} -> {x.Dependency.FullName}")
            .Distinct()
            .ToList();

        domainTypes.ShouldNotBeEmpty();
        offending.ShouldBeEmpty();
    }
}
