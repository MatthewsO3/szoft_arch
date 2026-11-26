using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using QuizGame.Contracts;
using QuizGame.Server.Domain;
using QuizGame.Server.Hubs;

namespace QuizGame.Server.Services;

public sealed class RoundOptions
{
    public const string Section = "Rounds";

    /// <summary>Network allowance after the deadline (plan §7.3); later answers get AnswerTooLate.</summary>
    public int GraceMilliseconds { get; set; } = 300;
}

/// <summary>
/// Runs question rounds end to end (plan §7.1 RoundTimerService + the round part of the hub):
/// opens a round, pushes the right view to each audience, schedules the deadline, closes the round on
/// deadline / all answered / host request, broadcasts <c>RoundClosed</c> and records it.
/// The room's own lock makes the three closers race-safe: only the first one gets a result.
/// </summary>
public sealed class RoundCoordinator(
    IQuestionProvider questions,
    IHubContext<GameHub> hub,
    ConnectionRegistry registry,
    ISessionRecorder recorder,
    RoomCommandGate commands,
    PhaseCoordinator phases,
    TimeProvider time,
    IOptions<RoundOptions> options,
    ILogger<RoundCoordinator> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (Guid RoundId, CancellationTokenSource Cts)> _timers = [];

    // Serialize state changes AND their delivery so reconnect snapshots cannot overtake round events.
    private Task<T> SerializedAsync<T>(GameRoom room, Func<Task<T>> action) => commands.RunAsync(room, action);

    private TimeSpan Grace => TimeSpan.FromMilliseconds(Math.Max(0, options.Value.GraceMilliseconds));

    /// <summary>Host opens a round. Returns the error code to report, or null on success.</summary>
    public Task<string?> StartAsync(GameRoom room, SendQuestionRequest request, CancellationToken ct = default) =>
        SerializedAsync(room, () => StartCoreAsync(room, request, ct));

    private async Task<string?> StartCoreAsync(GameRoom room, SendQuestionRequest request, CancellationToken ct)
    {
        var question = await questions.GetActiveAsync(request.QuestionId, ct);
        if (question is null) return ErrorCodes.QuestionNotFound;

        var started = room.TryStartRound(question, request, time.GetUtcNow());
        if (!started.Ok)
        {
            logger.LogInformation("Round not started in room {Room}: {Error} {Detail}", room.Code, started.Error, started.Detail);
            return started.Error switch
            {
                StartRoundError.RoundActive => ErrorCodes.RoundInProgress,
                StartRoundError.NoPlayers => ErrorCodes.NotEnoughPlayers,
                StartRoundError.InvalidPhase => ErrorCodes.InvalidPhase,
                _ => ErrorCodes.InvalidRound,
            };
        }

        var round = started.Round!;
        ScheduleClose(room, round);
        await PublishStartAsync(room.Code, round);
        return null;
    }

    private async Task PublishStartAsync(string code, RoundInfo round)
    {
        var forPlayer = round.ForPlayer();

        // Host (and projector): full question including the correct answer.
        await hub.Clients.Group(GroupNames.Gm(code)).SendAsync(HubNames.Events.QuestionStarted, round.ForHost());
        await hub.Clients.Group(GroupNames.Gm(code)).SendAsync(HubNames.Events.AnswerCountChanged,
            new AnswerCount(0, round.ParticipantIds.Count));

        if (round.Kind == RoundKind.Battle)
        {
            // Only the two duellists get the question; everybody else is told who fights (plan §6.3).
            var duellists = registry.GetPlayerConnections(code, round.ParticipantIds.ToHashSet());
            await hub.Clients.Clients(duellists).SendAsync(HubNames.Events.QuestionStarted, forPlayer);
            await hub.Clients.GroupExcept(GroupNames.Players(code), duellists).SendAsync(
                HubNames.Events.BattleAnnounced,
                new BattleAnnouncement(round.AttackerId!.Value, round.DefenderId!.Value, round.TargetTerritoryIndex!.Value));
        }
        else
        {
            await hub.Clients.Group(GroupNames.Players(code)).SendAsync(HubNames.Events.QuestionStarted, forPlayer);
        }
    }

    /// <summary>Records a player's answer. Returns the error code to report, or null on success.</summary>
    public Task<string?> SubmitAsync(GameRoom room, Guid playerId, SubmitAnswerRequest request, DateTimeOffset receivedAt) =>
        SerializedAsync(room, () => SubmitCoreAsync(room, playerId, request, receivedAt));

    private async Task<string?> SubmitCoreAsync(GameRoom room, Guid playerId, SubmitAnswerRequest request, DateTimeOffset receivedAt)
    {
        var result = room.TrySubmitAnswer(playerId, request, receivedAt, Grace);
        if (!result.Ok)
        {
            return result.Status switch
            {
                SubmitStatus.NoActiveRound => ErrorCodes.NoActiveRound,
                SubmitStatus.NotParticipant => ErrorCodes.NotParticipant,
                SubmitStatus.AlreadyAnswered => ErrorCodes.AlreadyAnswered,
                SubmitStatus.TooLate => ErrorCodes.AnswerTooLate,
                _ => ErrorCodes.InvalidAnswer,
            };
        }

        // Progress only, never the answer content (plan §6.3).
        await hub.Clients.Group(GroupNames.Gm(room.Code)).SendAsync(
            HubNames.Events.AnswerCountChanged, new AnswerCount(result.Answered, result.Total));

        if (result.AllAnswered)
            await CloseCoreAsync(room, request.RoundId);
        return null;
    }

    /// <summary>Closes the round (host, deadline timer or all answered). False if it was already closed.</summary>
    public Task<bool> CloseAsync(GameRoom room, Guid? roundId = null) =>
        SerializedAsync(room, () => CloseCoreAsync(room, roundId));

    private async Task<bool> CloseCoreAsync(GameRoom room, Guid? roundId)
    {
        var closed = room.CloseRound(roundId, time.GetUtcNow());
        if (closed is null) return false;

        CancelTimer(room.Code, closed.Record.RoundId);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.RoundClosed, closed.Result);
        await recorder.RoundClosedAsync(room.SessionId, closed.Record);
        await phases.OnRoundClosedCoreAsync(room, closed); // base-order round: ask the first player for a base
        return true;
    }

    /// <summary>Stops the deadline timer of a room that is being closed or removed.</summary>
    public void Cancel(string roomCode)
    {
        CancelTimer(roomCode, null);
        phases.Cancel(roomCode);
    }

    /// <summary>One caller-specific snapshot restores open rounds, missed results and a pending base pick.</summary>
    public Task SendStateAsync(GameRoom room, IClientProxy caller, Guid? playerId) =>
        SerializedAsync(room, async () =>
        {
            await caller.SendAsync(HubNames.Events.RoomState,
                room.Snapshot(time.GetUtcNow(), playerId, forHost: playerId is null));
            await phases.SendPickStateCoreAsync(room, caller, playerId);
            return true;
        });

    private void ScheduleClose(GameRoom room, RoundInfo round)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_timers.Remove(room.Code, out var previous)) previous.Cts.Cancel();
            _timers[room.Code] = (round.RoundId, cts);
        }

        var delay = round.Deadline + Grace - time.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, time, cts.Token);
                await CloseAsync(room, round.RoundId);
            }
            catch (OperationCanceledException)
            {
                // round closed early or room removed
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Closing round {Round} in room {Room} failed", round.RoundId, room.Code);
            }
        });
    }

    // Only the timer of the given round is cancelled, so a late close of round N can never kill round N+1's timer.
    private void CancelTimer(string roomCode, Guid? roundId)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (_timers.TryGetValue(roomCode, out var entry) && (roundId is null || entry.RoundId == roundId))
            {
                _timers.Remove(roomCode);
                cts = entry.Cts;
            }
        }

        cts?.Cancel();
    }
}
