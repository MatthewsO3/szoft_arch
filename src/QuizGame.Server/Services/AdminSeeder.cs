using QuizGame.Server.Auth;

namespace QuizGame.Server.Services;

/// <summary>Creates the seed game master from configuration (plan §5.2). Never overwrites an existing account.</summary>
public static class AdminSeeder
{
    public static async Task<bool> EnsureAsync(IGameMasterStore store, PasswordHasher hasher, SeedOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.AdminUsername) || string.IsNullOrEmpty(options.AdminPassword))
            throw new InvalidOperationException("Seed:AdminUsername and Seed:AdminPassword must be set.");

        return await store.CreateIfMissingAsync(options.AdminUsername, hasher.Hash(options.AdminPassword), ct);
    }
}
