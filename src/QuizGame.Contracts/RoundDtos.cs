namespace QuizGame.Contracts;

/// <summary>Host-only round presentation. Never send this payload to players.</summary>
public sealed record QuestionForHost(Guid RoundId, QuestionDto Question, DateTimeOffset ServerDeadlineUtc);

/// <summary>Returned by SubmitAnswer only after the server has recorded the answer.</summary>
public sealed record AnswerAccepted(Guid RoundId);

/// <summary>
/// Args for the host opening a round (plan §6.3 SendQuestion). The round kind comes from the phase, so only the
/// question id is needed; a Duel also carries the attacker's confirmed target (set by the server from the nomination).
/// </summary>
public sealed record SendQuestionRequest
{
    public Guid QuestionId { get; init; }
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

    /// <summary>The territory this player claimed and kept this round, if any (Conquest), or was handed (Expansion / Duel).</summary>
    public int? WonTerritoryIndex { get; init; }

    /// <summary>Bonus points this player earned this round (Duel defender / draw), if any.</summary>
    public int? BonusAwarded { get; init; }
}

/// <summary>Round result broadcast to the room (plan §6.3 RoundClosed).</summary>
public sealed record RoundClosedDto
{
    public Guid RoundId { get; init; }
    public RoundKind Kind { get; init; }
    public IReadOnlyList<AnswerResult> Ranking { get; init; } = [];
    public char? CorrectOption { get; init; }
    public decimal? CorrectNumber { get; init; }

    /// <summary>Players who won a territory this round.</summary>
    public IReadOnlyList<Guid> WinnerPlayerIds { get; init; } = [];
}

/// <summary>Sent to the current picker/claimer (plan §6.3 PickRequested). The purpose depends on the phase.</summary>
public sealed record PickRequest(IReadOnlyList<int> ValidTerritoryIndices, DateTimeOffset PickDeadlineUtc, PickPurpose Purpose);

/// <summary>Sent to everyone except the current picker (plan §6.3 PickWaiting): who is choosing and by when.</summary>
public sealed record PickWaitingDto(PlayerSummary Picker, DateTimeOffset PickDeadlineUtc, PickPurpose Purpose);

/// <summary>Why a player is being asked to choose a territory.</summary>
public enum PickPurpose
{
    /// <summary>BaseSelection: the chosen tile becomes the player's base.</summary>
    Base,

    /// <summary>Conquest: the chosen tile is claimed; it is only won if the player then answers the question correctly.</summary>
    ConquestClaim,

    /// <summary>Expansion: the estimation winner takes the chosen free tile.</summary>
    Expansion,

    /// <summary>Duel: the attacker chooses an adjacent enemy tile to attack.</summary>
    DuelTarget
}

/// <summary>Spectator info for a duel the recipient is not part of (plan §6.3).</summary>
public sealed record DuelAnnouncement(Guid AttackerId, Guid DefenderId, int TargetTerritoryIndex);

/// <summary>Host-only info that the active player has nominated a duel target, awaiting confirmation (plan §4.2).</summary>
public sealed record DuelNominationDto(Guid AttackerId, Guid DefenderId, int TargetTerritoryIndex);

/// <summary>Args for the host confirming a nominated duel (plan §4.2): the multiple-choice question to duel with.</summary>
public sealed record ConfirmDuelRequest
{
    public Guid QuestionId { get; init; }
}

/// <summary>Host-only progress: how many of the eligible players have answered.</summary>
public sealed record AnswerCount(int Answered, int Total);

/// <summary>Map change broadcast: only the cells that changed, plus fresh scores (plan §6.3 MapUpdated).</summary>
public sealed record MapUpdate(IReadOnlyList<TerritoryDto> ChangedTerritories, IReadOnlyList<ScoreEntry> Scoreboard);

/// <summary>Standardized error pushed to a caller (plan §6.3 Error).</summary>
public sealed record ErrorDto(string Code, string Message);
