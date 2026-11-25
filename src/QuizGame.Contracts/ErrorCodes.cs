namespace QuizGame.Contracts;

/// <summary>
/// Stable machine-readable error codes: <c>ProblemDetails.code</c> on REST and <c>ErrorDto.Code</c> on
/// SignalR. Keep in sync with docs/api-contract.md §3 and wwwroot/js/hubNames.js.
/// </summary>
public static class ErrorCodes
{
    public const string InvalidCredentials = "InvalidCredentials";
    public const string RoomNotFound = "RoomNotFound";
    public const string PlayerNotFound = "PlayerNotFound";
    public const string InvalidName = "InvalidName";
    public const string NameTaken = "NameTaken";
    public const string RoomFull = "RoomFull";
    public const string NotInLobby = "NotInLobby";
    public const string InvalidSettings = "InvalidSettings";
    public const string NotAuthorized = "NotAuthorized";
    public const string NotAttached = "NotAttached";

    // Question rounds (plan §4.2, §4.5, §7.3)
    public const string InvalidPhase = "InvalidPhase";
    public const string NotEnoughPlayers = "NotEnoughPlayers";
    public const string AnswerTooLate = "AnswerTooLate";
    public const string AlreadyAnswered = "AlreadyAnswered";
    public const string QuestionNotFound = "QuestionNotFound";
    public const string RoundInProgress = "RoundInProgress";
    public const string NoActiveRound = "NoActiveRound";
    public const string InvalidRound = "InvalidRound";
    public const string InvalidAnswer = "InvalidAnswer";
    public const string NotParticipant = "NotParticipant";

    // Question bank (REST)
    public const string CategoryNotFound = "CategoryNotFound";
    public const string CategoryNameTaken = "CategoryNameTaken";
    public const string CategoryInUse = "CategoryInUse";
    public const string InvalidQuestion = "InvalidQuestion";
    public const string InvalidCategory = "InvalidCategory";
}
