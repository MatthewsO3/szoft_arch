namespace QuizGame.Contracts;

/// <summary>
/// Tunable room rules, set by the host in the Lobby (plan §4, §16). Every rule the
/// game leaves open lives here so the game can be reconfigured without code changes.
/// Defaults match the plan.
/// </summary>
public sealed record RoomSettings
{
    /// <summary>Map preset. Overridable; drives grid size (plan §4.4).</summary>
    public MapPreset MapPreset { get; init; } = MapPreset.Medium;

    /// <summary>Point value of a normal territory (plan §4.3).</summary>
    public int TerritoryValue { get; init; } = 100;

    /// <summary>Point value of a base territory (plan §4.3).</summary>
    public int BaseValue { get; init; } = 300;

    /// <summary>Hit points of a base: it must be won in a Duel this many times before it is captured (plan §4.2 Duel).</summary>
    public int BaseHealth { get; init; } = 3;

    /// <summary>Maximum Conquest rounds; a full map ends the phase earlier (plan §4.2 Conquest).</summary>
    public int ConquestRounds { get; init; } = 10;

    /// <summary>Maximum number of Duel rounds before the phase ends (plan §4.2 Duel); the phase may also end when one player holds every base.</summary>
    public int DuelRounds { get; init; } = 10;

    /// <summary>Bonus points a defender earns for a correct answer in a Duel (plan §4.3).</summary>
    public int DefenderBonus { get; init; } = 100;

    /// <summary>Bonus points each duellist earns when attacker and defender both answer correctly (plan §4.3).</summary>
    public int DrawBonus { get; init; } = 50;

    /// <summary>Minimum connected players before the host may start (plan §4.2 Lobby).</summary>
    public int MinPlayers { get; init; } = 2;

    /// <summary>Seconds a player has to pick a territory before auto-assignment (plan §4.2).</summary>
    public int PickTimeoutSeconds { get; init; } = 20;

    /// <summary>Default per-question answer window when the question does not override it.</summary>
    public int DefaultQuestionSeconds { get; init; } = 20;

    /// <summary>Forbid choosing a base adjacent to another base during BaseSelection (enabled by default, plan §4.2).</summary>
    public bool DisallowAdjacentBases { get; init; } = true;
}

/// <summary>Fixed map size presets (plan §4.4). Host may override dimensions.</summary>
public enum MapPreset
{
    Small,   // 5×5
    Medium,  // 6×8
    Large    // 8×10
}
