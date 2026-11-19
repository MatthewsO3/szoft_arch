namespace QuizGame.Contracts;

/// <summary>Top-level game phase. Advanced only by the game master; transitions are
/// validated server-side (no skipping, no going back). See plan §4.2.</summary>
public enum GamePhase
{
    Lobby,
    BaseSelection,
    Conquest,
    Results
}

/// <summary>Question type. Multiple choice decides free-territory capture (fastest
/// correct); estimation decides base order and battles (closest guess). See plan §4.5.</summary>
public enum QuestionType
{
    MultipleChoice,
    Estimation
}

/// <summary>What a round is for. See plan §5 (Rounds.Kind).</summary>
public enum RoundKind
{
    /// <summary>Base-selection estimation round that establishes pick order.</summary>
    BaseOrder,

    /// <summary>Conquest multiple-choice round: fastest correct grabs a free territory.</summary>
    Grab,

    /// <summary>Conquest estimation duel over an owned territory (attacker vs defender).</summary>
    Battle
}
