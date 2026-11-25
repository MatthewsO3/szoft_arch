namespace QuizGame.Server.Data;

/// <summary>Plan §5.1 GameMasters. <see cref="UsernameKey"/> is the normalised name used for the unique index.</summary>
public sealed class GameMasterEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string UsernameKey { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row per room that was created (history; live state stays in memory, plan D9).</summary>
public sealed class GameSessionEntity
{
    public Guid Id { get; set; }
    public string RoomCode { get; set; } = "";
    public Guid GameMasterId { get; set; }
    public GameMasterEntity? GameMaster { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public List<PlayerEntity> Players { get; set; } = [];
}

public sealed class PlayerEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSessionEntity? Session { get; set; }
    public string Name { get; set; } = "";
    public string ColorHex { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}
