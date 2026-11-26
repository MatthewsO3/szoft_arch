namespace QuizGame.Contracts;

/// <summary>
/// Canonical SignalR method and event names, shared by the server and the WPF client.
/// The JS player client mirrors these in <c>wwwroot/js/hubNames.js</c>; keep the two in
/// sync and documented in <c>docs/api-contract.md</c>. See plan §6.3.
/// </summary>
public static class HubNames
{
    /// <summary>The single game hub route.</summary>
    public const string HubPath = "/hubs/game";

    /// <summary>Client → server method names.</summary>
    public static class Methods
    {
        public const string HostAttach = "HostAttach";
        public const string KickPlayer = "KickPlayer";
        public const string UpdateSettings = "UpdateSettings";
        public const string StartGame = "StartGame";
        public const string AdvancePhase = "AdvancePhase";
        public const string SendQuestion = "SendQuestion";
        public const string CloseRound = "CloseRound";
        public const string ConfirmDuel = "ConfirmDuel";
        public const string SubmitAnswer = "SubmitAnswer";
        public const string PickTerritory = "PickTerritory";
        public const string NominateTarget = "NominateTarget";
        public const string Rejoin = "Rejoin";
        public const string LeaveRoom = "LeaveRoom";
        public const string Ping = "Ping";
    }

    /// <summary>Server → client event names.</summary>
    public static class Events
    {
        public const string RoomState = "RoomState";
        public const string PlayerJoined = "PlayerJoined";
        public const string PlayerLeft = "PlayerLeft";
        public const string PlayerConnectionChanged = "PlayerConnectionChanged";
        public const string PhaseChanged = "PhaseChanged";
        public const string MapUpdated = "MapUpdated";
        public const string QuestionStarted = "QuestionStarted";
        public const string DuelAnnounced = "DuelAnnounced";
        public const string DuelNominated = "DuelNominated";
        public const string AnswerCountChanged = "AnswerCountChanged";
        public const string RoundClosed = "RoundClosed";
        public const string PickRequested = "PickRequested";
        public const string PickWaiting = "PickWaiting";
        public const string ScoreboardUpdated = "ScoreboardUpdated";
        public const string GameFinished = "GameFinished";
        public const string Kicked = "Kicked";
        public const string RoomClosed = "RoomClosed";
        public const string Error = "Error";
    }
}
