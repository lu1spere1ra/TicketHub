using TicketHub.BuildingBlocks;
using TicketHub.Catalog.Domain;

namespace TicketHub.Catalog.Tests.Domain;

public class EventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Venue Hall = Venue.Create("Hall", [("A", "1", 1), ("B", "1", 1)]);

    private static Event JazzNight() => Event.Schedule(
        Guid.NewGuid(), "Jazz", Hall, startsAt: Now.AddDays(10), salesOpenAt: Now, salesCloseAt: Now.AddDays(9),
        [new SectionPrice("A", 45m, Money.Euro)]);

    [Fact]
    public void Event_is_on_sale_only_between_sales_open_and_close()
    {
        var @event = JazzNight();

        @event.IsOnSale(Now.AddTicks(-1)).ShouldBeFalse();
        @event.IsOnSale(Now).ShouldBeTrue();
        @event.IsOnSale(Now.AddDays(9).AddTicks(-1)).ShouldBeTrue();
        @event.IsOnSale(Now.AddDays(9)).ShouldBeFalse();
    }

    [Fact]
    public void Price_is_taken_from_the_seat_section()
    {
        var @event = JazzNight();

        @event.PriceFor("A").ShouldBe(new Money(45m, Money.Euro));
        @event.PriceFor("B").ShouldBeNull();
    }

    [Fact]
    public void Sales_window_must_open_before_it_closes()
    {
        Should.Throw<ArgumentException>(() => Event.Schedule(
            Guid.NewGuid(), "Bad", Hall, Now.AddDays(10), salesOpenAt: Now.AddDays(2), salesCloseAt: Now.AddDays(1), []));
    }

    [Fact]
    public void Sales_must_close_before_the_event_starts()
    {
        Should.Throw<ArgumentException>(() => Event.Schedule(
            Guid.NewGuid(), "Bad", Hall, Now.AddDays(1), salesOpenAt: Now, salesCloseAt: Now.AddDays(2), []));
    }

    [Fact]
    public void Venue_layout_rejects_duplicate_seats()
    {
        Should.Throw<ArgumentException>(() => Venue.Create("Hall", [("A", "1", 1), ("A", "1", 1)]));
    }

    [Fact]
    public void Seat_label_combines_section_row_and_number()
    {
        Venue.Create("Hall", [("A", "3", 12)]).Seats.Single().Label.ShouldBe("A-312");
    }
}
