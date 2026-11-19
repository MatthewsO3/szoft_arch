namespace QuizGame.Contracts;

/// <summary>
/// Tunable room rules, set by the host in the Lobby (plan §4, §16). Every rule the
/// spec leaves open (D1–D11) lives here so the game can be reconfigured without code
/// changes. Defaults match the plan.
/// </summary>
public sealed record RoomSettings
{
    /// <summary>Map preset. Overridable; drives grid size (plan §4.4).</summary>
    public MapPreset MapPreset { get; init; } = MapPreset.Medium;

    /// <summary>Point value of a normal territory (plan §4.3).</summary>
    public int TerritoryValue { get; init; } = 100;

    /// <summary>Point value of a base territory (plan §4.3).</summary>
    public int BaseValue { get; init; } = 300;

    /// <summary>Winners per multiple-choice grab round (D2).</summary>
    public int WinnersPerRound { get; init; } = 1;

    /// <summary>Minimum connected players before the host may start (plan §4.2 Lobby).</summary>
    public int MinPlayers { get; init; } = 2;

    /// <summary>Seconds a player has to pick a territory before auto-assignment (plan §4.2).</summary>
    public int PickTimeoutSeconds { get; init; } = 20;

    /// <summary>Default per-question answer window when the question does not override it.</summary>
    public int DefaultQuestionSeconds { get; init; } = 20;

    /// <summary>Soft limit that makes the host UI suggest ending Conquest (D4).</summary>
    public int SuggestedMaxRounds { get; init; } = 10;

    /// <summary>Forbid choosing a base adjacent to another base during BaseSelection (optional rule, plan §4.2).</summary>
    public bool DisallowAdjacentBases { get; init; } = false;
}

/// <summary>Fixed map size presets (plan §4.4). Host may override dimensions.</summary>
public enum MapPreset
{
    Small,   // 5×5
    Medium,  // 6×8
    Large    // 8×10
}
