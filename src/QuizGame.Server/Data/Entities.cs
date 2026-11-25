namespace QuizGame.Server.Data;

/// <summary>Plan §5.1 GameMasters. <see cref="UsernameKey"/> is the normalised name used for the unique index.</summary>
public sealed class GameMasterEntity
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string UsernameKey { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One row per room that was created (history; live state stays in memory, plan D9).</summary>
public sealed class GameSessionEntity
{
    public Guid Id { get; set; }
    public string RoomCode { get; set; } = "";
    public Guid GameMasterId { get; set; }
    public GameMasterEntity? GameMaster { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public List<PlayerEntity> Players { get; set; } = [];
    public List<RoundEntity> Rounds { get; set; } = [];
}

public sealed class PlayerEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSessionEntity? Session { get; set; }
    public string Name { get; set; } = "";
    public string ColorHex { get; set; } = "";
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}

/// <summary>
/// One closed question round (plan §5.1 Rounds). QuestionId and the player / territory references are plain
/// values without foreign keys: Questions arrive with the question bank (M2-B) and Territories with the map (M3),
/// and a history write must never fail because a referenced row is missing.
/// </summary>
public sealed class RoundEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public GameSessionEntity? Session { get; set; }
    public int Seq { get; set; }
    public string Kind { get; set; } = "";
    public Guid QuestionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public DateTimeOffset ClosedAt { get; set; }
    public Guid? AttackerId { get; set; }
    public Guid? DefenderId { get; set; }
    public int? TargetTerritoryIndex { get; set; }
    public Guid? WinnerPlayerId { get; set; }
    public List<AnswerEntity> Answers { get; set; } = [];
}

/// <summary>One accepted answer (plan §5.1 Answers). Players who did not answer have no row.</summary>
public sealed class AnswerEntity
{
    public Guid Id { get; set; }
    public Guid RoundId { get; set; }
    public RoundEntity? Round { get; set; }
    public Guid PlayerId { get; set; }
    public string? ChosenOption { get; set; }
    public decimal? NumericValue { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool? IsCorrect { get; set; }
    public decimal? Distance { get; set; }
    public int Rank { get; set; }
}

/// <summary>Plan §5.1 Categories: a small lookup table with an int key and a unique name.</summary>
public sealed class CategoryEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<QuestionEntity> Questions { get; set; } = [];
}

/// <summary>
/// Plan §5.1 Questions. A multiple-choice question carries 2–4 <see cref="Options"/> with exactly one correct;
/// an estimation question carries <see cref="CorrectNumber"/> and no options (validated in the service layer and
/// by a DB check constraint, plan §5.2). Deleting is a soft delete (<see cref="IsActive"/> = false) so past
/// rounds keep their reference.
/// </summary>
public sealed class QuestionEntity
{
    public Guid Id { get; set; }
    public int CategoryId { get; set; }
    public CategoryEntity? Category { get; set; }
    public QuizGame.Contracts.QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public int Difficulty { get; set; }
    public int TimeLimitSeconds { get; set; }

    /// <summary>Estimation only: the correct numeric value. Null for multiple choice.</summary>
    public decimal? CorrectNumber { get; set; }

    /// <summary>Optional unit shown with an estimation answer, e.g. "km".</summary>
    public string? Unit { get; set; }

    /// <summary>Owning game master, or null for a question shared by everyone (plan §5.1).</summary>
    public Guid? OwnerId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public List<QuestionOptionEntity> Options { get; set; } = [];
}

/// <summary>Plan §5.1 QuestionOptions: one A–D choice of a multiple-choice question.</summary>
public sealed class QuestionOptionEntity
{
    public Guid Id { get; set; }
    public Guid QuestionId { get; set; }
    public QuestionEntity? Question { get; set; }
    public char Label { get; set; }
    public string Text { get; set; } = "";
    public bool IsCorrect { get; set; }
}
