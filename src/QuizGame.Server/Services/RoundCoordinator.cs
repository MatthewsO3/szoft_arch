using System.Collections.Concurrent;
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
/// Runs question rounds end to end (plan §7.1 + the round part of the hub). Per phase:
/// Conquest runs a claim stage (via <see cref="PhaseCoordinator"/>) then one multiple-choice question for the
/// claimers; Expansion runs one estimation question and hands a tile to the closest guess; Duel runs a
/// nominate → confirm → multiple-choice duel between attacker and defender.
/// The room's own lock makes the three closers (deadline, all-answered, host) race-safe.
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

    // While a Conquest claim stage runs, the question the host chose, to open once all claims are in.
    private readonly ConcurrentDictionary<string, Guid> _pendingConquestQuestion = new();

    private Task<T> SerializedAsync<T>(GameRoom room, Func<Task<T>> action) => commands.RunAsync(room, action);
    private TimeSpan Grace => TimeSpan.FromMilliseconds(Math.Max(0, options.Value.GraceMilliseconds));

    /// <summary>Wires the claim-stage completion callback. Called once at startup.</summary>
    public void Wire() => phases.OnClaimsReady = OpenConquestQuestionCoreAsync;

    /// <summary>
    /// Host sends a question. In Conquest this starts the claim stage and defers the question until claims are in;
    /// in Expansion it opens an estimation round; in Duel it confirms the nomination and opens the duel.
    /// </summary>
    public Task<string?> SendQuestionAsync(GameRoom room, SendQuestionRequest request, CancellationToken ct = default) =>
        SerializedAsync(room, async () =>
        {
            var question = await questions.GetActiveAsync(request.QuestionId, ct);
            if (question is null) return ErrorCodes.QuestionNotFound;

            return room.Phase switch
            {
                GamePhase.Conquest => await StartConquestAsync(room, question),
                GamePhase.Expansion => Report(room, room.TryStartExpansionRound(question, time.GetUtcNow())) is { } e ? e : await AfterStartAsync(room),
                GamePhase.Duel => await StartDuelAsync(room, question),
                _ => ErrorCodes.InvalidPhase,
            };
        });

    private async Task<string?> StartConquestAsync(GameRoom room, QuestionDto question)
    {
        if (question.Type != QuestionType.MultipleChoice) return ErrorCodes.InvalidRound;
        if (room.HasOpenRound || room.ClaimsOpen) return ErrorCodes.RoundInProgress;
        if (room.PhaseRoundsExhausted) return ErrorCodes.PhaseNotComplete;

        _pendingConquestQuestion[room.Code] = question.Id;
        await phases.StartConquestClaimsCoreAsync(room); // asks the first claimer; OpenConquestQuestionCoreAsync fires when all are in
        await phases.PublishQuestionAvailabilityCoreAsync(room);
        return null;
    }

    // Called by PhaseCoordinator.OnClaimsReady once every player has claimed a tile.
    private async Task OpenConquestQuestionCoreAsync(GameRoom room)
    {
        if (!_pendingConquestQuestion.TryRemove(room.Code, out var questionId)) return;
        var question = await questions.GetActiveAsync(questionId, CancellationToken.None);
        if (question is null) return;

        var started = room.TryStartConquestRound(question, time.GetUtcNow());
        if (!started.Ok)
        {
            logger.LogWarning("Conquest question not opened in room {Room}: {Error}", room.Code, started.Error);
            return;
        }

        ScheduleClose(room, started.Round!);
        await PublishStartAsync(room, started.Round!);
    }

    private async Task<string?> StartDuelAsync(GameRoom room, QuestionDto question)
    {
        var started = room.TryStartDuelRound(question, time.GetUtcNow());
        if (Report(room, started) is { } error) return error;
        ScheduleClose(room, started.Round!);
        await PublishStartAsync(room, started.Round!);
        return null;
    }

    private async Task<string?> AfterStartAsync(GameRoom room)
    {
        if (room.GetOpenRound() is { } open)
        {
            ScheduleClose(room, open.Round);
            await PublishStartAsync(room, open.Round);
        }

        return null;
    }

    private string? Report(GameRoom room, StartRoundResult started)
    {
        if (started.Ok) return null;
        logger.LogInformation("Round not started in room {Room}: {Error} {Detail}", room.Code, started.Error, started.Detail);
        return started.Error switch
        {
            StartRoundError.RoundActive => ErrorCodes.RoundInProgress,
            StartRoundError.PickInProgress => ErrorCodes.RoundInProgress,
            StartRoundError.NoPlayers => ErrorCodes.NotEnoughPlayers,
            StartRoundError.InvalidPhase => ErrorCodes.InvalidPhase,
            StartRoundError.RoundsExhausted => ErrorCodes.PhaseNotComplete,
            StartRoundError.MapFull => ErrorCodes.PhaseNotComplete,
            StartRoundError.NoTarget => ErrorCodes.NoNomination,
            _ => ErrorCodes.InvalidRound,
        };
    }

    /// <summary>An attacker nominates a duel target; the host is told and later confirms it (plan §4.2).</summary>
    public Task<string?> NominateTargetAsync(GameRoom room, Guid attackerId, int targetIndex) =>
        SerializedAsync(room, async () =>
        {
            var result = room.TryNominateTarget(attackerId, targetIndex);
            if (!result.Ok)
            {
                return result.Error switch
                {
                    NominateError.InvalidPhase => ErrorCodes.InvalidPhase,
                    NominateError.RoundInProgress => ErrorCodes.RoundInProgress,
                    NominateError.NotYourTurn => ErrorCodes.NotYourTurn,
                    _ => ErrorCodes.InvalidTarget,
                };
            }

            await phases.PublishDuelNominationCoreAsync(room, result.Nomination!);
            return null;
        });

    /// <summary>Host confirms a pending nomination and opens the duel with the chosen multiple-choice question.</summary>
    public Task<string?> ConfirmDuelAsync(GameRoom room, Guid questionId, CancellationToken ct = default) =>
        SerializedAsync(room, async () =>
        {
            if (room.CurrentNomination() is null) return ErrorCodes.NoNomination;
            var question = await questions.GetActiveAsync(questionId, ct);
            if (question is null) return ErrorCodes.QuestionNotFound;
            return await StartDuelAsync(room, question);
        });

    private async Task PublishStartAsync(GameRoom room, RoundInfo round)
    {
        var code = room.Code;
        var forPlayer = round.ForPlayer();

        await phases.PublishQuestionAvailabilityCoreAsync(room);
        await hub.Clients.Group(GroupNames.Gm(code)).SendAsync(HubNames.Events.QuestionStarted, round.ForHost());
        await hub.Clients.Group(GroupNames.Gm(code)).SendAsync(HubNames.Events.AnswerCountChanged,
            new AnswerCount(0, round.ParticipantIds.Count));

        if (round.Kind == RoundKind.Duel)
        {
            // Only the two duellists get the question; everybody else is told who fights (plan §4.2).
            var duellists = registry.GetPlayerConnections(code, round.ParticipantIds.ToHashSet());
            await hub.Clients.Clients(duellists).SendAsync(HubNames.Events.QuestionStarted, forPlayer);
            await hub.Clients.GroupExcept(GroupNames.Players(code), duellists).SendAsync(
                HubNames.Events.DuelAnnounced,
                new DuelAnnouncement(round.AttackerId!.Value, round.DefenderId!.Value, round.TargetTerritoryIndex!.Value));
        }
        else if (round.Kind == RoundKind.Conquest)
        {
            // Only the players who claimed a tile answer.
            var claimers = registry.GetPlayerConnections(code, round.ParticipantIds.ToHashSet());
            await hub.Clients.Clients(claimers).SendAsync(HubNames.Events.QuestionStarted, forPlayer);
        }
        else
        {
            await hub.Clients.Group(GroupNames.Players(code)).SendAsync(HubNames.Events.QuestionStarted, forPlayer);
        }
    }

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

        await hub.Clients.Group(GroupNames.Gm(room.Code)).SendAsync(
            HubNames.Events.AnswerCountChanged, new AnswerCount(result.Answered, result.Total));

        if (result.AllAnswered)
            await CloseCoreAsync(room, request.RoundId);
        return null;
    }

    public Task<bool> CloseAsync(GameRoom room, Guid? roundId = null) =>
        SerializedAsync(room, () => CloseCoreAsync(room, roundId));

    private async Task<bool> CloseCoreAsync(GameRoom room, Guid? roundId)
    {
        var closed = room.CloseRound(roundId, time.GetUtcNow());
        if (closed is null) return false;

        CancelTimer(room.Code, closed.Record.RoundId);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.RoundClosed, closed.Result);
        await recorder.RoundClosedAsync(room.SessionId, closed.Record);
        await phases.OnRoundResolvedCoreAsync(room, closed); // map change / expansion pick follow-up
        return true;
    }

    public void Cancel(string roomCode)
    {
        _pendingConquestQuestion.TryRemove(roomCode, out _);
        CancelTimer(roomCode, null);
        phases.Cancel(roomCode);
    }

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
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Closing round {Round} in room {Room} failed", round.RoundId, room.Code); }
        });
    }

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
