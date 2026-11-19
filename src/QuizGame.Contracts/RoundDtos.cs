namespace QuizGame.Contracts;

/// <summary>Args for the host opening a round (plan §6.3 SendQuestion).</summary>
public sealed record SendQuestionRequest
{
    public Guid QuestionId { get; init; }
    public RoundKind Kind { get; init; }

    /// <summary>Battle rounds only: who attacks and the territory under attack.</summary>
    public Guid? AttackerId { get; init; }
    public int? TargetTerritoryIndex { get; init; }
}

/// <summary>Question pushed to players: no correct answer (plan §6.3 rule).</summary>
public sealed record QuestionForPlayer
{
    public Guid RoundId { get; init; }
    public QuestionType Type { get; init; }
    public DateTimeOffset ServerDeadlineUtc { get; init; }

    /// <summary>Option labels + text for multiple choice. Empty for estimation.</summary>
    public IReadOnlyList<OptionDto> Options { get; init; } = [];

    /// <summary>Unit hint for estimation questions, e.g. "km".</summary>
    public string? Unit { get; init; }
}

/// <summary>A player's answer intent (plan §6.3 SubmitAnswer). Exactly one field set by type.</summary>
public sealed record SubmitAnswerRequest
{
    public Guid RoundId { get; init; }
    public char? Option { get; init; }
    public decimal? Number { get; init; }
}

/// <summary>One line of a closed round's ranking (plan §6.3 RoundClosed).</summary>
public sealed record AnswerResult
{
    public Guid PlayerId { get; init; }
    public string Name { get; init; } = "";
    public int Rank { get; init; }
    public bool? IsCorrect { get; init; }

    /// <summary>Milliseconds from round start to receipt (multiple choice).</summary>
    public long? ElapsedMs { get; init; }

    /// <summary>Absolute distance from the correct number (estimation).</summary>
    public decimal? Distance { get; init; }
}

/// <summary>Round result broadcast to the room (plan §6.3 RoundClosed).</summary>
public sealed record RoundClosedDto
{
    public Guid RoundId { get; init; }
    public RoundKind Kind { get; init; }
    public IReadOnlyList<AnswerResult> Ranking { get; init; } = [];
    public char? CorrectOption { get; init; }
    public decimal? CorrectNumber { get; init; }

    /// <summary>Winners in order (one for a battle; WinnersPerRound for a grab).</summary>
    public IReadOnlyList<Guid> WinnerPlayerIds { get; init; } = [];
}

/// <summary>Sent to the current picker (plan §6.3 PickRequested).</summary>
public sealed record PickRequest(IReadOnlyList<int> ValidTerritoryIndices, DateTimeOffset PickDeadlineUtc);

/// <summary>Spectator info for a battle the recipient is not part of (plan §6.3 BattleAnnounced).</summary>
public sealed record BattleAnnouncement(Guid AttackerId, Guid DefenderId, int TargetTerritoryIndex);

/// <summary>Host-only progress: how many of the eligible players have answered.</summary>
public sealed record AnswerCount(int Answered, int Total);

/// <summary>Map change broadcast: only the cells that changed, plus fresh scores (plan §6.3 MapUpdated).</summary>
public sealed record MapUpdate(IReadOnlyList<TerritoryDto> ChangedTerritories, IReadOnlyList<ScoreEntry> Scoreboard);

/// <summary>Standardized error pushed to a caller (plan §6.3 Error).</summary>
public sealed record ErrorDto(string Code, string Message);
