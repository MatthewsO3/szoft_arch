using Microsoft.AspNetCore.SignalR;
using QuizGame.Contracts;
using QuizGame.Server.Domain;
using QuizGame.Server.Hubs;

namespace QuizGame.Server.Services;

/// <summary>
/// Phase changes, room settings and the base-selection flow (plan §4.2, §7.1): starts the game and generates the
/// map, advances the phase, asks each player in ranking order for a base, enforces the pick timeout (random valid
/// territory) and pushes the resulting <c>PhaseChanged</c> / <c>MapUpdated</c> / <c>PickRequested</c> / <c>PickWaiting</c> events.
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

    // ---- hub commands ----

    /// <summary>Host changes the room settings (Lobby only). Returns the error code, or null.</summary>
    public Task<string?> UpdateSettingsAsync(GameRoom room, RoomSettings settings) =>
        gate.RunAsync(room, () => Task.FromResult(Code(room.TryUpdateSettings(settings))));

    /// <summary>Lobby → BaseSelection: generates the map and tells everybody.</summary>
    public Task<string?> StartGameAsync(GameRoom room) =>
        gate.RunAsync(room, () => StartGameCoreAsync(room));

    /// <summary>Host moves to the next phase. Returns the error code, or null.</summary>
    public Task<string?> AdvancePhaseAsync(GameRoom room, GamePhase target) =>
        gate.RunAsync(room, () => AdvancePhaseCoreAsync(room, target));

    /// <summary>The current picker chooses a base territory.</summary>
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
                    _ => ErrorCodes.NotYourPick,
                };
            }

            await AfterMapChangeCoreAsync(room, result.Change!);
            return null;
        });

    /// <summary>A player was kicked or left: free their territories and move on if they were up to pick.</summary>
    public Task OnPlayerRemovedAsync(GameRoom room, Guid playerId) =>
        gate.RunAsync(room, async () =>
        {
            var change = room.HandlePlayerRemoved(playerId, time.GetUtcNow());
            if (change is not null) await AfterMapChangeCoreAsync(room, change);
            return true;
        });

    // ---- called by RoundCoordinator while it holds the gate ----

    /// <summary>A round closed: after the base-order round the first player is asked for a base.</summary>
    public async Task OnRoundClosedCoreAsync(GameRoom room, ClosedRound closed)
    {
        if (closed.Record.Kind != RoundKind.BaseOrder) return;
        if (room.CurrentPickView() is { } pick)
            await PublishPickAsync(room, pick);
    }

    /// <summary>Resends the pending pick to a (re)attached connection, right after its <c>RoomState</c>.</summary>
    public async Task SendPickStateCoreAsync(GameRoom room, IClientProxy caller, Guid? playerId)
    {
        if (room.CurrentPickView() is not { } pick) return;
        if (playerId == pick.Picker.Id)
            await caller.SendAsync(HubNames.Events.PickRequested, new PickRequest(pick.ValidTerritoryIndices, pick.Deadline));
        else
            await caller.SendAsync(HubNames.Events.PickWaiting, pick.Picker);
    }

    /// <summary>Stops the pick timer of a room that is being closed or removed.</summary>
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

    // A base was assigned (picked or auto) or released (player left): tell everybody and ask the next picker.
    private async Task AfterMapChangeCoreAsync(GameRoom room, MapChange change)
    {
        await recorder.TerritoriesChangedAsync(room.SessionId, ToRecords(change.Changed));
        await hub.Clients.Group(GroupNames.Room(room.Code)).SendAsync(
            HubNames.Events.MapUpdated, new MapUpdate(change.Changed, change.Scoreboard));

        if (change.NextPick is not { } next)
        {
            CancelTimer(room.Code, null);
            return;
        }

        // Same picker as before (e.g. somebody else left): keep their question and timer as they are.
        bool alreadyAsked;
        lock (_gate) alreadyAsked = _timers.TryGetValue(room.Code, out var t) && t.Seq == next.Seq;
        if (!alreadyAsked) await PublishPickAsync(room, next);
    }

    private async Task PublishPickAsync(GameRoom room, PickView pick)
    {
        SchedulePickTimeout(room, pick);

        var pickerConnections = registry.GetPlayerConnections(room.Code, [pick.Picker.Id]);
        await hub.Clients.Clients(pickerConnections).SendAsync(
            HubNames.Events.PickRequested, new PickRequest(pick.ValidTerritoryIndices, pick.Deadline));
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
            catch (OperationCanceledException)
            {
                // picked in time, phase changed or room removed
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-pick in room {Room} failed", room.Code);
            }
        });
    }

    private async Task<bool> PickTimedOutCoreAsync(GameRoom room, long seq)
    {
        var result = room.TryAutoPick(seq, time.GetUtcNow());
        if (!result.Ok) return false; // the player picked just in time, or the pick is gone

        logger.LogInformation("Room {Room}: pick timed out, territory assigned at random", room.Code);
        await AfterMapChangeCoreAsync(room, result.Change!);
        return true;
    }

    // Only the timer of the given pick is cancelled (null = whatever is running).
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
        cells.Select(c => new TerritoryRecord(c.Index, c.Row, c.Col, c.Value, c.IsBase, c.OwnerPlayerId)).ToList();

    private static string? Code(PhaseError error) => error switch
    {
        PhaseError.None => null,
        PhaseError.InvalidPhase => ErrorCodes.InvalidPhase,
        PhaseError.InvalidTransition => ErrorCodes.InvalidTransition,
        PhaseError.NotEnoughPlayers => ErrorCodes.NotEnoughPlayers,
        PhaseError.RoundInProgress => ErrorCodes.RoundInProgress,
        PhaseError.BaseSelectionIncomplete => ErrorCodes.BaseSelectionIncomplete,
        PhaseError.InvalidSettings => ErrorCodes.InvalidSettings,
        _ => ErrorCodes.InvalidPhase,
    };
}
