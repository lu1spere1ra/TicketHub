namespace TicketHub.BuildingBlocks;

/// <summary>The authenticated customer making the current request.</summary>
public interface ICurrentUser
{
    string UserId { get; }
    string Email { get; }
}
