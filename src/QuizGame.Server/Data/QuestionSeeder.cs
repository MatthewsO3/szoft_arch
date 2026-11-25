using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using QuizGame.Contracts;

namespace QuizGame.Server.Data;

/// <summary>
/// Seeds the question bank from an embedded JSON file (plan §5.2: ~5 categories, ≥40 questions) so the game is
/// demoable immediately. Idempotent and non-destructive: it does nothing if any questions already exist.
/// </summary>
public static class QuestionSeeder
{
    private const string ResourceName = "QuizGame.Server.Data.Seed.questions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Returns the number of questions inserted (0 if the bank was already populated).</summary>
    public static async Task<int> EnsureAsync(QuizDbContext db, CancellationToken ct = default)
    {
        if (await db.Questions.AnyAsync(ct))
            return 0;

        var seed = Load();
        if (seed is null || seed.Questions.Count == 0)
            return 0;

        var categoriesByName = await EnsureCategoriesAsync(db, seed, ct);

        var added = 0;
        foreach (var q in seed.Questions)
        {
            if (!categoriesByName.TryGetValue(q.Category, out var categoryId))
                continue;

            var entity = new QuestionEntity
            {
                Id = Guid.NewGuid(),
                CategoryId = categoryId,
                Type = q.Type,
                Text = q.Text.Trim(),
                Difficulty = q.Difficulty,
                TimeLimitSeconds = q.TimeLimitSeconds,
                OwnerId = null, // shared seed questions (plan §5.1)
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            if (q.Type == QuestionType.Estimation)
            {
                entity.CorrectNumber = q.CorrectNumber;
                entity.Unit = string.IsNullOrWhiteSpace(q.Unit) ? null : q.Unit!.Trim();
            }
            else
            {
                foreach (var o in q.Options)
                {
                    entity.Options.Add(new QuestionOptionEntity
                    {
                        Id = Guid.NewGuid(),
                        QuestionId = entity.Id,
                        Label = char.ToUpperInvariant(o.Label),
                        Text = o.Text.Trim(),
                        IsCorrect = o.Correct,
                    });
                }
            }

            db.Questions.Add(entity);
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    private static async Task<Dictionary<string, int>> EnsureCategoriesAsync(
        QuizDbContext db, SeedFile seed, CancellationToken ct)
    {
        var existing = await db.Categories.ToDictionaryAsync(c => c.Name, c => c.Id, ct);
        var map = new Dictionary<string, int>(existing, StringComparer.Ordinal);

        var names = seed.Categories
            .Concat(seed.Questions.Select(q => q.Category))
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (map.ContainsKey(name))
                continue;
            var entity = new CategoryEntity { Name = name };
            db.Categories.Add(entity);
            await db.SaveChangesAsync(ct); // need the generated int id before linking questions
            map[name] = entity.Id;
        }

        return map;
    }

    private static SeedFile? Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return null;
        return JsonSerializer.Deserialize<SeedFile>(stream, JsonOptions);
    }

    private sealed record SeedFile(
        [property: JsonPropertyName("categories")] List<string> Categories,
        [property: JsonPropertyName("questions")] List<SeedQuestion> Questions);

    private sealed record SeedQuestion(
        string Category,
        QuestionType Type,
        int Difficulty,
        int TimeLimitSeconds,
        string Text,
        decimal? CorrectNumber,
        string? Unit,
        List<SeedOption> Options)
    {
        public List<SeedOption> Options { get; init; } = Options ?? [];
    }

    private sealed record SeedOption(char Label, string Text, bool Correct);
}
