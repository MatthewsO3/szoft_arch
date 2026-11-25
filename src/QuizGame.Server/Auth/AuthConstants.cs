namespace QuizGame.Server.Auth;

/// <summary>JWT role values (plan §6.1).</summary>
public static class Roles
{
    public const string GameMaster = "GameMaster";
    public const string Player = "Player";
}

/// <summary>Custom JWT claim names.</summary>
public static class ClaimNames
{
    /// <summary>Room code a player token is bound to.</summary>
    public const string Room = "room";
}
