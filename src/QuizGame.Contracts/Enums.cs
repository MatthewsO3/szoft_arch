namespace QuizGame.Contracts;

/// <summary>Top-level game phase. Advanced only by the game master; transitions are
/// validated server-side (no skipping, no going back). See docs/implementation_plan.md §4.2.</summary>
public enum GamePhase
{
    Lobby,
    BaseSelection,
    Conquest,
    Expansion,
    Duel,
    Results
}

/// <summary>Question type. Multiple choice decides Conquest claims and Duels (correct/not);
/// estimation decides Expansion picks (closest guess). See plan §4.5.</summary>
public enum QuestionType
{
    MultipleChoice,
    Estimation
}

/// <summary>What a round is for. See plan §5 (Rounds.Kind).</summary>
public enum RoundKind
{
    /// <summary>Conquest round: everyone claims a free tile, then a shared multiple-choice question; every correct claimer keeps their tile.</summary>
    Conquest,

    /// <summary>Expansion round: one estimation question; the closest guess picks a free tile. Repeats until the map is full.</summary>
    Expansion,

    /// <summary>Duel round: the active player attacks an adjacent enemy tile with a multiple-choice question (attacker vs defender).</summary>
    Duel
}
