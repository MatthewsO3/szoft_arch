namespace QuizGame.Server.Services;

/// <summary>A game master account as the auth code needs it.</summary>
public sealed record GameMasterRecord(Guid Id, string Username, string PasswordHash);

/// <summary>Storage of game masters (plan §5.1 GameMasters). EF Core in production, in-memory for local dev and tests.</summary>
public interface IGameMasterStore
{
    Task<GameMasterRecord?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>Creates the account unless the username already exists (case-insensitive). True if created.</summary>
    Task<bool> CreateIfMissingAsync(string username, string passwordHash, CancellationToken ct = default);
}

/// <summary>Username / name normalisation shared by every store so the rules cannot drift.</summary>
public static class NameKey
{
    /// <summary>Upper-invariant matches the OrdinalIgnoreCase comparison the room uses for player names.</summary>
    public static string Of(string value) => value.Trim().ToUpperInvariant();
}
