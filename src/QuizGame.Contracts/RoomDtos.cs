namespace QuizGame.Contracts;

/// <summary>Result of creating a room (plan §6.2 POST /rooms).</summary>
public sealed record CreateRoomResponse(Guid SessionId, string RoomCode, string JoinUrl);

/// <summary>Join request body (plan §6.2 POST /rooms/{code}/join).</summary>
public sealed record JoinRequest(string Name);

/// <summary>Result of joining: the player token plus the player's own info.</summary>
public sealed record JoinResponse(string Token, PlayerSummary Player, DateTimeOffset ExpiresAt);

/// <summary>Lightweight room-existence check (plan §6.2 GET /rooms/{code}/exists).</summary>
public sealed record RoomExistsResponse(bool Exists, GamePhase? Phase);
