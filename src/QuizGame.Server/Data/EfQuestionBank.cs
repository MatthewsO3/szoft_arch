using Microsoft.EntityFrameworkCore;
using QuizGame.Contracts;
using QuizGame.Server.Services;

namespace QuizGame.Server.Data;

/// <summary>
/// EF Core implementation of the question bank (plan §5.1, §6.2). Follows the store pattern: a short-lived
/// context per call from <see cref="IDbContextFactory{T}"/> because it is registered as a singleton.
/// Type-specific validation is done by <see cref="QuestionValidation"/> before any write.
/// </summary>
public sealed class EfQuestionBank(IDbContextFactory<QuizDbContext> factory) : IQuestionBank
{
    // ---- Categories ----

    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Categories.AsNoTracking()
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name))
            .ToListAsync(ct);
    }

    public async Task<CategoryResult> CreateCategoryAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 64)
            return CategoryResult.Fail(CategoryError.NameInvalid);

        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Categories.AnyAsync(c => c.Name.ToLower() == trimmed.ToLower(), ct))
            return CategoryResult.Fail(CategoryError.NameTaken);

        var entity = new CategoryEntity { Name = trimmed };
        db.Categories.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return CategoryResult.Fail(CategoryError.NameTaken); // raced the unique index
        }

        return CategoryResult.Success(new CategoryDto(entity.Id, entity.Name));
    }

    public async Task<CategoryResult> RenameCategoryAsync(int id, string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 64)
            return CategoryResult.Fail(CategoryError.NameInvalid);

        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null)
            return CategoryResult.Fail(CategoryError.NotFound);

        if (await db.Categories.AnyAsync(c => c.Id != id && c.Name.ToLower() == trimmed.ToLower(), ct))
            return CategoryResult.Fail(CategoryError.NameTaken);

        entity.Name = trimmed;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return CategoryResult.Fail(CategoryError.NameTaken);
        }

        return CategoryResult.Success(new CategoryDto(entity.Id, entity.Name));
    }

    public async Task<CategoryDeleteResult> DeleteCategoryAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null)
            return CategoryDeleteResult.NotFound;

        // A category with questions (even soft-deleted ones) is kept so Question→Category references stay valid.
        if (await db.Questions.AnyAsync(q => q.CategoryId == id, ct))
            return CategoryDeleteResult.InUse;

        db.Categories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return CategoryDeleteResult.Deleted;
    }

    // ---- Questions ----

    public async Task<PagedResult<QuestionDto>> ListQuestionsAsync(QuestionQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize switch { < 1 => 20, > 100 => 100, var n => n };

        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.Questions.AsNoTracking().Where(x => x.IsActive);

        if (query.CategoryId is { } categoryId)
            q = q.Where(x => x.CategoryId == categoryId);
        if (query.Type is { } type)
            q = q.Where(x => x.Type == type);
        if (query.Difficulty is { } difficulty)
            q = q.Where(x => x.Difficulty == difficulty);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLower();
            q = q.Where(x => x.Text.ToLower().Contains(term));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Include(x => x.Options)
            .ToListAsync(ct);

        return new PagedResult<QuestionDto>(items.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<QuestionDto?> GetQuestionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Questions.AsNoTracking()
            .Include(x => x.Options)
            .FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<QuestionResult> CreateQuestionAsync(QuestionUpsertRequest request, Guid ownerId, CancellationToken ct = default)
    {
        if (QuestionValidation.Validate(request) is { } reason)
            return QuestionResult.Fail(QuestionError.Invalid, reason);

        await using var db = await factory.CreateDbContextAsync(ct);
        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct))
            return QuestionResult.Fail(QuestionError.CategoryNotFound, "The category does not exist.");

        var entity = new QuestionEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        Apply(entity, request);

        db.Questions.Add(entity);
        await db.SaveChangesAsync(ct);
        return QuestionResult.Success(Map(entity));
    }

    public async Task<QuestionResult> UpdateQuestionAsync(Guid id, QuestionUpsertRequest request, CancellationToken ct = default)
    {
        if (QuestionValidation.Validate(request) is { } reason)
            return QuestionResult.Fail(QuestionError.Invalid, reason);

        await using var db = await factory.CreateDbContextAsync(ct);
        var entity = await db.Questions.Include(x => x.Options)
            .FirstOrDefaultAsync(x => x.Id == id && x.IsActive, ct);
        if (entity is null)
            return QuestionResult.Fail(QuestionError.NotFound, "The question does not exist.");

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct))
            return QuestionResult.Fail(QuestionError.CategoryNotFound, "The category does not exist.");

        db.QuestionOptions.RemoveRange(entity.Options);
        entity.Options.Clear();
        Apply(entity, request);

        await db.SaveChangesAsync(ct);
        return QuestionResult.Success(Map(entity));
    }

    public async Task<bool> SoftDeleteQuestionAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var updated = await db.Questions
            .Where(x => x.Id == id && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false), ct);
        return updated > 0;
    }

    /// <summary>Copies a validated upsert onto an entity, replacing its options. Normalises labels to upper-case.</summary>
    private static void Apply(QuestionEntity entity, QuestionUpsertRequest request)
    {
        entity.CategoryId = request.CategoryId;
        entity.Type = request.Type;
        entity.Text = request.Text.Trim();
        entity.Difficulty = request.Difficulty;
        entity.TimeLimitSeconds = request.TimeLimitSeconds;

        if (request.Type == QuestionType.Estimation)
        {
            entity.CorrectNumber = request.CorrectNumber;
            entity.Unit = string.IsNullOrWhiteSpace(request.Unit) ? null : request.Unit.Trim();
        }
        else
        {
            entity.CorrectNumber = null;
            entity.Unit = null;
            foreach (var o in request.Options)
            {
                entity.Options.Add(new QuestionOptionEntity
                {
                    Id = Guid.NewGuid(),
                    QuestionId = entity.Id,
                    Label = char.ToUpperInvariant(o.Label),
                    Text = o.Text.Trim(),
                    IsCorrect = o.IsCorrect,
                });
            }
        }
    }

    private static QuestionDto Map(QuestionEntity e) => new()
    {
        Id = e.Id,
        CategoryId = e.CategoryId,
        Type = e.Type,
        Text = e.Text,
        Difficulty = e.Difficulty,
        TimeLimitSeconds = e.TimeLimitSeconds,
        CorrectNumber = e.CorrectNumber,
        Unit = e.Unit,
        Options = e.Options
            .OrderBy(o => o.Label)
            .Select(o => new OptionDto(o.Id, o.Label, o.Text))
            .ToList(),
        CorrectOption = e.Type == QuestionType.MultipleChoice
            ? e.Options.FirstOrDefault(o => o.IsCorrect)?.Label
            : null,
    };
}
