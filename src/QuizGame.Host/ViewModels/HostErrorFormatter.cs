using QuizGame.Contracts;

namespace QuizGame.Host.ViewModels;

/// <summary>Formats REST and SignalR hub errors into concise user-facing messages.</summary>
public static class HostErrorFormatter
{
    private static readonly (string Token, string Message)[] ErrorMappings =
    [
        (ErrorCodes.PhaseNotComplete, "The phase cannot advance yet, or its rounds are already complete. Finish any pending territory picks and check the current phase. Expansion must fill the map before Duel."),
        (ErrorCodes.BaseSelectionIncomplete, "Wait until every player has picked a base."),
        (ErrorCodes.InvalidRound, "This question cannot be used for the current round. Conquest and Duel require multiple-choice questions; Expansion requires estimation questions. If the type is correct, check the question's answer data and the duel target."),
        (ErrorCodes.NotEnoughPlayers, "Not enough connected players to start the game."),
        (ErrorCodes.InvalidPhase, "This action is not allowed in the current phase."),
        ("InvalidTransition", "That phase transition is not allowed."),
        (ErrorCodes.InvalidSettings, "Room settings contain an invalid or out-of-range value."),
        (ErrorCodes.RoundInProgress, "Close the active round before doing that."),
        (ErrorCodes.NoActiveRound, "There is no active round to close."),
        (ErrorCodes.NotAttached, "Host connection is not attached to a room."),
        (ErrorCodes.RoomNotFound, "This room no longer exists."),
        (ErrorCodes.NotAuthorized, "You are not authorized for this room."),
        ("Method does not exist", "This action is not supported by the connected server yet.")
    ];

    public static string Format(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return FormatMessage(ex.Message);
    }

    public static string FormatMessage(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return "An unexpected error occurred.";

        var trimmed = rawMessage.Trim();
        foreach (var (token, message) in ErrorMappings)
        {
            if (trimmed.Contains(token, StringComparison.OrdinalIgnoreCase))
                return message;
        }

        return trimmed;
    }
}
