using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Tests.Domain;

public class EventSeatStatusTests
{
    [Fact]
    public void Newer_version_is_applied()
    {
        var status = EventSeatStatus.Create(Guid.NewGuid(), Guid.NewGuid(), SeatStatus.Held, version: 1);

        status.Apply(SeatStatus.Sold, version: 2).ShouldBeTrue();

        status.Status.ShouldBe(SeatStatus.Sold);
        status.Version.ShouldBe(2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Status_update_with_lower_or_equal_version_is_ignored(long staleVersion)
    {
        var status = EventSeatStatus.Create(Guid.NewGuid(), Guid.NewGuid(), SeatStatus.Sold, version: 2);

        status.Apply(SeatStatus.Available, staleVersion).ShouldBeFalse();

        status.Status.ShouldBe(SeatStatus.Sold);
        status.Version.ShouldBe(2);
    }
}
