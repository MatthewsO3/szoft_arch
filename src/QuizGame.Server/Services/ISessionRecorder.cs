using QuizGame.Contracts;
using QuizGame.Server.Domain;

namespace QuizGame.Server.Services;

/// <summary>
/// Writes room history to the database (plan §7.4 persistence cadence). Live state stays in memory;
/// this only records that sessions and players existed. Implementations must never throw: a history write
/// failing must not break a running game.
/// </summary>
public interface ISessionRecorder
{
    Task SessionStartedAsync(GameRoom room, CancellationToken ct = default);
    Task PlayerJoinedAsync(Guid sessionId, PlayerSummary player, CancellationToken ct = default);
    Task PlayerLeftAsync(Guid sessionId, Guid playerId, CancellationToken ct = default);
    Task SessionEndedAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>Plan §7.4: on round close insert the Round and its Answers in one transaction.</summary>
    Task RoundClosedAsync(Guid sessionId, RoundRecord round, CancellationToken ct = default);
}

/// <summary>Runs once at startup before requests are served: migrations, admin seed, stale-session cleanup.</summary>
public interface IPersistenceInitializer
{
    Task InitializeAsync(CancellationToken ct = default);
}
