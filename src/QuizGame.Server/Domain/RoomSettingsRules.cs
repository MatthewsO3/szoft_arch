using QuizGame.Contracts;

namespace QuizGame.Server.Domain;

/// <summary>Range checks for <see cref="RoomSettings"/>, shared by room creation (REST) and <c>UpdateSettings</c> (hub).</summary>
public static class RoomSettingsRules
{
    public static bool IsValid(RoomSettings s) =>
        Enum.IsDefined(s.MapPreset) &&
        s.TerritoryValue > 0 && s.BaseValue > 0 && s.WinnersPerRound >= 1 && s.MinPlayers >= 1 &&
        s.PickTimeoutSeconds > 0 && s.DefaultQuestionSeconds > 0 && s.SuggestedMaxRounds >= 1;
}
