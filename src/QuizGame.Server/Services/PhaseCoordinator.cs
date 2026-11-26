using Microsoft.AspNetCore.SignalR;
using QuizGame.Contracts;
using QuizGame.Server.Domain;
using QuizGame.Server.Hubs;

namespace QuizGame.Server.Services;

/// <summary>
/// Phase changes, room settings, turn order and the territory-pick flow (plan §4.2, §7.1): starts the game and
/// generates the map, advances phases, drives base picks, Conquest claims, Expansion picks and the pick timeout,
/// and pushes <c>PhaseChanged</c> / <c>MapUpdated</c> / <c>PickRequested</c> / <c>PickWaiting</c>.
/// Every public method takes the room's command gate; <c>…CoreAsync</c> methods expect the caller to hold it.
/// </summary>
public sealed class PhaseCoordinator(
    IHubContext<GameHub> hub,
    ConnectionRegistry registry,
    ISessionRecorder recorder,
    RoomCommandGate gate,
    TimeProvider time,
    ILogger<PhaseCoordinator> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Seq, CancellationTokenSource Cts)> _timers = [];

    // RoundCoordinator calls back here after a Conquest claim stage fills so it can open the question.
    public Func<GameRoom, Task>? OnClaimsReady { get; set; }

    // ---- hub commands ----

    public Task<string?> UpdateSettingsAsync(GameRoom room, RoomSettings settings) =>
        gate.RunAsync(room, () => Task.FromResult(Code(room.TryUpdateSettings(settings))));

    public Task<string?> StartGameAsync(GameRoom room) =>
        gate.RunAsync(room, () => StartGameCoreAsync(room));

    public Task<string?> AdvancePhaseAsync(GameRoom room, GamePhase target) =>
        gate.RunAsync(room, async () =>
        {
            // An automatic transition may have won the race with the host's button click.
            if (room.Phase == target) return null;
            var error = await AdvancePhaseCoreAsync(room, target);
            if (error is null) await AdvanceCompletedPhasesCoreAsync(room);
            return error;
        });

    /// <summary>The current picker chooses a territory (base, conquest claim or expansion).</summary>
    public Task<string?> PickAsync(GameRoom room, Guid playerId, int territoryIndex) =>
        gate.RunAsync(room, async () =>
        {
            var result = room.TryPick(playerId, territoryIndex, time.GetUtcNow());
            if (!result.Ok)
            {
                return result.Error switch
                {
                    PickError.InvalidPick => ErrorCodes.InvalidPick,
                    PickError.InvalidPhase => ErrorCodes.InvalidPhase,
                    PickError.NoActivePick => ErrorCodes.NotYourPick,
                    _ => ErrorCodes.NotYourPick,
                };
            }

            await AfterPickCoreAsync(room, result);
            return null;
        });

    public Task OnPlayerRemovedAsync(GameRoom room, Guid playerId) =>
        gate.RunAsync(room, async () =>
        {
            var change = room.HandlePlayerRemoved(playerId, time.GetUtcNow());
            if (change is not null) await AfterMapChangeCoreAsync(room, change);
            await AdvanceCompletedPhasesCoreAsync(room);
            return true;
        });

    // ---- called by RoundCoordinator while it holds the gate ----

    /// <summary>Begins a Conquest claim stage: asks the first player to claim a tile.</summary>
    public async Task StartConquestClaimsCoreAsync(GameRoom room)
    {
        if (room.StartConquestClaims(time.GetUtcNow()) is { } pick)
            await PublishPickAsync(room, pick);
    }

    /// <summary>After a round closed with a map change and/or an Expansion pick, broadcast and ask the next picker.</summary>
    public async Task OnRoundResolvedCoreAsync(GameRoom room, ClosedRound closed)
    {
        if (closed.Change is { } change && change.Changed.Count > 0)
        {
            await recorder.TerritoriesChangedAsync(room.SessionId, ToRecords(change.Changed));
            await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(
                HubNames.Events.MapUpdated, new MapUpdate(change.Changed, change.Scoreboard));
        }
        else if (closed.Change is { } scoreOnly)
        {
            // Duel draw / defender bonus: no cells changed but scores did.
            await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(
                HubNames.Events.ScoreboardUpdated, scoreOnly.Scoreboard);
        }

        if (closed.ExpansionPick is { } pick)
            await PublishPickAsync(room, pick);
        await AdvanceCompletedPhasesCoreAsync(room);
    }

    // Caller holds the room command gate. Reuse normal transitions so events and persistence stay ordered.
    public async Task AdvanceCompletedPhasesCoreAsync(GameRoom room)
    {
        while (room.NextAutomaticPhase() is { } next)
        {
            if (await AdvancePhaseCoreAsync(room, next) is not null) break;
        }

        // Duel begins with the active player choosing an adjacent enemy territory.
        if (room.Phase == GamePhase.Duel && room.StartDuelTargetPick(time.GetUtcNow()) is { } duelPick)
            await PublishPickAsync(room, duelPick);
    }

    public async Task PublishDuelNominationCoreAsync(GameRoom room, DuelNomination nomination)
    {
        CancelTimer(room.Code, null);
        await hub.Clients.Group(GroupNames.Gm(room.Code)).SendAsync(
            HubNames.Events.DuelNominated,
            new DuelNominationDto(nomination.AttackerId, nomination.DefenderId, nomination.TargetTerritoryIndex));
    }

    public async Task SendPickStateCoreAsync(GameRoom room, IClientProxy caller, Guid? playerId)
    {
        if (room.CurrentPickView() is not { } pick) return;
        if (playerId == pick.Picker.Id)
            await caller.SendAsync(HubNames.Events.PickRequested, new PickRequest(pick.ValidTerritoryIndices, pick.Deadline, pick.Purpose));
        else
            await caller.SendAsync(HubNames.Events.PickWaiting, pick.Picker);
    }

    public void Cancel(string roomCode) => CancelTimer(roomCode, null);

    // ---- internals ----

    private async Task<string?> StartGameCoreAsync(GameRoom room)
    {
        var started = room.TryStartGame(time.GetUtcNow());
        if (!started.Ok) return Code(started.Error);

        logger.LogInformation("Room {Room}: game started, map {Rows}x{Cols}", room.Code, started.Map!.Rows, started.Map.Cols);
        await recorder.TerritoriesChangedAsync(room.SessionId, ToRecords(started.Map.Territories));
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PhaseChanged, GamePhase.BaseSelection);
        await PushStateToAllAsync(room);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(
            HubNames.Events.MapUpdated, new MapUpdate(started.Map.Territories, room.Scoreboard()));

        // Ask the first player for their base.
        if (room.CurrentPickView() is { } pick) await PublishPickAsync(room, pick);
        return null;
    }

    private async Task<string?> AdvancePhaseCoreAsync(GameRoom room, GamePhase target)
    {
        if (target == GamePhase.BaseSelection && room.Phase == GamePhase.Lobby)
            return await StartGameCoreAsync(room);

        var advanced = room.TryAdvancePhase(target, time.GetUtcNow());
        if (!advanced.Ok) return Code(advanced.Error);

        CancelTimer(room.Code, null);
        logger.LogInformation("Room {Room}: phase {Phase}", room.Code, advanced.Phase);
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.PhaseChanged, advanced.Phase);

        if (advanced.Phase == GamePhase.Results)
        {
            await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(HubNames.Events.GameFinished, room.BuildGameFinished());
            await recorder.SessionEndedAsync(room.SessionId);
        }

        return null;
    }

    // A pick resolved: broadcast the map change and ask the next picker, or tell the round coordinator claims are in.
    private async Task AfterPickCoreAsync(GameRoom room, PickResult result)
    {
        if (result.Change is { } change) await AfterMapChangeCoreAsync(room, change);
        if (result.Nomination is { } nomination) await PublishDuelNominationCoreAsync(room, nomination);

        // Conquest claim stage finished: let the round coordinator open the question.
        if (result.AllClaimsIn && OnClaimsReady is { } ready) await ready(room);
        await AdvanceCompletedPhasesCoreAsync(room);
    }

    private async Task AfterMapChangeCoreAsync(GameRoom room, MapChange change)
    {
        if (change.Changed.Count > 0)
        {
            await recorder.TerritoriesChangedAsync(room.SessionId, ToRecords(change.Changed));
            await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(
                HubNames.Events.MapUpdated, new MapUpdate(change.Changed, change.Scoreboard));
        }

        if (change.NextPick is not { } next)
        {
            CancelTimer(room.Code, null);
            return;
        }

        bool alreadyAsked;
        lock (_gate) alreadyAsked = _timers.TryGetValue(room.Code, out var t) && t.Seq == next.Seq;
        if (!alreadyAsked) await PublishPickAsync(room, next);
    }

    private async Task PublishPickAsync(GameRoom room, PickView pick)
    {
        SchedulePickTimeout(room, pick);

        var pickerConnections = registry.GetPlayerConnections(room.Code, [pick.Picker.Id]);
        await hub.Clients.Clients(pickerConnections).SendAsync(
            HubNames.Events.PickRequested, new PickRequest(pick.ValidTerritoryIndices, pick.Deadline, pick.Purpose));
        await hub.Clients.GroupExcept(GroupNames.Room(room.Code), pickerConnections).SendAsync(
            HubNames.Events.PickWaiting, pick.Picker);
    }

    private async Task PushStateToAllAsync(GameRoom room)
    {
        var now = time.GetUtcNow();
        foreach (var connection in registry.GetRoomConnections(room.Code))
        {
            var forHost = connection.Role == Auth.Roles.GameMaster;
            await hub.Clients.Client(connection.ConnectionId).SendAsync(
                HubNames.Events.RoomState, room.Snapshot(now, connection.PlayerId, forHost));
        }
    }

    private void SchedulePickTimeout(GameRoom room, PickView pick)
    {
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_timers.Remove(room.Code, out var previous)) previous.Cts.Cancel();
            _timers[room.Code] = (pick.Seq, cts);
        }

        var delay = pick.Deadline - time.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, time, cts.Token);
                await gate.RunAsync(room, () => PickTimedOutCoreAsync(room, pick.Seq));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogError(ex, "Auto-pick in room {Room} failed", room.Code); }
        });
    }

    private async Task<bool> PickTimedOutCoreAsync(GameRoom room, long seq)
    {
        var result = room.TryAutoPick(seq, time.GetUtcNow());
        if (!result.Ok) return false;

        logger.LogInformation("Room {Room}: pick timed out, territory assigned at random", room.Code);
        await AfterPickCoreAsync(room, result);
        return true;
    }

    private void CancelTimer(string roomCode, long? seq)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (_timers.TryGetValue(roomCode, out var entry) && (seq is null || entry.Seq == seq))
            {
                _timers.Remove(roomCode);
                cts = entry.Cts;
            }
        }

        cts?.Cancel();
    }

    private static IReadOnlyList<TerritoryRecord> ToRecords(IReadOnlyList<TerritoryDto> cells) =>
        cells.Select(c => new TerritoryRecord(c.Index, c.Row, c.Col, c.Value, c.IsBase, c.Health, c.OwnerPlayerId)).ToList();

    private static string? Code(PhaseError error) => error switch
    {
        PhaseError.None => null,
        PhaseError.InvalidPhase => ErrorCodes.InvalidPhase,
        PhaseError.InvalidTransition => ErrorCodes.InvalidTransition,
        PhaseError.NotEnoughPlayers => ErrorCodes.NotEnoughPlayers,
        PhaseError.RoundInProgress => ErrorCodes.RoundInProgress,
        PhaseError.PickInProgress => ErrorCodes.RoundInProgress,
        PhaseError.BaseSelectionIncomplete => ErrorCodes.BaseSelectionIncomplete,
        PhaseError.PhaseNotComplete => ErrorCodes.PhaseNotComplete,
        PhaseError.InvalidSettings => ErrorCodes.InvalidSettings,
        _ => ErrorCodes.InvalidPhase,
    };
}
