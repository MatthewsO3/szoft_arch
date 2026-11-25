using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Domain;

namespace QuizGame.Server.Services;

/// <summary>
/// Development fallback when no <c>ConnectionStrings:Default</c> is configured: game masters live in memory
/// and nothing is recorded. Never used outside the Development environment (see Program.cs).
/// </summary>
public static class InMemoryPersistenceExtensions
{
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<IGameMasterStore, InMemoryGameMasterStore>();
        services.AddSingleton<ISessionRecorder, NullSessionRecorder>();
        services.AddSingleton<IPersistenceInitializer, InMemoryPersistenceInitializer>();
        return services;
    }
}

public sealed class InMemoryGameMasterStore : IGameMasterStore
{
    private readonly ConcurrentDictionary<string, GameMasterRecord> _byKey = new();

    public Task<GameMasterRecord?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(_byKey.GetValueOrDefault(NameKey.Of(username)));

    public Task<bool> CreateIfMissingAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        var record = new GameMasterRecord(Guid.NewGuid(), username.Trim(), passwordHash);
        return Task.FromResult(_byKey.TryAdd(NameKey.Of(username), record));
    }
}

public sealed class NullSessionRecorder : ISessionRecorder
{
    public Task SessionStartedAsync(GameRoom room, CancellationToken ct = default) => Task.CompletedTask;
    public Task PlayerJoinedAsync(Guid sessionId, PlayerSummary player, CancellationToken ct = default) => Task.CompletedTask;
    public Task PlayerLeftAsync(Guid sessionId, Guid playerId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SessionEndedAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RoundClosedAsync(Guid sessionId, RoundRecord round, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class InMemoryPersistenceInitializer(
    IGameMasterStore store,
    PasswordHasher hasher,
    IOptions<SeedOptions> seed) : IPersistenceInitializer
{
    public Task InitializeAsync(CancellationToken ct = default) =>
        AdminSeeder.EnsureAsync(store, hasher, seed.Value, ct);
}
