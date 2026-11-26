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
        services.AddSingleton<IQuestionBank, InMemoryQuestionBank>();
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
    public Task TerritoriesChangedAsync(Guid sessionId, IReadOnlyList<TerritoryRecord> territories, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Development-only question bank kept entirely in memory. Mirrors <c>EfQuestionBank</c> behaviour closely
/// enough to develop the controllers and clients against; it is never used when a database is configured.
/// </summary>
public sealed class InMemoryQuestionBank : IQuestionBank
{
    private readonly object _gate = new();
    private readonly Dictionary<int, string> _categories = new();
    private readonly Dictionary<Guid, StoredQuestion> _questions = new();
    private int _nextCategoryId = 1;

    private sealed record StoredQuestion(QuestionDto Dto, Guid? OwnerId, bool IsActive, DateTimeOffset CreatedAt);

    public Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<CategoryDto> list = _categories
                .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new CategoryDto(kv.Key, kv.Value))
                .ToList();
            return Task.FromResult(list);
        }
    }

    public Task<CategoryResult> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 64)
            return Task.FromResult(CategoryResult.Fail(CategoryError.NameInvalid));

        lock (_gate)
        {
            if (_categories.Values.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(CategoryResult.Fail(CategoryError.NameTaken));

            var id = _nextCategoryId++;
            _categories[id] = trimmed;
            return Task.FromResult(CategoryResult.Success(new CategoryDto(id, trimmed)));
        }
    }

    public Task<CategoryResult> RenameCategoryAsync(int id, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 64)
            return Task.FromResult(CategoryResult.Fail(CategoryError.NameInvalid));

        lock (_gate)
        {
            if (!_categories.ContainsKey(id))
                return Task.FromResult(CategoryResult.Fail(CategoryError.NotFound));
            if (_categories.Any(kv => kv.Key != id && string.Equals(kv.Value, trimmed, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(CategoryResult.Fail(CategoryError.NameTaken));

            _categories[id] = trimmed;
            return Task.FromResult(CategoryResult.Success(new CategoryDto(id, trimmed)));
        }
    }

    public Task<CategoryDeleteResult> DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_categories.ContainsKey(id))
                return Task.FromResult(CategoryDeleteResult.NotFound);
            if (_questions.Values.Any(q => q.Dto.CategoryId == id))
                return Task.FromResult(CategoryDeleteResult.InUse);

            _categories.Remove(id);
            return Task.FromResult(CategoryDeleteResult.Deleted);
        }
    }

    public Task<PagedResult<QuestionDto>> ListQuestionsAsync(QuestionQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize switch { < 1 => 20, > 100 => 100, var n => n };

        lock (_gate)
        {
            var filtered = _questions.Values.Where(q => q.IsActive).Select(q => q.Dto);
            if (query.CategoryId is { } categoryId)
                filtered = filtered.Where(q => q.CategoryId == categoryId);
            if (query.Type is { } type)
                filtered = filtered.Where(q => q.Type == type);
            if (query.Difficulty is { } difficulty)
                filtered = filtered.Where(q => q.Difficulty == difficulty);
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim();
                filtered = filtered.Where(q => q.Text.Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            var ordered = filtered
                .OrderByDescending(q => _questions[q.Id].CreatedAt)
                .ToList();
            var total = ordered.Count;
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new PagedResult<QuestionDto>(items, page, pageSize, total));
        }
    }

    public Task<QuestionDto?> GetQuestionAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _questions.TryGetValue(id, out var q) && q.IsActive ? q.Dto : null);
        }
    }

    public Task<QuestionResult> CreateQuestionAsync(QuestionUpsertRequest request, Guid ownerId, CancellationToken ct = default)
    {
        if (QuestionValidation.Validate(request) is { } reason)
            return Task.FromResult(QuestionResult.Fail(QuestionError.Invalid, reason));

        lock (_gate)
        {
            if (!_categories.ContainsKey(request.CategoryId))
                return Task.FromResult(QuestionResult.Fail(QuestionError.CategoryNotFound, "The category does not exist."));

            var id = Guid.NewGuid();
            var dto = Build(id, request);
            _questions[id] = new StoredQuestion(dto, ownerId, true, DateTimeOffset.UtcNow);
            return Task.FromResult(QuestionResult.Success(dto));
        }
    }

    public Task<QuestionResult> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default)
    {
        if (QuestionValidation.Validate(request) is { } reason)
            return Task.FromResult(QuestionResult.Fail(QuestionError.Invalid, reason));

        lock (_gate)
        {
            if (!_questions.TryGetValue(id, out var existing) || !existing.IsActive)
                return Task.FromResult(QuestionResult.Fail(QuestionError.NotFound, "The question does not exist."));
            if (!_categories.ContainsKey(request.CategoryId))
                return Task.FromResult(QuestionResult.Fail(QuestionError.CategoryNotFound, "The category does not exist."));

            var dto = Build(id, request);
            _questions[id] = existing with { Dto = dto };
            return Task.FromResult(QuestionResult.Success(dto));
        }
    }

    public Task<bool> SoftDeleteQuestionAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_questions.TryGetValue(id, out var existing) || !existing.IsActive)
                return Task.FromResult(false);
            _questions[id] = existing with { IsActive = false };
            return Task.FromResult(true);
        }
    }

    private static QuestionDto Build(Guid id, QuestionUpsertRequest r)
    {
        var options = r.Type == QuestionType.MultipleChoice
            ? r.Options
                .Select(o => new OptionDto(Guid.NewGuid(), char.ToUpperInvariant(o.Label), o.Text.Trim()))
                .OrderBy(o => o.Label)
                .ToList()
            : [];

        return new QuestionDto
        {
            Id = id,
            CategoryId = r.CategoryId,
            Type = r.Type,
            Text = r.Text.Trim(),
            Difficulty = r.Difficulty,
            TimeLimitSeconds = r.TimeLimitSeconds,
            Options = options,
            CorrectOption = r.Type == QuestionType.MultipleChoice
                ? r.Options.Where(o => o.IsCorrect)
                    .Select(o => (char?)char.ToUpperInvariant(o.Label)).FirstOrDefault()
                : null,
            CorrectNumber = r.Type == QuestionType.Estimation ? r.CorrectNumber : null,
            Unit = r.Type == QuestionType.Estimation && !string.IsNullOrWhiteSpace(r.Unit) ? r.Unit.Trim() : null,
        };
    }
}

public sealed class InMemoryPersistenceInitializer(
    IGameMasterStore store,
    PasswordHasher hasher,
    IOptions<SeedOptions> seed) : IPersistenceInitializer
{
    public Task InitializeAsync(CancellationToken ct = default) =>
        AdminSeeder.EnsureAsync(store, hasher, seed.Value, ct);
}
