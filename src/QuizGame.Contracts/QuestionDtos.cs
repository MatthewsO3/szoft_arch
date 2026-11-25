namespace QuizGame.Contracts;

/// <summary>A question-bank category (plan §5.1 Categories).</summary>
public sealed record CategoryDto(int Id, string Name);

/// <summary>Create/update payload for a category (plan §6.2 POST/PUT /categories).</summary>
public sealed record CategoryUpsertRequest
{
    public string Name { get; init; } = "";
}

/// <summary>One multiple-choice option (plan §5.1 QuestionOptions).</summary>
public sealed record OptionDto(Guid Id, char Label, string Text);

/// <summary>
/// Full question as the game master sees it in the bank, including the correct answer.
/// NEVER sent to player clients before a round closes (plan §6.3 rule).
/// </summary>
public sealed record QuestionDto
{
    public Guid Id { get; init; }
    public int CategoryId { get; init; }
    public QuestionType Type { get; init; }
    public string Text { get; init; } = "";
    public int Difficulty { get; init; }
    public int TimeLimitSeconds { get; init; }

    /// <summary>Multiple-choice options (2–4). Empty for estimation questions.</summary>
    public IReadOnlyList<OptionDto> Options { get; init; } = [];

    /// <summary>Label of the correct option (multiple choice only).</summary>
    public char? CorrectOption { get; init; }

    /// <summary>Correct numeric value (estimation only).</summary>
    public decimal? CorrectNumber { get; init; }

    /// <summary>Optional unit shown with estimation questions, e.g. "km".</summary>
    public string? Unit { get; init; }
}

/// <summary>Create/update payload for a question (validated by type, plan §5.2).</summary>
public sealed record QuestionUpsertRequest
{
    public int CategoryId { get; init; }
    public QuestionType Type { get; init; }
    public string Text { get; init; } = "";
    public int Difficulty { get; init; } = 1;
    public int TimeLimitSeconds { get; init; } = 20;
    public IReadOnlyList<OptionUpsert> Options { get; init; } = [];
    public decimal? CorrectNumber { get; init; }
    public string? Unit { get; init; }
}

public sealed record OptionUpsert(char Label, string Text, bool IsCorrect);

/// <summary>One page of a filtered question list (plan §6.2 GET /questions).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
