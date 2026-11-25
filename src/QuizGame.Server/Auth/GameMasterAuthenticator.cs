using QuizGame.Server.Services;

namespace QuizGame.Server.Auth;

/// <summary>Game-master login against the persisted GameMasters (plan §5, §6.1).</summary>
public sealed class GameMasterAuthenticator(IGameMasterStore store, PasswordHasher hasher)
{
    private const int MaxPasswordLength = 256;

    // Verified when the username is unknown, so "no such user" costs the same time as "wrong password".
    private readonly string _dummyHash = hasher.Hash("not-a-real-password");

    /// <summary>Returns the game master for valid credentials, otherwise null.</summary>
    public async Task<GameMasterRecord?> AuthenticateAsync(string? username, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            return null;

        var gm = await store.FindByUsernameAsync(username, ct);
        var matches = hasher.Verify(password, gm?.PasswordHash ?? _dummyHash);
        return matches ? gm : null;
    }
}
