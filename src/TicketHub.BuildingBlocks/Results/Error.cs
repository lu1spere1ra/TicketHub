namespace TicketHub.BuildingBlocks.Results;

/// <summary>Category of a failure; decides the HTTP status code at the edge.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Gone,
    Unprocessable,
    Unavailable,
}

/// <summary>An expected, domain-level failure. Exceptions are reserved for things that should not happen.</summary>
public sealed record Error(string Code, string Message, ErrorKind Kind)
{
    /// <summary>Optional machine-readable details, e.g. the seat ids that were unavailable.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; init; }

    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);
    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static Error Gone(string code, string message) => new(code, message, ErrorKind.Gone);
    public static Error Unprocessable(string code, string message) => new(code, message, ErrorKind.Unprocessable);
    public static Error Unavailable(string code, string message) => new(code, message, ErrorKind.Unavailable);
}
