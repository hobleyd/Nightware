namespace Nightware.Domain.Rooms;

/// <summary>A Graph <c>roomList</c>: a group of rooms, addressed by its email address.</summary>
public sealed record RoomList(string Id, string DisplayName, string EmailAddress)
{
    /// <summary>The list's street address (one line), when its rooms give one.</summary>
    public string? Address { get; init; }
}
