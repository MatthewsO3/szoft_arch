using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuizGame.Contracts;
using QuizGame.Server.Auth;
using QuizGame.Server.Domain;
using QuizGame.Server.Services;

namespace QuizGame.Server.Data;

public static class EfPersistenceExtensions
{
    public static IServiceCollection AddEfPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<QuizDbContext>(o => o.UseNpgsql(connectionString));
        services.AddSingleton<IGameMasterStore, EfGameMasterStore>();
        services.AddSingleton<ISessionRecorder, EfSessionRecorder>();
        services.AddSingleton<IQuestionBank, EfQuestionBank>();
        services.AddSingleton<IPersistenceInitializer, EfPersistenceInitializer>();
        return services;
    }
}

public sealed class EfGameMasterStore(IDbContextFactory<QuizDbContext> factory) : IGameMasterStore
{
    public async Task<GameMasterRecord?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var key = NameKey.Of(username);
        await using var db = await factory.CreateDbContextAsync(ct);
        var e = await db.GameMasters.AsNoTracking().FirstOrDefaultAsync(x => x.UsernameKey == key, ct);
        return e is null ? null : new GameMasterRecord(e.Id, e.Username, e.PasswordHash);
    }

    public async Task<bool> CreateIfMissingAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        var key = NameKey.Of(username);
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.GameMasters.AnyAsync(x => x.UsernameKey == key, ct)) return false;

        db.GameMasters.Add(new GameMasterEntity
        {
            Id = Guid.NewGuid(),
            Username = username.Trim(),
            UsernameKey = key,
            PasswordHash = passwordHash,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // another instance created the same username first (unique index)
        }
    }
}

/// <summary>Records room history. Every method swallows and logs failures: the live game never depends on the database.</summary>
public sealed class EfSessionRecorder(
    IDbContextFactory<QuizDbContext> factory,
    TimeProvider time,
    ILogger<EfSessionRecorder> logger) : ISessionRecorder
{
    public Task SessionStartedAsync(GameRoom room, CancellationToken ct = default) =>
        Run("session start", async db =>
        {
            db.GameSessions.Add(new GameSessionEntity
            {
                Id = room.SessionId,
                RoomCode = room.Code,
                GameMasterId = room.GameMasterId,
                CreatedAt = room.CreatedAt,
            });
            await db.SaveChangesAsync(ct);
        }, ct);

    public Task PlayerJoinedAsync(Guid sessionId, PlayerSummary player, CancellationToken ct = default) =>
        Run("player join", async db =>
        {
            db.Players.Add(new PlayerEntity
            {
                Id = player.Id,
                SessionId = sessionId,
                Name = player.Name,
                ColorHex = player.ColorHex,
                JoinedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
        }, ct);

    public Task PlayerLeftAsync(Guid sessionId, Guid playerId, CancellationToken ct = default) =>
        Run("player leave", async db =>
        {
            var now = time.GetUtcNow();
            await db.Players
                .Where(p => p.Id == playerId && p.SessionId == sessionId && p.LeftAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.LeftAt, now), ct);
        }, ct);

    public Task SessionEndedAsync(Guid sessionId, CancellationToken ct = default) =>
        Run("session end", async db =>
        {
            var now = time.GetUtcNow();
            await db.GameSessions
                .Where(s => s.Id == sessionId && s.EndedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now), ct);
        }, ct);

    public Task RoundClosedAsync(Guid sessionId, RoundRecord round, CancellationToken ct = default) =>
        Run("round close", async db =>
        {
            db.Rounds.Add(new RoundEntity
            {
                Id = round.RoundId,
                SessionId = sessionId,
                Seq = round.Seq,
                Kind = round.Kind.ToString(),
                QuestionId = round.QuestionId,
                StartedAt = round.StartedAt,
                Deadline = round.Deadline,
                ClosedAt = round.ClosedAt,
                AttackerId = round.AttackerId,
                DefenderId = round.DefenderId,
                TargetTerritoryIndex = round.TargetTerritoryIndex,
                WinnerPlayerId = round.WinnerPlayerId,
                Answers = round.Answers.Select(a => new AnswerEntity
                {
                    Id = Guid.NewGuid(),
                    PlayerId = a.PlayerId,
                    ChosenOption = a.Option?.ToString(),
                    NumericValue = a.Number,
                    ReceivedAt = a.ReceivedAt,
                    IsCorrect = a.IsCorrect,
                    Distance = a.Distance,
                    Rank = a.Rank,
                }).ToList(),
            });
            await db.SaveChangesAsync(ct); // Round + Answers: one SaveChanges = one transaction
        }, ct);

    private async Task Run(string what, Func<QuizDbContext, Task> action, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await action(db);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record {What}; the game continues", what);
        }
    }
}

/// <summary>Startup: apply migrations, seed the admin, close sessions left open by a previous run (rooms are in-memory).</summary>
public sealed class EfPersistenceInitializer(
    IDbContextFactory<QuizDbContext> factory,
    IGameMasterStore store,
    PasswordHasher hasher,
    IOptions<SeedOptions> seed,
    TimeProvider time,
    ILogger<EfPersistenceInitializer> logger) : IPersistenceInitializer
{
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            if (db.Database.GetMigrations().Any())
            {
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                // Fresh checkout before `dotnet ef migrations add InitialCreate` was run (see README).
                logger.LogWarning("No EF migrations found; creating the schema directly. Add the InitialCreate migration.");
                await db.Database.EnsureCreatedAsync(ct);
            }

            var now = time.GetUtcNow();
            var closed = await db.GameSessions.Where(s => s.EndedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.EndedAt, now), ct);
            if (closed > 0) logger.LogInformation("Closed {Count} session(s) left open by a previous run", closed);

            var seededQuestions = await QuestionSeeder.EnsureAsync(db, ct);
            if (seededQuestions > 0) logger.LogInformation("Seeded {Count} question(s) into the question bank", seededQuestions);
        }

        if (await AdminSeeder.EnsureAsync(store, hasher, seed.Value, ct))
            logger.LogInformation("Seeded game master '{User}'", seed.Value.AdminUsername);
    }
}
