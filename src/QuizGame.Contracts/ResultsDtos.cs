namespace QuizGame.Contracts;

/// <summary>One player's final placement (plan §4.2 Results; ties share a rank).</summary>
public sealed record FinalStanding
{
    public int Rank { get; init; }
    public Guid PlayerId { get; init; }
    public string Name { get; init; } = "";
    public int Score { get; init; }
    public int TerritoryCount { get; init; }
}

/// <summary>Broadcast when the game ends (plan §6.3 GameFinished).</summary>
public sealed record GameFinishedDto(IReadOnlyList<FinalStanding> Standings, MapDto FinalMap);

/// <summary>Persisted results for a completed session (plan §6.2 GET /sessions/{id}/results).</summary>
public sealed record SessionResultsDto
{
    public Guid SessionId { get; init; }
    public string RoomCode { get; init; } = "";
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public IReadOnlyList<FinalStanding> Standings { get; init; } = [];
    public IReadOnlyList<RoundClosedDto> Rounds { get; init; } = [];
}
