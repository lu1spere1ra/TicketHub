using Microsoft.AspNetCore.Http;
using TicketHub.BuildingBlocks.Results;

namespace TicketHub.BuildingBlocks.Http;

public static class ErrorHttpExtensions
{
    /// <summary>Maps a domain error to an RFC 9457 problem response.</summary>
    public static IResult ToProblem(this Error error)
    {
        var status = error.Kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Gone => StatusCodes.Status410Gone,
            ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
            ErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };

        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is not null)
        {
            foreach (var (key, value) in error.Details)
            {
                extensions[key] = value;
            }
        }

        return TypedResults.Problem(detail: error.Message, statusCode: status, title: error.Code, extensions: extensions);
    }
}
