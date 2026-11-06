namespace TicketHub.Catalog.Domain;

public sealed class Venue
{
    private readonly List<Seat> _seats = [];

    private Venue() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyList<Seat> Seats => _seats;

    public static Venue Create(string name, IEnumerable<(string Section, string Row, int Number)> layout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var venue = new Venue { Id = Guid.CreateVersion7(), Name = name };
        foreach (var (section, row, number) in layout)
        {
            if (venue._seats.Any(s => s.Section == section && s.Row == row && s.Number == number))
            {
                throw new ArgumentException($"Seat {section}-{row}{number} appears twice in the layout.", nameof(layout));
            }

            venue._seats.Add(new Seat(Guid.CreateVersion7(), venue.Id, section, row, number));
        }

        return venue;
    }
}

public sealed class Seat
{
    private Seat() { }

    internal Seat(Guid id, Guid venueId, string section, string row, int number)
    {
        Id = id;
        VenueId = venueId;
        Section = section;
        Row = row;
        Number = number;
    }

    public Guid Id { get; private set; }
    public Guid VenueId { get; private set; }
    public string Section { get; private set; } = string.Empty;
    public string Row { get; private set; } = string.Empty;
    public int Number { get; private set; }

    /// <summary>A seat the venue never sells, e.g. reserved for a camera position.</summary>
    public bool IsBlocked { get; private set; }

    public string Label => $"{Section}-{Row}{Number}";

    public void Block() => IsBlocked = true;
}
