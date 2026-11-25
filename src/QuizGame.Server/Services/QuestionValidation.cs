using QuizGame.Contracts;

namespace QuizGame.Server.Services;

/// <summary>
/// Type-specific validation for question upserts (plan §5.2): a multiple-choice question has 2–4 options with
/// exactly one correct and no number; an estimation question has a correct number and no options. Pure and
/// dependency-free so it is trivially unit-testable (plan §6.2 "plain code is fine").
/// </summary>
public static class QuestionValidation
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;
    public const int MaxTextLength = 1000;
    public const int MinDifficulty = 1;
    public const int MaxDifficulty = 3;
    public const int MinTimeLimitSeconds = 3;
    public const int MaxTimeLimitSeconds = 600;

    private static readonly char[] ValidLabels = ['A', 'B', 'C', 'D'];

    /// <summary>Returns null when valid, otherwise a human-readable reason for a 400.</summary>
    public static string? Validate(QuestionUpsertRequest r)
    {
        var text = r.Text?.Trim() ?? "";
        if (text.Length == 0)
            return "Question text must not be empty.";
        if (text.Length > MaxTextLength)
            return $"Question text must be at most {MaxTextLength} characters.";
        if (r.Difficulty is < MinDifficulty or > MaxDifficulty)
            return $"Difficulty must be between {MinDifficulty} and {MaxDifficulty}.";
        if (r.TimeLimitSeconds is < MinTimeLimitSeconds or > MaxTimeLimitSeconds)
            return $"Time limit must be between {MinTimeLimitSeconds} and {MaxTimeLimitSeconds} seconds.";
        if (r.CategoryId <= 0)
            return "A valid category is required.";

        return r.Type switch
        {
            QuestionType.MultipleChoice => ValidateMultipleChoice(r),
            QuestionType.Estimation => ValidateEstimation(r),
            _ => "Unknown question type.",
        };
    }

    private static string? ValidateMultipleChoice(QuestionUpsertRequest r)
    {
        if (r.CorrectNumber is not null)
            return "A multiple-choice question must not have a correct number.";
        if (!string.IsNullOrWhiteSpace(r.Unit))
            return "A multiple-choice question must not have a unit.";

        var options = r.Options;
        if (options.Count is < MinOptions or > MaxOptions)
            return $"A multiple-choice question must have between {MinOptions} and {MaxOptions} options.";

        if (options.Count(o => o.IsCorrect) != 1)
            return "A multiple-choice question must have exactly one correct option.";

        var labels = new HashSet<char>();
        foreach (var o in options)
        {
            if (string.IsNullOrWhiteSpace(o.Text))
                return "Every option must have text.";
            var label = char.ToUpperInvariant(o.Label);
            if (Array.IndexOf(ValidLabels, label) < 0)
                return "Option labels must be A, B, C or D.";
            if (!labels.Add(label))
                return "Option labels must be unique.";
        }

        return null;
    }

    private static string? ValidateEstimation(QuestionUpsertRequest r)
    {
        if (r.CorrectNumber is null)
            return "An estimation question must have a correct number.";
        if (r.Options.Count > 0)
            return "An estimation question must not have options.";
        return null;
    }
}
