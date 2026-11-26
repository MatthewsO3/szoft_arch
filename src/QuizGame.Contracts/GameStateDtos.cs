namespace QuizGame.Contracts;

/// <summary>A player as shown to everyone in the room (plan §6.3 player summary).</summary>
public sealed record PlayerSummary
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string ColorHex { get; init; } = "";
    public bool Connected { get; init; }

    /// <summary>Pick order from base selection (1-based); null until assigned.</summary>
    public int? BaseRank { get; init; }
}

/// <summary>One map cell. Layout-agnostic: the domain only needs Id and Neighbours (plan §4.4).</summary>
public sealed record TerritoryDto
{
    public int Index { get; init; }
    public int Row { get; init; }
    public int Col { get; init; }
    public int Value { get; init; }
    public bool IsBase { get; init; }

    /// <summary>Remaining hit points. A base starts at <c>BaseHealth</c>; a normal territory is 1 (plan §4.2 Duel).</summary>
    public int Health { get; init; }

    /// <summary>Owner player id, or null if free.</summary>
    public Guid? OwnerPlayerId { get; init; }
}

/// <summary>Grid dimensions plus all cells (plan §4.4).</summary>
public sealed record MapDto(int Rows, int Cols, IReadOnlyList<TerritoryDto> Territories);

/// <summary>One player's current score: territory value plus accumulated Duel bonus points (plan §4.3).</summary>
public sealed record ScoreEntry(Guid PlayerId, string Name, int Score, int TerritoryCount);

/// <summary>Public view of the round in progress (no correct answer).</summary>
public sealed record CurrentRoundDto
{
    public Guid RoundId { get; init; }
    public RoundKind Kind { get; init; }
    public QuestionType QuestionType { get; init; }
    public DateTimeOffset ServerDeadlineUtc { get; init; }

    /// <summary>Attacker / defender / target for Duel rounds; null otherwise.</summary>
    public Guid? AttackerId { get; init; }
    public Guid? DefenderId { get; init; }
    public int? TargetTerritoryIndex { get; init; }
}

/// <summary>
/// A pending territory choice (plan §4.2). <see cref="ValidTerritoryIndices"/> is only filled for the player whose
/// turn it is and empty for everybody else. <see cref="Purpose"/> says what the choice is for.
/// </summary>
public sealed record CurrentPickDto
{
    public Guid PickerPlayerId { get; init; }
    public DateTimeOffset PickDeadlineUtc { get; init; }
    public IReadOnlyList<int> ValidTerritoryIndices { get; init; } = [];
    public PickPurpose Purpose { get; init; }
}

/// <summary>
/// Full room snapshot sent on (re)connect (plan §6.3 RoomState). The server's current
/// UTC lets clients compute a clock offset for the countdown (plan §7.3).
/// </summary>
public sealed record RoomStateDto
{
    public string RoomCode { get; init; } = "";
    public GamePhase Phase { get; init; }
    public RoomSettings Settings { get; init; } = new();
    public IReadOnlyList<PlayerSummary> Players { get; init; } = [];

    /// <summary>Current turn order (player ids), front first. Empty before the game starts.</summary>
    public IReadOnlyList<Guid> TurnOrder { get; init; } = [];

    /// <summary>1-based number of the current Conquest or Duel round, or null outside those phases.</summary>
    public int? RoundNumber { get; init; }

    /// <summary>Total rounds configured for the current phase (Conquest or Duel), or null.</summary>
    public int? RoundsTotal { get; init; }

    public MapDto? Map { get; init; }
    public CurrentRoundDto? CurrentRound { get; init; }
    /// <summary>Territory choice in progress (base, conquest claim, expansion or duel target), otherwise null.</summary>
    public CurrentPickDto? CurrentPick { get; init; }
    /// <summary>Caller-specific active question. Players must never receive HostQuestion.</summary>
    public QuestionForPlayer? PlayerQuestion { get; init; }
    public QuestionForHost? HostQuestion { get; init; }
    /// <summary>Whether the calling player has an accepted answer in the current round.</summary>
    public bool HasAnswered { get; init; }
    public AnswerCount? AnswerCount { get; init; }
    /// <summary>Latest completed round, for recovery after missing RoundClosed.</summary>
    public RoundClosedDto? LastRoundResult { get; init; }
    public IReadOnlyList<ScoreEntry> Scoreboard { get; init; } = [];
    public DateTimeOffset ServerUtcNow { get; init; }
}
