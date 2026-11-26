using QuizGame.Contracts;

namespace QuizGame.Server.Domain;

/// <summary>Range checks for <see cref="RoomSettings"/>, shared by room creation (REST) and <c>UpdateSettings</c> (hub).</summary>
public static class RoomSettingsRules
{
    public static bool IsValid(RoomSettings s) =>
        Enum.IsDefined(s.MapPreset) &&
        s.TerritoryValue > 0 && s.BaseValue > 0 && s.BaseHealth >= 1 &&
        s.ConquestRounds >= 1 && s.DuelRounds >= 1 &&
        s.DefenderBonus >= 0 && s.DrawBonus >= 0 && s.MinPlayers >= 1 &&
        s.PickTimeoutSeconds > 0 && s.DefaultQuestionSeconds > 0;
}
